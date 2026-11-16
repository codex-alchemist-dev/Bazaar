# Builds one packaged PostgreSQL bundle from pinned upstream source on Windows: PostgreSQL (18 or 19 candidate, built with
# Meson and Ninja under the MSVC toolchain) plus pgvector (nmake). This is the Windows source-compile path of doc 42
# section 14.1 (Q1 path a). The upstream EDB binary path (build-pgvector-windows.ps1) is separate and unchanged.
#
#   pwsh packaging/postgres/source/build-from-source-windows.ps1 -Major 18 -OutDir artifacts/pg-src-bundle
#
# Run from a Visual Studio Developer shell (cl.exe on PATH) with python, meson and ninja installed. CI sets up MSVC
# with ilammy/msvc-dev-cmd and installs meson and ninja with pip.
#
# Output has the same shape as the EDB bundle (one folder per major): <OutDir>\<major>\bin, lib, share, plus
# bazaar-bundle.txt and SHA256SUMS. Each pinned tarball is checked against sources.txt before it is unpacked.
#
# Known difference from the Linux source build (recorded in doc 42 section 14.1): this build uses -Dssl=none and
# -Dzlib=disabled, because no OpenSSL or zlib source is pinned for Windows yet. The host connects over loopback with
# scram-sha-256, so the server does not need TLS for its own use.
param(
    [Parameter(Mandatory)][ValidateSet('18', '19')][string]$Major,
    [string]$OutDir = "$PSScriptRoot\..\..\..\artifacts\pg-src-bundle",
    [string]$Work = $(if ($env:RUNNER_TEMP) { Join-Path $env:RUNNER_TEMP 'pg-src-work' } else { Join-Path ([IO.Path]::GetTempPath()) 'pg-src-work' }),
    [int]$Jobs = [Environment]::ProcessorCount
)
$ErrorActionPreference = 'Stop'

# Read the pins. Keys are PG18_*, PG19_*, PGVECTOR_*.
$pins = @{}
Get-Content (Join-Path $PSScriptRoot 'sources.txt') | Where-Object { $_ -match '^[A-Z0-9_]+=' } | ForEach-Object {
    $k, $v = $_ -split '=', 2; $pins[$k] = $v.Trim()
}
$pgPrefix = "PG$Major"
$pgVersion = $pins["${pgPrefix}_VERSION"]
$pgTar = $pins["${pgPrefix}_TARBALL"]
$pgUrl = $pins["${pgPrefix}_URL"]
$pgSha = $pins["${pgPrefix}_SHA256"]
$pgvector = $pins['PGVECTOR_VERSION']
$pgvectorTar = $pins['PGVECTOR_TARBALL']
$pgvectorUrl = $pins['PGVECTOR_URL']
$pgvectorSha = $pins['PGVECTOR_SHA256']

foreach ($sha in $pgSha, $pgvectorSha) {
    if ($sha -notmatch '^[0-9A-Fa-f]{64}$') { throw 'a SHA-256 pin in sources.txt is not 64 hex digits' }
}

# Download once, verify the SHA-256, delete the file on a mismatch.
function Get-Verified([string]$Url, [string]$File, [string]$Expected) {
    if (-not (Test-Path $File)) {
        Invoke-WebRequest -Uri $Url -OutFile "$File.part" -UseBasicParsing
        Move-Item "$File.part" $File -Force
    }
    $actual = (Get-FileHash -Path $File -Algorithm SHA256).Hash
    if ($actual -ne $Expected.ToUpperInvariant()) {
        Remove-Item $File -Force
        throw "$(Split-Path $File -Leaf) SHA256 mismatch: expected $Expected, got $actual"
    }
    Write-Host "$(Split-Path $File -Leaf) SHA256 verified."
    return $actual.ToLowerInvariant()
}

New-Item -ItemType Directory -Force -Path (Join-Path $Work 'dl'), $OutDir | Out-Null
$pgTarball = Get-Verified $pgUrl (Join-Path $Work "dl\$pgTar") $pgSha
$pgvectorTarball = Get-Verified $pgvectorUrl (Join-Path $Work "dl\$pgvectorTar") $pgvectorSha

