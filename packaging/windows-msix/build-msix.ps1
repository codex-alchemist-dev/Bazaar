# Builds the MSIX for Bazaar.Client.Windows (doc 37, sections 2 and 7).
# Signing (owner decision, 2026-11-13): the company server holds its own signing certificate and
# passes it in with -PfxPath. The Publisher is taken from that certificate's subject, so it always
# matches. Without -PfxPath the script still builds an UNSIGNED package and warns, as before.
# The PFX password is read from the BAZAAR_SIGN_PFX_PASSWORD environment variable, never from the
# command line, so it stays out of process listings and shell history.
#
# Usage (from the repository root):
#   pwsh packaging/windows-msix/build-msix.ps1 [-Configuration Release] [-OutDir <path>]
#   pwsh packaging/windows-msix/build-msix.ps1 -PfxPath <company.pfx> [-TimestampUrl <url>]

param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutDir = (Join-Path $PSScriptRoot '..\..\artifacts\msix'),
    [string]$PfxPath = '',
    [string]$TimestampUrl = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$project  = Join-Path $repoRoot 'src\Bazaar.Client.Windows\Bazaar.Client.Windows.csproj'
$manifest = Join-Path $PSScriptRoot 'Package.appxmanifest'
$assets   = Join-Path $PSScriptRoot 'Assets'
$OutDir   = [System.IO.Path]::GetFullPath($OutDir)
$layout   = Join-Path $OutDir 'layout'

# Version comes from VersionPrefix (Directory.Build.props), in MSIX four-part form.
$props = [xml](Get-Content (Join-Path $repoRoot 'Directory.Build.props') -Raw)
$prefix = $props.Project.PropertyGroup | ForEach-Object { $_.VersionPrefix } | Where-Object { $_ } | Select-Object -First 1
if (-not $prefix) { throw 'VersionPrefix not found in Directory.Build.props.' }
$msixVersion = if ($prefix -match '^\d+\.\d+\.\d+$') { "$prefix.0" } else { throw "Unsupported VersionPrefix '$prefix' for MSIX." }

# Locate makeappx.exe from the newest installed Windows SDK.
$makeappx = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\makeappx\.exe$' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if (-not $makeappx) { throw 'makeappx.exe not found. Install the Windows 10/11 SDK.' }

# Locate signtool.exe only when signing (same SDK as makeappx).
$signtool = $null
if ($PfxPath) {
    $signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe not found. Install the Windows 10/11 SDK.' }
    if (-not (Test-Path $PfxPath)) { throw "Signing certificate not found: $PfxPath" }
}

if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Force -Path $layout | Out-Null

# 1. Self-contained publish of the client (no source changes).
dotnet publish $project -c $Configuration -r $Runtime --self-contained true -o $layout
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

# 2. Manifest and visual assets.
Copy-Item $manifest (Join-Path $layout 'AppxManifest.xml') -Force
Copy-Item $assets (Join-Path $layout 'Assets') -Recurse -Force
$manifestXml = [xml](Get-Content (Join-Path $layout 'AppxManifest.xml') -Raw)
$manifestXml.Package.Identity.Version = $msixVersion

# The signing certificate's subject becomes the manifest Publisher. MSIX requires an exact match.
$cert = $null
if ($PfxPath) {
    $password = $env:BAZAAR_SIGN_PFX_PASSWORD
    if (-not $password) { throw 'Set BAZAAR_SIGN_PFX_PASSWORD to unlock the signing certificate.' }
    $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($PfxPath, $password, 'EphemeralKeySet')
    $manifestXml.Package.Identity.Publisher = $cert.Subject
}
$manifestXml.Save((Join-Path $layout 'AppxManifest.xml'))

# 3. Pack.
$suffix = if ($PfxPath) { 'signed' } else { 'unsigned' }
$package = Join-Path $OutDir "Bazaar.Client-$msixVersion-$Runtime-$suffix.msix"
& $makeappx.FullName pack /d $layout /p $package /o
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed ($LASTEXITCODE)." }

# 4. Sign with the company certificate, if one was given.
if ($PfxPath) {
    $signArgs = @('sign', '/fd', 'SHA256', '/f', $PfxPath, '/p', $env:BAZAAR_SIGN_PFX_PASSWORD)
    if ($TimestampUrl) { $signArgs += @('/tr', $TimestampUrl, '/td', 'SHA256') }
    $signArgs += $package
    & $signtool.FullName @signArgs
    if ($LASTEXITCODE -ne 0) { throw "signtool sign failed ($LASTEXITCODE)." }
    Write-Host "Signed MSIX written to $package (publisher: $($cert.Subject))"
} else {
    Write-Host "Unsigned MSIX written to $package"
    Write-Host 'WARNING: unsigned build. Publisher is a placeholder; the package cannot install until it is signed.'
}
