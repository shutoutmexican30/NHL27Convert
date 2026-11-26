@echo off
cd /d C:\dev\NHL27Convert\tools\RinkSharp
"C:\Program Files\dotnet\dotnet.exe" build -c Release %*
