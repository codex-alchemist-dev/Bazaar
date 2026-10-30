# archive_command helper for the Windows drill: archive_command = 'pwsh -NoProfile -File <path>/archive.ps1 "%p" "%f" "<waldir>"'
# Copies to a temp name, checks the size, then renames into place. Exits non-zero on any doubt.
param([string]$Src, [string]$Name, [string]$Dir)
$ErrorActionPreference = 'Stop'
$dst = Join-Path $Dir $Name
$tmp = Join-Path $Dir ".$Name.tmp"
if (Test-Path $dst) {
    if ((Get-FileHash $Src).Hash -eq (Get-FileHash $dst).Hash) { exit 0 } else { exit 1 }
}
Copy-Item $Src $tmp -Force
$fs = [System.IO.File]::Open($tmp, 'Open', 'ReadWrite'); $fs.Flush($true); $fs.Dispose()
if ((Get-Item $Src).Length -ne (Get-Item $tmp).Length) { exit 1 }
Move-Item $tmp $dst
exit 0
