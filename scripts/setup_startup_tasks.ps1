# Run this script once as Administrator to register auto-start tasks.
# Right-click this file → "Run with PowerShell" (as Admin), or:
#   Start-Process powershell -Verb RunAs -ArgumentList "-File setup_startup_tasks.ps1"

$repo = "C:\Users\elavarasid\workspace\mscs\CodeReviewAIAgent"

# ── Task 1: Local Claude Flask server ──────────────────────────────────────
$a1 = New-ScheduledTaskAction `
    -Execute  "cmd.exe" `
    -Argument "/c `"$repo\scripts\start_local_claude_server.bat`""

$t1 = New-ScheduledTaskTrigger -AtStartup

$s1 = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit (New-TimeSpan -Hours 0) `
    -RestartCount 5 `
    -RestartInterval (New-TimeSpan -Minutes 2) `
    -StartWhenAvailable

Register-ScheduledTask `
    -TaskName "ClaudeReviewServer" `
    -Action   $a1 `
    -Trigger  $t1 `
    -Settings $s1 `
    -RunLevel Highest `
    -Force

Write-Host "Registered: ClaudeReviewServer"

# ── Task 2: devtunnel ─────────────────────────────────────────────────────
$a2 = New-ScheduledTaskAction `
    -Execute  "cmd.exe" `
    -Argument "/c `"$repo\scripts\start_devtunnel.bat`""

$t2 = New-ScheduledTaskTrigger -AtStartup

$s2 = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit (New-TimeSpan -Hours 0) `
    -RestartCount 5 `
    -RestartInterval (New-TimeSpan -Minutes 2) `
    -StartWhenAvailable

Register-ScheduledTask `
    -TaskName "ClaudeReviewTunnel" `
    -Action   $a2 `
    -Trigger  $t2 `
    -Settings $s2 `
    -RunLevel Highest `
    -Force

Write-Host "Registered: ClaudeReviewTunnel"
Write-Host ""
Write-Host "Done. Both tasks will start automatically on next boot."
Write-Host "To start them now without rebooting:"
Write-Host "  Start-ScheduledTask -TaskName ClaudeReviewServer"
Write-Host "  Start-ScheduledTask -TaskName ClaudeReviewTunnel"
