# start_local_review_services.ps1
# Starts the local Claude review server and devtunnel host.
# Designed to be run by Task Scheduler at logon.
# Each process is restarted automatically if it exits.

param(
    [int]$ServerPort = 5010,
    [string]$TunnelId = "claude-review"
)

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Split-Path -Parent $ScriptDir
$LogDir    = "$RepoRoot\logs"

if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir | Out-Null }

$ServerLog = "$LogDir\local_claude_server.log"
$TunnelLog = "$LogDir\devtunnel.log"

function Start-Watched {
    param([string]$Name, [string]$Exe, [string[]]$Args, [string]$WorkDir, [string]$Log)
    while ($true) {
        $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
        Add-Content $Log "[$timestamp] Starting $Name..."
        $proc = Start-Process -FilePath $Exe -ArgumentList $Args `
            -WorkingDirectory $WorkDir `
            -RedirectStandardOutput $Log -RedirectStandardError $Log `
            -NoNewWindow -PassThru
        $proc.WaitForExit()
        $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
        Add-Content $Log "[$timestamp] $Name exited (code $($proc.ExitCode)) — restarting in 10s..."
        Start-Sleep -Seconds 10
    }
}

# Start tunnel in background thread
$tunnelJob = Start-Job -ScriptBlock ${function:Start-Watched} -ArgumentList `
    "devtunnel", "devtunnel", @("host", $TunnelId, "--allow-anonymous"), $RepoRoot, $TunnelLog

Write-Host "devtunnel job started (id=$($tunnelJob.Id))"

# Run local server in foreground (Task Scheduler keeps this process alive)
Start-Watched -Name "local_claude_server" `
    -Exe "python" `
    -Args @("$ScriptDir\local_claude_server.py") `
    -WorkDir $RepoRoot `
    -Log $ServerLog
