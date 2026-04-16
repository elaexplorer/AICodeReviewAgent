@echo off
:: Keeps the devtunnel host running — restarts on exit.
:: Tunnel "claude-review" must already exist:
::   devtunnel create -n claude-review -p 5010 --allow-anonymous

:loop
echo [%date% %time%] Starting devtunnel host claude-review...
devtunnel host claude-review --allow-anonymous
echo [%date% %time%] devtunnel exited -- restarting in 10s...
timeout /t 10 /nobreak >/dev/null
goto loop
