# Vendors the pinned Monaco release (MIT) into web/monaco/ from the npm tarball. Not committed (about 25 MB).
param([string]$Tgz = "$PSScriptRoot\monaco-editor-0.57.0.tgz")
$expected = '3ea1712fbacd3290cf4751007e3a5b57cc279767607812d4c3e57925fb0b05c2'
if (-not (Test-Path $Tgz)) {
  Invoke-WebRequest https://registry.npmjs.org/monaco-editor/-/monaco-editor-0.57.0.tgz -OutFile $Tgz
}
$h = (Get-FileHash $Tgz -Algorithm SHA256).Hash.ToLower()
if ($h -ne $expected) { throw "sha256 mismatch: $h" }
$tmp = Join-Path $PSScriptRoot 'obj\monaco-extract'
New-Item -ItemType Directory -Force $tmp | Out-Null
& (Join-Path $env:SystemRoot 'System32\tar.exe') -xzf $Tgz -C $tmp
$dst = Join-Path $PSScriptRoot 'web\monaco'
New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item "$tmp\package\min\vs" $dst -Recurse -Force
Copy-Item "$tmp\package\LICENSE" "$dst\LICENSE"
Copy-Item "$tmp\package\ThirdPartyNotices.txt" "$dst\ThirdPartyNotices.txt"
Write-Host "monaco 0.57.0 vendored into $dst"
