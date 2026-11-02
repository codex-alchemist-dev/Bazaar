# Builds the packaged PostgreSQL bundle for Windows: EDB PostgreSQL binaries plus upstream pgvector (MSVC nmake).
# Output layout (one folder per major, so 18 and 19 can sit side by side):
#   <OutDir>\<major>\bin      postgres.exe, initdb.exe, pg_ctl.exe, ...
#   <OutDir>\<major>\lib      vector.dll and the server libraries
#   <OutDir>\<major>\share    share\extension\vector.control and vector--*.sql
# Run from a Visual Studio Developer shell (cl.exe and nmake on PATH). CI sets this up with ilammy/msvc-dev-cmd.
param(
    [string]$OutDir = "$PSScriptRoot\..\..\artifacts\pg-bundle",
    [string]$Work = "$env:RUNNER_TEMP\pg-bundle-work"
)
$ErrorActionPreference = 'Stop'

$versions = @{}
Get-Content "$PSScriptRoot\versions.env" | Where-Object { $_ -match '^[A-Z_]+=' } | ForEach-Object {
    $k, $v = $_ -split '=', 2; $versions[$k] = $v.Trim()
}
$major = $versions['PG_MAJOR']
$pgVersion = $versions['PG_VERSION']
$build = $versions['PG_EDB_BUILD']
$pgvector = $versions['PGVECTOR_VERSION']

New-Item -ItemType Directory -Force -Path $Work, $OutDir | Out-Null
$zip = Join-Path $Work "postgresql-$pgVersion-$build-windows-x64-binaries.zip"
$pgUrl = "https://get.enterprisedb.com/postgresql/postgresql-$pgVersion-$build-windows-x64-binaries.zip"
if (-not (Test-Path $zip)) {
    Invoke-WebRequest -Uri $pgUrl -OutFile $zip -UseBasicParsing
}
$pgExtract = Join-Path $Work 'pg-extract'
Remove-Item $pgExtract -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive -Path $zip -DestinationPath $pgExtract
$pgRoot = Join-Path $pgExtract 'pgsql'   # EDB archives contain one top-level pgsql folder

$src = Join-Path $Work "pgvector-$pgvector"
if (-not (Test-Path $src)) {
    $tgz = Join-Path $Work "pgvector-$pgvector.tar.gz"
    Invoke-WebRequest -Uri "https://github.com/pgvector/pgvector/archive/refs/tags/v$pgvector.tar.gz" -OutFile $tgz -UseBasicParsing
    tar -xzf $tgz -C $Work
}

# Build pgvector against the bundled server headers and import libraries.
Push-Location $src
try {
    $env:PGROOT = $pgRoot
    nmake /NOLOGO /F Makefile.win
    if ($LASTEXITCODE -ne 0) { throw "pgvector nmake build failed ($LASTEXITCODE)" }
} finally {
    Pop-Location
}

# Stage the bundle: copy the whole EDB tree, then add pgvector's files in the same places an install would.
$dest = Join-Path $OutDir $major
Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item -Path $pgRoot -Destination $dest -Recurse
$ext = Join-Path $dest 'share\extension'
New-Item -ItemType Directory -Force -Path $ext | Out-Null
Copy-Item (Join-Path $src 'vector.dll') (Join-Path $dest 'lib') -Force
Copy-Item (Join-Path $src 'vector.control') $ext -Force
Copy-Item (Join-Path $src 'sql\vector--*.sql') $ext -Force

# Keep only what the bundle needs: drop the EDB installer helpers and the development headers.
Remove-Item (Join-Path $dest 'pgAdmin*'), (Join-Path $dest 'stackbuilder'), (Join-Path $dest 'doc') -Recurse -Force -ErrorAction SilentlyContinue

# The bundle must record its major and pgvector version; the host reads pg-version before it starts a cluster.
Set-Content -Path (Join-Path $dest 'bazaar-bundle.txt') -Value "pg_major=$major`npg_version=$pgVersion`npgvector=$pgvector"

# Fail the build if a required file is missing (the host probes for these at startup).
foreach ($required in @('bin\postgres.exe', 'bin\initdb.exe', 'bin\pg_ctl.exe', 'lib\vector.dll', 'share\extension\vector.control')) {
    if (-not (Test-Path (Join-Path $dest $required))) { throw "bundle is missing $required" }
}
$sqlCount = (Get-ChildItem (Join-Path $ext 'vector--*.sql')).Count
if ($sqlCount -lt 1) { throw 'bundle has no pgvector SQL scripts' }
Write-Host "Bundle staged at $dest (pgvector $pgvector, $sqlCount SQL scripts)."
