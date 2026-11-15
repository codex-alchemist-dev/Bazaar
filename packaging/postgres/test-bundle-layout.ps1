# Checks a staged Windows PostgreSQL bundle against the layout the host probes for, and checks that versions.txt pins
# every download. Run after build-pgvector-windows.ps1. Exits non-zero with a message on the first failure.
#   pwsh packaging/postgres/test-bundle-layout.ps1 -BundleRoot artifacts/pg-bundle
param(
    [string]$BundleRoot = "$PSScriptRoot\..\..\artifacts\pg-bundle"
)
$ErrorActionPreference = 'Stop'
$failures = @()
function Expect([bool]$Condition, [string]$Message) { if (-not $Condition) { $script:failures += $Message } }

# 1. versions.txt pins every download (the build refuses to run without them).
$versions = @{}
Get-Content "$PSScriptRoot\versions.txt" | Where-Object { $_ -match '^[A-Z_0-9]+=' } | ForEach-Object {
    $k, $v = $_ -split '=', 2; $versions[$k] = $v.Trim()
}
foreach ($key in 'PG_MAJOR', 'PG_VERSION', 'PG_EDB_BUILD', 'PGVECTOR_VERSION', 'PG_SHA256', 'PGVECTOR_SHA256') {
    Expect ([bool]$versions[$key]) "versions.txt has no value for $key"
}
Expect ($versions['PG_SHA256'] -match '^[0-9A-Fa-f]{64}$') 'PG_SHA256 is not a 64-digit hex SHA-256'
Expect ($versions['PGVECTOR_SHA256'] -match '^[0-9A-Fa-f]{64}$') 'PGVECTOR_SHA256 is not a 64-digit hex SHA-256'

# 2. The staged major folder has the files the host probes at startup.
$major = $versions['PG_MAJOR']
$dest = Join-Path $BundleRoot $major
foreach ($required in @('bin\postgres.exe', 'bin\initdb.exe', 'bin\pg_ctl.exe', 'lib\vector.dll', 'share\extension\vector.control')) {
    Expect (Test-Path (Join-Path $dest $required)) "bundle is missing $required"
}
Expect (@(Get-ChildItem (Join-Path $dest 'share\extension') -Filter 'vector--*.sql' -ErrorAction SilentlyContinue).Count -ge 1) 'bundle has no pgvector SQL scripts'
Expect (-not (Test-Path (Join-Path $dest 'include'))) 'bundle still ships development headers (include\)'

# 3. The manifest records the major, the versions and the input hashes.
$manifestPath = Join-Path $dest 'bazaar-bundle.txt'
if (Test-Path $manifestPath) {
    $manifest = @{}
    Get-Content $manifestPath | Where-Object { $_ -match '=' } | ForEach-Object { $k, $v = $_ -split '=', 2; $manifest[$k] = $v }
    Expect ($manifest['pg_major'] -eq $major) "manifest pg_major is '$($manifest['pg_major'])', expected $major"
    Expect ($manifest['pg_version'] -eq $versions['PG_VERSION']) 'manifest pg_version does not match versions.txt'
    Expect ($manifest['pgvector'] -eq $versions['PGVECTOR_VERSION']) 'manifest pgvector does not match versions.txt'
    Expect ($manifest['pg_zip_sha256'] -eq $versions['PG_SHA256']) 'manifest pg_zip_sha256 does not match versions.txt'
    Expect ($manifest['pgvector_tarball_sha256'] -eq $versions['PGVECTOR_SHA256']) 'manifest pgvector_tarball_sha256 does not match versions.txt'
} else {
    $failures += "bundle has no $manifestPath"
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    exit 1
}
Write-Host "Bundle layout OK: $dest (PostgreSQL $($versions['PG_VERSION']), pgvector $($versions['PGVECTOR_VERSION']))."
