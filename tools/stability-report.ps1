<#
  stability-report.ps1 [-Days 14] [-Out file.txt]
  Read-only summary of the machine's hardware-error history, for checking whether a BIOS / Curve Optimizer
  change actually helped: CPU machine checks per core, hard resets, GPU driver timeouts, the one-off bugchecks,
  and the memory/BIOS configuration they happen on. Run it after each change and compare.
#>
param([int]$Days = 14, [string]$Out = "")

$lines = New-Object System.Collections.Generic.List[string]
function Add-Line([string]$text) { $script:lines.Add($text); Write-Host $text }

$since = (Get-Date).AddDays(-$Days)
Add-Line ("stability report - {0} - window {1} days" -f (Get-Date -Format 'yyyy-MM-dd HH:mm'), $Days)
Add-Line ("#" * 78)

# ---- what the machine is
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$bios = Get-CimInstance Win32_BIOS
$gpu = Get-CimInstance Win32_VideoController | Where-Object { $_.Name -notmatch 'Parsec|Basic' }
Add-Line ("CPU    {0}  ({1} cores)" -f $cpu.Name.Trim(), $cpu.NumberOfCores)
Add-Line ("BIOS   {0} {1}  ({2:yyyy-MM-dd})" -f $bios.Manufacturer, $bios.SMBIOSBIOSVersion, $bios.ReleaseDate)
foreach ($g in $gpu) { Add-Line ("GPU    {0}  driver {1}  ({2:yyyy-MM-dd})" -f $g.Name, $g.DriverVersion, $g.DriverDate) }
$mem = Get-CimInstance Win32_PhysicalMemory
Add-Line ("RAM    {0} GB in {1} sticks, {2} MT/s effective" -f [int](($mem | Measure-Object Capacity -Sum).Sum/1GB), $mem.Count, ($mem | Select-Object -First 1).ConfiguredClockSpeed)
Add-Line ""

# ---- CPU machine checks (the thing a Curve Optimizer change moves)
$mce = @()
$ev = Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-WHEA-Logger'; Id=18; StartTime=$since} -ErrorAction SilentlyContinue
foreach ($e in $ev) {
    $a = [regex]::Match($e.Message, 'Processor APIC ID:\s*(\d+)')
    $t = [regex]::Match($e.Message, 'Error Type:\s*(.+)')
    $apic = -1
    if ($a.Success) { $apic = [int]$a.Groups[1].Value }
    $type = ""
    if ($t.Success) { $type = $t.Groups[1].Value.Trim() }
    $mce += [pscustomobject]@{ Time = $e.TimeCreated; Apic = $apic; Type = $type }
}
Add-Line ("CPU fatal machine checks (WHEA 18): {0}" -f $mce.Count)
if ($mce.Count -gt 0) {
    $mce | Sort-Object Time | Select-Object -Last 10 | ForEach-Object {
        Add-Line ("   {0:yyyy-MM-dd HH:mm:ss}   APIC {1,3}   {2}" -f $_.Time, $_.Apic, $_.Type)
    }
    Add-Line "   per logical processor:"
    $mce | Group-Object Apic | Sort-Object Count -Descending | ForEach-Object {
        Add-Line ("      APIC {0,3}  {1,3}x" -f $_.Name, $_.Count)
    }
    $cores = ($mce | Select-Object -ExpandProperty Apic -Unique).Count
    Add-Line ("   distinct APIC IDs: {0}  (0/1 = SMT siblings of one core; >16 usually the second CCD)" -f $cores)
}
Add-Line ""

# ---- corrected errors: appear before fatal ones when a setting is only slightly off
$corr = (Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-WHEA-Logger'; Id=17; StartTime=$since} -ErrorAction SilentlyContinue | Measure-Object).Count
Add-Line ("corrected hardware errors (WHEA 17): {0}" -f $corr)

# ---- resets and bugchecks
$k41 = Get-WinEvent -FilterHashtable @{LogName='System'; Id=41; StartTime=$since} -ErrorAction SilentlyContinue
Add-Line ("unclean resets (Kernel-Power 41): {0}" -f ($k41 | Measure-Object).Count)
$bc = Get-WinEvent -FilterHashtable @{LogName='System'; Id=1001; StartTime=$since} -ErrorAction SilentlyContinue | Where-Object { $_.ProviderName -match 'BugCheck|WER-SystemErrorReporting' }
foreach ($e in $bc) {
    $m = [regex]::Match($e.Message, 'bugcheck was:\s*(0x[0-9a-fA-F]+)')
    $code = "?"
    if ($m.Success) { $code = $m.Groups[1].Value }
    Add-Line ("   {0:yyyy-MM-dd HH:mm}   BSOD {1}" -f $e.TimeCreated, $code)
}
Add-Line ""

# ---- GPU driver timeouts: a different failure from the CPU ones
$tdr = Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Display'; StartTime=$since} -ErrorAction SilentlyContinue | Where-Object { $_.Id -in 4101,4102 }
Add-Line ("GPU driver timeouts (Display 4101/4102): {0}" -f ($tdr | Measure-Object).Count)
foreach ($e in ($tdr | Select-Object -First 5)) { Add-Line ("   {0:yyyy-MM-dd HH:mm}  {1}" -f $e.TimeCreated, ($e.Message -split "`r?`n")[0]) }

$g = 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers'
Add-Line ""
Add-Line "TDR settings:"
foreach ($n in 'TdrDelay','TdrLevel') {
    $v = (Get-ItemProperty -Path $g -Name $n -ErrorAction SilentlyContinue).$n
    $shown = "(default)"
    if ($null -ne $v) { $shown = "$v" }
    Add-Line ("   {0,-10} {1}" -f $n, $shown)
}

if ($Out -ne "") {
    [IO.File]::WriteAllLines($Out, $lines, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ""
    Write-Host ("written to {0}" -f $Out)
}