@echo off
:: Start the local Claude review server (Flask, port 5010)
:: This script is registered as a Windows Task Scheduler startup task.

set REPO=C:\Users\elavarasid\workspace\mscs\CodeReviewAIAgent

:: Load .env vars into the current session
for /f "usebackq tokens=1,* delims==" %%A in ("%REPO%\.env") do (
    if not "%%A"=="" if not "%%A:~0,1%"=="#" set "%%A=%%B"
)

cd /d "%REPO%"

:: Keep restarting if it crashes
:loop
echo [%date% %time%] Starting local_claude_server.py...
python scripts\local_claude_server.py --port 5010 >> "%REPO%\local_claude_server.log" 2>&1
echo [%date% %time%] Server exited — restarting in 10s...
timeout /t 10 /nobreak >nul
goto loop
