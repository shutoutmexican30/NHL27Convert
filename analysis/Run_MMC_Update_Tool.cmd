@echo off
setlocal
cd /d "%~dp0"
where py >nul 2>&1
if %errorlevel%==0 (
    py -3 mmc_online_block_updater.py %*
) else (
    python mmc_online_block_updater.py %*
)
if errorlevel 1 pause
endlocal
