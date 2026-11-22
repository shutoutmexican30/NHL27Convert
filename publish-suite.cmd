@echo off
rem Builds the shareable suite: dist\CIMS-<version>-win-x64\CenterIceModdingSuite.exe and dist\CIMS-<version>-win-x64.zip
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\publish-suite.ps1" %*
pause
