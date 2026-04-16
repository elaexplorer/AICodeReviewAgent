@echo off
:: Start the local Claude review server (Flask, port 5010)
:: Managed by NSSM as a Windows service — local_claude_server.py loads .env itself.

set REPO=C:\Users\elavarasid\workspace\mscs\CodeReviewAIAgent
set PYTHON=C:\Python311\python.exe

cd /d "%REPO%"

:: Keep restarting if it crashes.
:: No >> redirect here — NSSM captures stdout via AppStdout,
:: and local_claude_server.py has its own FileHandler for local_claude_server.log.
:loop
echo [%date% %time%] Starting local_claude_server.py...
"%PYTHON%" scripts\local_claude_server.py --port 5010
echo [%date% %time%] Server exited — restarting in 10s...
timeout /t 10 /nobreak >nul
goto loop
