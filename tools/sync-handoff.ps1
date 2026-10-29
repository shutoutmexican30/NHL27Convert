# Copies docs\HANDOFF.md to the user's Desktop as CIMS_HANDOFF.md (handles redirected Desktop folders).
# Run after every HANDOFF update so the two copies never drift.
$src = Join-Path (Split-Path $PSScriptRoot -Parent) 'docs\HANDOFF.md'
$dst = Join-Path ([Environment]::GetFolderPath('Desktop')) 'CIMS_HANDOFF.md'
Copy-Item -LiteralPath $src -Destination $dst -Force
Write-Output "Synced $src -> $dst"
