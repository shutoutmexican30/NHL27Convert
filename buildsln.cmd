@echo off
cd /d "%~dp0"
dotnet build NhlSuite.sln -c Debug -v m 2>&1
