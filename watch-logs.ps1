# PowerShell script to monitor runtime.log in real-time
# Usage: .\watch-logs.ps1

Write-Host "🔍 Code Review Agent - Runtime Log Monitor" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

$logFile = "runtime.log"

if (-not (Test-Path $logFile)) {
    Write-Host "⚠️  Log file '$logFile' not found." -ForegroundColor Yellow
    Write-Host "   Make sure the Code Review Agent is running." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "💡 Start the agent with:" -ForegroundColor Green
    Write-Host "   dotnet run --web" -ForegroundColor White
    Write-Host ""
    exit
}

Write-Host "📁 Monitoring: $((Get-Item $logFile).FullName)" -ForegroundColor Green
Write-Host "🔄 Press Ctrl+C to stop monitoring" -ForegroundColor Yellow
Write-Host ""

try {
    # Show existing content first
    Write-Host "📖 Current log content:" -ForegroundColor Magenta
    Write-Host "───────────────────────" -ForegroundColor Magenta
    Get-Content $logFile
    Write-Host ""
    Write-Host "🔴 LIVE LOG (new entries will appear below):" -ForegroundColor Red
    Write-Host "─────────────────────────────────────────────" -ForegroundColor Red
    
    # Start monitoring new content
    Get-Content $logFile -Wait -Tail 0
}
catch {
    Write-Host "❌ Error monitoring log file: $($_.Exception.Message)" -ForegroundColor Red
}