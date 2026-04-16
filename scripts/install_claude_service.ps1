# Run this script as Administrator to install/configure the ClaudeReviewServer NSSM service.
# Usage: Right-click -> Run with PowerShell (as Administrator)

$nssm = "C:\tools\nssm\nssm-2.24\win64\nssm.exe"
$bat  = "C:\Users\elavarasid\workspace\mscs\CodeReviewAIAgent\scripts\start_local_claude_server.bat"
$dir  = "C:\Users\elavarasid\workspace\mscs\CodeReviewAIAgent"
$log  = "C:\Users\elavarasid\workspace\mscs\CodeReviewAIAgent\local_claude_server.log"
$pyPath = "C:\Python311;C:\Python311\Scripts"

Write-Host "=== ClaudeReviewServer NSSM Setup ===" -ForegroundColor Cyan

# Stop and remove if already exists
$svc = Get-Service -Name "ClaudeReviewServer" -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host "Stopping existing service..." -ForegroundColor Yellow
    & $nssm stop ClaudeReviewServer confirm
    & $nssm remove ClaudeReviewServer confirm
    Start-Sleep -Seconds 2
}

# Install
Write-Host "Installing service..." -ForegroundColor Yellow
& $nssm install ClaudeReviewServer $bat

# Configure
Write-Host "Configuring service..." -ForegroundColor Yellow
& $nssm set ClaudeReviewServer AppDirectory $dir
& $nssm set ClaudeReviewServer Start SERVICE_AUTO_START
& $nssm set ClaudeReviewServer AppRestartDelay 10000

# Python PATH so LocalSystem can find python.exe
$systemPath = [System.Environment]::GetEnvironmentVariable("PATH", "Machine")
& $nssm set ClaudeReviewServer AppEnvironmentExtra "PATH=$pyPath;$systemPath"

# Log to the same file (append mode = 4)
& $nssm set ClaudeReviewServer AppStdout $log
& $nssm set ClaudeReviewServer AppStderr $log
& $nssm set ClaudeReviewServer AppStdoutCreationDisposition 4
& $nssm set ClaudeReviewServer AppStderrCreationDisposition 4

# Start it
Write-Host "Starting service..." -ForegroundColor Yellow
& $nssm start ClaudeReviewServer
Start-Sleep -Seconds 4

# Verify
$svc = Get-Service -Name "ClaudeReviewServer"
if ($svc.Status -eq "Running") {
    Write-Host "ClaudeReviewServer is RUNNING" -ForegroundColor Green
} else {
    Write-Host "Service status: $($svc.Status) — check Event Viewer for details" -ForegroundColor Red
}

Write-Host ""
Write-Host "Done. To manage the service:" -ForegroundColor Cyan
Write-Host "  nssm status ClaudeReviewServer"
Write-Host "  nssm restart ClaudeReviewServer"
Write-Host "  nssm stop ClaudeReviewServer"
