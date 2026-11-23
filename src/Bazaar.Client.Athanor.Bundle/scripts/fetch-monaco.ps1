# Vendors the pinned, trimmed Monaco bundle into ../web/monaco (generated, not committed).
#
#   pwsh scripts/fetch-monaco.ps1            fetch, verify both hashes, install
#   pwsh scripts/fetch-monaco.ps1 -Update    also write the computed bundleSha256 into monaco.lock.json (a maintainer step)
#
# 1. Takes the npm tarball from $env:ATHANOR_MONACO_TGZ if set, else from tarballUrl, and checks its sha256 against the lock.
# 2. Extracts it and copies only the files matching the lock's "include" patterns (the trimmed bundle).
# 3. Hashes the copied files (sorted "path sha256" lines) and checks the result against bundleSha256.
# Any mismatch stops the script and leaves nothing installed.
param([switch]$Update)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$lockPath = Join-Path $root 'monaco.lock.json'
$lock = Get-Content $lockPath -Raw | ConvertFrom-Json
$work = Join-Path $root 'obj/monaco'
$dest = Join-Path $root 'web/monaco'

function Get-Sha256([string]$path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() }

New-Item -ItemType Directory -Force $work | Out-Null
$tgz = $env:ATHANOR_MONACO_TGZ
if (-not $tgz) {
  $tgz = Join-Path $work "monaco-editor-$($lock.version).tgz"
  if (-not (Test-Path $tgz)) { Invoke-WebRequest -Uri $lock.tarballUrl -OutFile $tgz }
}
$actual = Get-Sha256 $tgz
if ($actual -ne $lock.tarballSha256) { throw "Monaco tarball sha256 mismatch: expected $($lock.tarballSha256), got $actual" }

$extract = Join-Path $work 'extract'
if (Test-Path $extract) { Remove-Item -Recurse -Force $extract }
New-Item -ItemType Directory -Force $extract | Out-Null
$tar = if ($IsWindows -or $env:OS -eq 'Windows_NT') { Join-Path $env:SystemRoot 'System32/tar.exe' } else { 'tar' }
& $tar -xzf $tgz -C $extract
if ($LASTEXITCODE -ne 0) { throw 'tar failed' }

$src = Join-Path $extract $lock.sourceRoot
$staging = Join-Path $work 'staging'
if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
$lines = @()
foreach ($file in Get-ChildItem -Recurse -File $src) {
  $rel = [IO.Path]::GetRelativePath($src, $file.FullName).Replace('\', '/')
  if ($lock.include | Where-Object { $rel -like $_ }) {
    $target = Join-Path $staging "vs/$rel"
    New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target
    $lines += "$rel $(Get-Sha256 $file.FullName)"
  }
}
if ($lines.Count -eq 0) { throw 'No files matched the include list.' }
$sorted = [string[]]$lines
[Array]::Sort($sorted, [StringComparer]::Ordinal)
$manifest = $sorted -join "`n"
$bytes = [Text.Encoding]::UTF8.GetBytes($manifest + "`n")
$bundleHash = ([BitConverter]::ToString([Security.Cryptography.SHA256]::HashData($bytes)) -replace '-', '').ToLowerInvariant()

if ($Update) {
  $text = Get-Content $lockPath -Raw
  $text = [regex]::Replace($text, '"bundleSha256":\s*"[0-9a-f]*"', "`"bundleSha256`": `"$bundleHash`"")
  Set-Content -LiteralPath $lockPath -Value $text -NoNewline
  Write-Host "Wrote bundleSha256 $bundleHash ($($lines.Count) files)"
} elseif ($bundleHash -ne $lock.bundleSha256) {
  throw "Trimmed bundle sha256 mismatch: expected $($lock.bundleSha256), got $bundleHash"
}

Copy-Item -LiteralPath (Join-Path $extract 'package/LICENSE') -Destination (Join-Path $staging 'LICENSE')
Copy-Item -LiteralPath (Join-Path $extract 'package/ThirdPartyNotices.txt') -Destination (Join-Path $staging 'ThirdPartyNotices.txt')
Set-Content -LiteralPath (Join-Path $staging '.stamp') -Value $bundleHash -NoNewline

if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
Move-Item -LiteralPath $staging -Destination $dest
Write-Host "Monaco $($lock.version): $($lines.Count) files, bundle $bundleHash, installed to $dest"
