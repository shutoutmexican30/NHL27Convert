# Publishes Center Ice Modding Suite as a shareable Windows build: self-contained (no .NET install needed on the
# player's PC), win-x64, Release, no PDBs. Output: dist\CIMS-<version>-win-x64\ (run CenterIceModdingSuite.exe) and
# dist\CIMS-<version>-win-x64.zip to hand out. Works from any clone location; needs the .NET 8 SDK on the build PC only.
#
# Not bundled (users supply their own): oo2core_9_win64.dll (drop it next to the exe, or keep FMT under <drive>:\Tools),
# the NHL24Kit worker runtime, and the games. WebView2 (scorebug / viewer preview) uses the Edge WebView2 runtime that
# ships with Windows 10/11.
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$app = Join-Path $root 'src\Suite.App\Suite.App.csproj'

# 1. The standalone mod installer the suite carries in its installer\ folder.
& (Join-Path $PSScriptRoot 'publish-installer.ps1')

# 2. The suite itself.
$version = ([xml](Get-Content $app)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { $version = '0.0.0' }
$name = "CIMS-$version-win-x64"
$dist = Join-Path $root 'dist'
$out = Join-Path $dist $name
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish $app -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=false `
    -p:PublishReadyToRun=true -p:DebugType=none -p:DebugSymbols=false -p:SatelliteResourceLanguages=en -o $out
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

$exe = Join-Path $out 'CenterIceModdingSuite.exe'
if (-not (Test-Path $exe)) { throw "missing $exe" }
# Nothing from this PC may ride along: drop any settings/log/cache a dev run left in the output.
Get-ChildItem $out -Recurse -Include 'settings.json', 'suite.log', '*.pdb' -ErrorAction SilentlyContinue | Remove-Item -Force

$zip = Join-Path $dist "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Optimal
Get-Item $exe, $zip | Select-Object FullName, Length
