@echo off
setlocal
cd /d "%~dp0"
where py >nul 2>&1
if %errorlevel%==0 (
    py -3 game_update_compatibility_builder.py %*
) else (
    python game_update_compatibility_builder.py %*
)
if errorlevel 1 pause
endlocal