# PostgreSQL with Meson: install into a private prefix that also gets the server headers (pgvector needs them).
$src = Join-Path $Work "pg-$Major"
$prefix = Join-Path $Work "prefix-$Major"
$build = Join-Path $Work "build-$Major"
Remove-Item $src, $prefix, $build -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $src | Out-Null
tar -xzf (Join-Path $Work "dl\$pgTar") -C $src --strip-components=1
if ($LASTEXITCODE -ne 0) { throw 'extracting the PostgreSQL source failed' }

Push-Location $src
try {
    meson setup $build --prefix $prefix --buildtype release --wrap-mode nodownload `
        -Dssl=none -Dzlib=disabled -Dnls=disabled -Dicu=disabled -Dllvm=disabled -Dlz4=disabled -Dzstd=disabled -Dlibxml=disabled -Dtap_tests=disabled
    if ($LASTEXITCODE -ne 0) { throw "meson setup failed ($LASTEXITCODE)" }
    meson compile -C $build -j $Jobs
    if ($LASTEXITCODE -ne 0) { throw "meson compile failed ($LASTEXITCODE)" }
    meson install -C $build --no-rebuild
    if ($LASTEXITCODE -ne 0) { throw "meson install failed ($LASTEXITCODE)" }
} finally {
    Pop-Location
}

# pgvector, built against this PostgreSQL's headers and import libraries.
$pgv = Join-Path $Work "pgvector-$pgvector"
Remove-Item $pgv -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $pgv | Out-Null
tar -xzf (Join-Path $Work "dl\$pgvectorTar") -C $pgv --strip-components=1
if ($LASTEXITCODE -ne 0) { throw 'extracting pgvector failed' }
Push-Location $pgv
try {
    $env:PGROOT = $prefix
    nmake /NOLOGO /F Makefile.win
    if ($LASTEXITCODE -ne 0) { throw "pgvector nmake build failed ($LASTEXITCODE)" }
} finally {
    Pop-Location
}

# Stage: bin, lib and share from the prefix, pgvector's files in the places an install would put them.
$dest = Join-Path $OutDir $Major
Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $dest | Out-Null
foreach ($dir in 'bin', 'lib', 'share') { Copy-Item -Path (Join-Path $prefix $dir) -Destination $dest -Recurse }
$ext = Join-Path $dest 'share\extension'
New-Item -ItemType Directory -Force -Path $ext | Out-Null
Copy-Item (Join-Path $pgv 'vector.dll') (Join-Path $dest 'lib') -Force
Copy-Item (Join-Path $pgv 'vector.control') $ext -Force
Copy-Item (Join-Path $pgv 'sql\vector--*.sql') $ext -Force
Remove-Item (Join-Path $dest 'share\man'), (Join-Path $dest 'share\doc') -Recurse -Force -ErrorAction SilentlyContinue

$msvc = ((cmd /c 'cl 2>&1') | Select-Object -First 1) -replace '\s+', ' '
Set-Content -Path (Join-Path $dest 'bazaar-bundle.txt') -Value @(
    "pg_major=$Major"
    "pg_version=$pgVersion"
    "pgvector=$pgvector"
    "pg_source_sha256=$pgTarball"
    "pgvector_tarball_sha256=$pgvectorTarball"
    'build_path=source'
    "toolchain=$msvc"
    'signed=false'
)

# Hash every staged file. Signing of the executables is a separate step (packaging/signing).
$root = (Resolve-Path $dest).Path
Get-ChildItem $dest -Recurse -File | Where-Object { $_.Name -ne 'SHA256SUMS' } | Sort-Object FullName | ForEach-Object {
    $rel = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
    "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $rel"
} | Set-Content -Path (Join-Path $dest 'SHA256SUMS')

foreach ($required in 'bin\postgres.exe', 'bin\initdb.exe', 'bin\pg_ctl.exe', 'lib\vector.dll', 'share\extension\vector.control') {
    if (-not (Test-Path (Join-Path $dest $required))) { throw "bundle is missing $required" }
}
Write-Host "Bundle staged at $dest (PostgreSQL $pgVersion, pgvector $pgvector, source build)."
