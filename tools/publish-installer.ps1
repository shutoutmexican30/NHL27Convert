# Publishes the standalone mod installer (self-contained, single file, no .NET needed on the player's PC)
# to src\Suite.ModInstaller\bin\publish, where the suite build picks it up into its installer\ folder.
$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot '..\src\Suite.ModInstaller\Suite.ModInstaller.csproj'
$out = Join-Path $PSScriptRoot '..\src\Suite.ModInstaller\bin\publish'
dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
Get-Item (Join-Path $out 'CIMS-Mod-Installer.exe') | Select-Object FullName, Length
