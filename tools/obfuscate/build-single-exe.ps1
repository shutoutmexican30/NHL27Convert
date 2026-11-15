# Builds ONE self-contained, obfuscated CenterIceModdingSuite.exe that runs on any Windows x64 PC with nothing
# installed. Pipeline: build self-contained -> obfuscate the managed assemblies in place -> bundle into a single file
# with --no-build so the bundle contains the obfuscated DLLs.
param(
    [string]$Config = 'Release',
    [string]$Rid = 'win-x64',
    [string]$Obfuscar = (Join-Path $env:USERPROFILE '.nuget\packages\obfuscar\2.2.40\tools\Obfuscar.Console.exe')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$proj = Join-Path $repo 'src\Suite.App\Suite.App.csproj'
$build = Join-Path $repo "src\Suite.App\bin\$Config\net8.0-windows\$Rid"
$out = Join-Path $repo 'dist\suite-single-exe'
if (-not (Test-Path $Obfuscar)) { throw "Obfuscar not found at $Obfuscar" }

$out = Join-Path $repo 'dist\suite-single-exe'
Write-Host "1/4  Publishing self-contained single file ($Rid) to create the bundle host ..."
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
& dotnet publish $proj -c $Config -r $Rid --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $out -v q
if ($LASTEXITCODE -ne 0) { throw 'initial publish failed' }
if (-not (Test-Path (Join-Path $build 'CenterIceModdingSuite.dll'))) { throw "no self-contained build at $build" }

Write-Host "2/4  Obfuscating assemblies in the build output ..."
$work = Join-Path $repo 'dist\_obf-single-work'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
$renameModules = 'Suite.Gameplay', 'Suite.Scorebug', 'Suite.Sounds', 'Suite.Textures', 'Suite.ModPackage'
$skipModules = 'CenterIceModdingSuite', 'Suite.Convert27', 'Suite.Core', 'Suite.Rosters'
$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine("<?xml version='1.0'?>")
[void]$sb.AppendLine("<Obfuscator>")
[void]$sb.AppendLine("  <Var name='InPath' value='$build' />")
[void]$sb.AppendLine("  <Var name='OutPath' value='$work' />")
[void]$sb.AppendLine("  <Var name='KeepPublicApi' value='true' />")
[void]$sb.AppendLine("  <Var name='HidePrivateApi' value='true' />")
[void]$sb.AppendLine("  <Var name='HideStrings' value='true' />")
[void]$sb.AppendLine("  <Var name='RenameProperties' value='false' />")
[void]$sb.AppendLine("  <Var name='RenameEvents' value='false' />")
[void]$sb.AppendLine("  <Var name='RenameFields' value='false' />")
[void]$sb.AppendLine("  <Var name='ReuseNames' value='true' />")
[void]$sb.AppendLine("  <Var name='SuppressIldasm' value='true' />")
foreach ($m in $renameModules) { [void]$sb.AppendLine("  <Module file='`$(InPath)\$m.dll' />") }
foreach ($m in $skipModules) {
    [void]$sb.AppendLine("  <Module file='`$(InPath)\$m.dll'>")
    [void]$sb.AppendLine("    <SkipType name='*' skipMethods='true' skipFields='true' skipProperties='true' skipEvents='true' skipStringHiding='false' />")
    [void]$sb.AppendLine("  </Module>")
}
[void]$sb.AppendLine("</Obfuscator>")
$xml = Join-Path $PSScriptRoot 'single.generated.xml'
$sb.ToString() | Set-Content -LiteralPath $xml -Encoding UTF8
& $Obfuscar $xml
if ($LASTEXITCODE -ne 0) { throw "Obfuscar failed ($LASTEXITCODE)" }
foreach ($m in ($renameModules + $skipModules)) {
    $o = Join-Path $work "$m.dll"
    if (Test-Path $o) { Copy-Item $o (Join-Path $build "$m.dll") -Force }
}
Remove-Item $work -Recurse -Force

Write-Host "3/4  Re-bundling with --no-build so the obfuscated DLLs go into the single file ..."
Remove-Item (Join-Path $out '*') -Recurse -Force -ErrorAction SilentlyContinue
& dotnet publish $proj -c $Config -r $Rid --self-contained true --no-build `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $out -v q
if ($LASTEXITCODE -ne 0) { throw 'publish/bundle failed' }

$exe = Join-Path $out 'CenterIceModdingSuite.exe'
Write-Host "4/4  Done."
"{0}  ({1:N0} MB)" -f $exe, ((Get-Item $exe).Length / 1MB)
