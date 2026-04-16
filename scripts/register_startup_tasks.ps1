<#
.SYNOPSIS
  Fix auto-start for the local Claude review server and devtunnel.

  Run once as Administrator.  Fixes two pre-existing bugs:
    1. ClaudeReviewServer NSSM service ran as LocalSystem which cannot see
       claude.exe (a per-user install).  Adds the user PATH to the service env.
    2. ClaudeReviewTunnel scheduled task had a BootTrigger + InteractiveToken
       (impossible combo — never fired) and pointed to a missing bat file.
       Recreated with a LogonTrigger so it fires when the user logs in.
#>

$ErrorActionPreference = "Stop"
$repo    = "C:\Users\elavarasid\workspace\mscs\CodeReviewAIAgent"
$scripts = "$repo\scripts"
$nssm    = "C:\tools\nssm\nssm-2.24\win64\nssm.exe"
$user    = $env:USERNAME   # elavarasid

Write-Host "=== Claude Review Auto-Start Fix ===" -ForegroundColor Cyan

# ---------------------------------------------------------------------------
# 1. Fix NSSM service: add user's claude path to AppEnvironmentExtra
# ---------------------------------------------------------------------------
Write-Host "`n[1/2] Patching ClaudeReviewServer service PATH..." -ForegroundColor Yellow

$claudeDir   = "C:\Users\elavarasid\.local\bin"
$pythonPaths = "C:\Python311;C:\Python311\Scripts"
$syspath     = [System.Environment]::GetEnvironmentVariable("PATH", "Machine")

& $nssm set ClaudeReviewServer AppEnvironmentExtra "PATH=$claudeDir;$pythonPaths;$syspath"

# Restart the service so it picks up the new env
Write-Host "  Restarting service..."
& $nssm restart ClaudeReviewServer
Start-Sleep -Seconds 4
$svc = Get-Service -Name ClaudeReviewServer
$color = if ($svc.Status -eq "Running") { "Green" } else { "Red" }
Write-Host "  Service status: $($svc.Status)" -ForegroundColor $color

# ---------------------------------------------------------------------------
# 2. Fix devtunnel task: delete old broken task, create with LogonTrigger
# ---------------------------------------------------------------------------
Write-Host "`n[2/2] Recreating ClaudeReviewTunnel scheduled task..." -ForegroundColor Yellow

# Remove old (broken) task
schtasks /delete /tn ClaudeReviewTunnel /f 2>$null | Out-Null

$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Keeps the devtunnel claude-review host running so the remote code-review container can reach the local Claude server on port 5010.</Description>
    <URI>\ClaudeReviewTunnel</URI>
  </RegistrationInfo>
  <Principals>
    <Principal id="Author">
      <UserId>$env:USERDOMAIN\$user</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RestartOnFailure>
      <Count>999</Count>
      <Interval>PT1M</Interval>
    </RestartOnFailure>
  </Settings>
  <Triggers>
    <LogonTrigger>
      <UserId>$env:USERDOMAIN\$user</UserId>
      <Delay>PT10S</Delay>
    </LogonTrigger>
  </Triggers>
  <Actions Context="Author">
    <Exec>
      <Command>cmd.exe</Command>
      <Arguments>/c "$scripts\start_devtunnel.bat" &gt;&gt; "$repo\logs\devtunnel.log" 2&gt;&amp;1</Arguments>
      <WorkingDirectory>$repo</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
"@

$tmpXml = "$env:TEMP\ClaudeReviewTunnel.xml"
$xml | Out-File -FilePath $tmpXml -Encoding Unicode
schtasks /create /tn ClaudeReviewTunnel /xml $tmpXml /f
Remove-Item $tmpXml

# Make logs dir
New-Item -ItemType Directory -Path "$repo\logs" -Force | Out-Null

# Start it now (don't wait for next logon)
Write-Host "  Starting task now..."
schtasks /run /tn ClaudeReviewTunnel
Start-Sleep -Seconds 3
$status = (schtasks /query /tn ClaudeReviewTunnel /fo LIST | Select-String "Status:").ToString().Trim()
Write-Host "  $status" -ForegroundColor Green

Write-Host "`nDone. Both services will auto-start on next logon." -ForegroundColor Cyan
Write-Host "  Flask server : managed by NSSM (ClaudeReviewServer, AUTO_START)"
Write-Host "  devtunnel    : managed by Task Scheduler (ClaudeReviewTunnel, LogonTrigger)"
