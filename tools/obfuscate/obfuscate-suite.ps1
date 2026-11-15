# Obfuscates the built Center Ice Modding Suite with Obfuscar.
#   - Encrypts every string literal in all suite assemblies (hides paths, messages, file-format knowledge).
#   - Renames private/internal members of the no-XAML library assemblies; public API is kept so WPF data
#     binding, reflection over SuiteSettings and JSON (de)serialization keep working.
#   - The two XAML assemblies (CenterIceModdingSuite.dll, Suite.Convert27.dll) are string-encrypted only, not
#     renamed, because WPF looks their event handlers and types up by name through BAML.
# Output: a self-contained obfuscated copy of the app under <repo>\dist\suite-obfuscated.
param(
    [string]$Config = 'Release',
    [string]$Obfuscar = (Join-Path $env:USERPROFILE '.nuget\packages\obfuscar\2.2.40\tools\Obfuscar.Console.exe')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$bin = Join-Path $repo "src\Suite.App\bin\$Config\net8.0-windows"
if (-not (Test-Path (Join-Path $bin 'CenterIceModdingSuite.dll'))) { throw "Build the suite first: dotnet build src\Suite.App -c $Config" }
if (-not (Test-Path $Obfuscar)) { throw "Obfuscar not found at $Obfuscar" }

$dist = Join-Path $repo 'dist\suite-obfuscated'
$work = Join-Path $repo 'dist\_obf-work'
foreach ($d in $dist, $work) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
Write-Host "Copying build to $dist ..."
Copy-Item $bin $dist -Recurse

# Libraries with no XAML and no constructor-bound JSON models: rename private/internal members + encrypt strings.
$renameModules = 'Suite.Gameplay', 'Suite.Scorebug', 'Suite.Sounds', 'Suite.Textures', 'Suite.ModPackage'
# Encrypt strings but keep every name:
#   - CenterIceModdingSuite, Suite.Convert27: WPF resolves XAML event handlers and types by name through BAML.
#   - Suite.Core, Suite.Rosters: System.Text.Json binds their many positional-record models (Stored, Snapshot,
#     BridgeRow, ApiPlayer, ...) through constructor parameter names, which renaming nulls.
$skipModules = 'CenterIceModdingSuite', 'Suite.Convert27', 'Suite.Core', 'Suite.Rosters'

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine("<?xml version='1.0'?>")
[void]$sb.AppendLine("<Obfuscator>")
[void]$sb.AppendLine("  <Var name='InPath' value='$dist' />")
[void]$sb.AppendLine("  <Var name='OutPath' value='$work' />")
[void]$sb.AppendLine("  <Var name='KeepPublicApi' value='true' />")
[void]$sb.AppendLine("  <Var name='HidePrivateApi' value='true' />")
[void]$sb.AppendLine("  <Var name='HideStrings' value='true' />")
# Properties and fields are kept: System.Text.Json binds settings.json and cached indexes by member name, and
# SettingsPage shows SuiteSettings property names as labels. Methods, parameters and private types still rename.
[void]$sb.AppendLine("  <Var name='RenameProperties' value='false' />")
[void]$sb.AppendLine("  <Var name='RenameEvents' value='false' />")
[void]$sb.AppendLine("  <Var name='RenameFields' value='false' />")
# Keep method parameter names: System.Text.Json binds positional records (Snapshot, Stored, BridgeRow, ...) through
# their constructor parameter names, and stripping them throws at load.
[void]$sb.AppendLine("  <Var name='RenameParameters' value='false' />")
[void]$sb.AppendLine("  <Var name='KeepParameterNames' value='true' />")
[void]$sb.AppendLine("  <Var name='ReuseNames' value='true' />")
[void]$sb.AppendLine("  <Var name='SuppressIldasm' value='true' />")
# Obfuscar must resolve framework assemblies (System.Text.Json etc.) that a framework-dependent app does not copy locally.
$shared = 'Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App', 'Microsoft.AspNetCore.App'
foreach ($s in $shared) {
    $p = Get-ChildItem (Join-Path $env:ProgramFiles "dotnet\shared\$s") -Directory -ErrorAction SilentlyContinue |
        Where-Object Name -like '8.*' | Sort-Object Name -Descending | Select-Object -First 1
    if ($p) { [void]$sb.AppendLine("  <AssemblySearchPath path='$($p.FullName)' />") }
}
foreach ($m in $renameModules) {
    [void]$sb.AppendLine("  <Module file='`$(InPath)\$m.dll'>")
    # Leave compiler-generated anonymous types alone: System.Text.Json reads their constructor parameter names, which
    # renaming would null (JsonSerializer.Serialize(new { ... })).
    [void]$sb.AppendLine("    <SkipType name='.*AnonymousType.*' skipMethods='true' skipFields='true' skipProperties='true' skipEvents='true' />")
    [void]$sb.AppendLine("  </Module>")
}
foreach ($m in $skipModules) {
    [void]$sb.AppendLine("  <Module file='`$(InPath)\$m.dll'>")
    [void]$sb.AppendLine("    <SkipType name='*' skipMethods='true' skipFields='true' skipProperties='true' skipEvents='true' skipStringHiding='false' />")
    [void]$sb.AppendLine("  </Module>")
}
[void]$sb.AppendLine("</Obfuscator>")
$xml = Join-Path $PSScriptRoot 'suite.generated.xml'
$sb.ToString() | Set-Content -LiteralPath $xml -Encoding UTF8
Write-Host "Running Obfuscar ..."
& $Obfuscar $xml
if ($LASTEXITCODE -ne 0) { throw "Obfuscar failed ($LASTEXITCODE)" }

# Swap the obfuscated managed assemblies back over the dist copy; keep Obfuscar's mapping out of the shipped folder.
foreach ($m in ($renameModules + $skipModules)) {
    $o = Join-Path $work "$m.dll"
    if (Test-Path $o) { Copy-Item $o (Join-Path $dist "$m.dll") -Force }
}
$map = Join-Path $work 'Mapping.txt'
if (Test-Path $map) { Copy-Item $map (Join-Path $repo 'dist\suite-obfuscation-mapping.txt') -Force }
Remove-Item $work -Recurse -Force
Write-Host "Done. Obfuscated app: $dist"
Write-Host "Launch to test: `"$dist\CenterIceModdingSuite.exe`""
