<#
  remove-ryzen-master.ps1 - cleans up what a failed Ryzen Master uninstall leaves behind.
  Run from an ELEVATED PowerShell. Read the console output; nothing is deleted without being listed first.
  Registry keys are exported to the Desktop before removal. Reboot afterwards.
#>
param([switch]$DryRun)

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($id)
if ((-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) -and (-not $DryRun)) {
    Write-Host "This needs an elevated PowerShell (Run as administrator)." -ForegroundColor Yellow
    exit 1
}

function Step([string]$text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }
function Invoke-Step([string]$what, [scriptblock]$action) {
    if ($DryRun) { Write-Host "   would: $what"; return }
    try { & $action; Write-Host "   done: $what" -ForegroundColor Green }
    catch { Write-Host "   FAILED: $what -> $($_.Exception.Message)" -ForegroundColor Red }
}

$backup = Join-Path ([Environment]::GetFolderPath('Desktop')) ("ryzenmaster-backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $backup | Out-Null

Step "1. stop Ryzen Master processes"
Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'RyzenMaster|cpumetricsserver' } | ForEach-Object {
    Invoke-Step ("stop process " + $_.Name + " (" + $_.Id + ")") { Stop-Process -Id $_.Id -Force }
}

Step "2. remove the kernel driver service"
$svc = 'AMDRyzenMasterDriverV32'
if (Get-Service $svc -ErrorAction SilentlyContinue) {
    Invoke-Step "sc stop $svc" { sc.exe stop $svc | Out-Null }
    Invoke-Step "sc delete $svc" { sc.exe delete $svc | Out-Null }
} else { Write-Host "   not present: $svc" }

Step "3. remove the driver file (may need the reboot below)"
$sys = Join-Path $env:SystemRoot 'system32\AMDRyzenMasterDriver.sys'
if (Test-Path $sys) { Invoke-Step "delete $sys" { Remove-Item $sys -Force } } else { Write-Host "   not present: $sys" }

Step "4. remove the empty program folder"
$dir = 'C:\Program Files\AMD\RyzenMaster'
if (Test-Path $dir) {
    $n = (Get-ChildItem $dir -Recurse -Force -ErrorAction SilentlyContinue | Measure-Object).Count
    Write-Host "   $dir contains $n item(s)"
    Invoke-Step "delete $dir" { Remove-Item $dir -Recurse -Force }
} else { Write-Host "   not present: $dir" }

Step "5. remove stale uninstall entries (registry backed up first)"
$roots = @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
           'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')
foreach ($r in $roots) {
    if (-not (Test-Path $r)) { continue }
    Get-ChildItem $r -ErrorAction SilentlyContinue | ForEach-Object {
        $item = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
        if ($item.DisplayName -and $item.DisplayName -match 'Ryzen Master') {
            $name = ($item.DisplayName -replace '[^A-Za-z0-9._-]', '_') + '-' + $_.PSChildName
            Invoke-Step ("export + delete uninstall key " + $_.PSChildName + " (" + $item.DisplayName + ")") {
                reg.exe export $_.PSPath (Join-Path $backup ($name + '.reg')) /y | Out-Null
                Remove-Item $_.PSPath -Recurse -Force
            }
        }
    }
}

Step "6. remove the leftover Ryzen Master settings root"
$amd = 'HKLM:\SOFTWARE\AMD\RyzenMaster'
if (Test-Path $amd) {
    Invoke-Step "export + delete $amd" {
        reg.exe export $amd (Join-Path $backup 'HKLM-SOFTWARE-AMD-RyzenMaster.reg') /y | Out-Null
        Remove-Item $amd -Recurse -Force
    }
} else { Write-Host "   not present: $amd" }

Step "left untouched on purpose"
Write-Host "   C:\Program Files\AMD\CNext\CNext\cpumetricsserver.exe and the AMDRyzenMasterSDKTask belong to"
Write-Host "   AMD Software (Adrenalin) CPU metrics, not Ryzen Master."

Step "result"
Write-Host "   registry backups: $backup"
Write-Host "   REBOOT now. After the reboot check with:"
Write-Host "     Get-Service AMDRyzenMasterDriverV32 -ErrorAction SilentlyContinue   # should be empty"
Write-Host "     Test-Path C:\Windows\system32\AMDRyzenMasterDriver.sys               # should be False"
Write-Host ""
Write-Host "   If 'Apps' still shows AMD Ryzen Master, reinstall 3.1.1.5502 and uninstall it from Apps -"
Write-Host "   that is the reliable way to clear a broken MSI registration."