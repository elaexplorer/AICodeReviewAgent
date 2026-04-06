@echo off
:: Start the devtunnel for the local Claude review server
:: Tunnel: claude-review.usw2  Port: 5010
:: URL:    https://claude-review-5010.usw2.devtunnels.ms

set REPO=C:\Users\elavarasid\workspace\mscs\CodeReviewAIAgent

:: Wait for Flask server to be ready
:waitloop
curl -s http://localhost:5010/health >nul 2>&1
if errorlevel 1 (
    echo [%date% %time%] Waiting for Flask server on port 5010...
    timeout /t 5 /nobreak >nul
    goto waitloop
)
echo [%date% %time%] Flask server ready. Starting devtunnel...

:: Keep restarting if it crashes
:loop
devtunnel host claude-review >> "%REPO%\devtunnel.log" 2>&1
echo [%date% %time%] devtunnel exited — restarting in 10s...
timeout /t 10 /nobreak >nul
goto loop
