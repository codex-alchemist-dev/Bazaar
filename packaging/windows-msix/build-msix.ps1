# Builds the MSIX for Bazaar.Client.Windows (doc 37, sections 2 and 7).
# Signing goes through the signer interface in packaging/signing (signer.ps1). The signer and the
# certificate location come from packaging/signing/signer.json, or from BAZAAR_SIGNER,
# BAZAAR_SIGN_PFX_PATH and BAZAAR_SIGN_TIMESTAMP_URL. The PFX password is read from
# BAZAAR_SIGN_PFX_PASSWORD only, never from the command line.
# -PfxPath and -TimestampUrl still work and override the config, so existing callers are unchanged.
# The Publisher is taken from the signing certificate's subject, so it always matches.
# With signer 'none' the script builds an UNSIGNED package and warns, as before.
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

. (Join-Path $PSScriptRoot '..\signing\signer.ps1')

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$project  = Join-Path $repoRoot 'src\Bazaar.Client.Windows\Bazaar.Client.Windows.csproj'
$manifest = Join-Path $PSScriptRoot 'Package.appxmanifest'
$assets   = Join-Path $PSScriptRoot 'Assets'
$OutDir   = [System.IO.Path]::GetFullPath($OutDir)
$layout   = Join-Path $OutDir 'layout'

# Signer: config first, then explicit -PfxPath / -TimestampUrl arguments.
$signerConfig = Get-BazaarSignerConfig
if ($PfxPath) {
    $signerConfig.signer = 'pfx'
    $signerConfig.pfxPath = $PfxPath
}
if ($TimestampUrl) { $signerConfig.timestampUrl = $TimestampUrl }
$willSign = $signerConfig.signer -eq 'pfx'

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

if ($willSign -and -not (Test-Path $signerConfig.pfxPath)) { throw "Signing certificate not found: $($signerConfig.pfxPath)" }

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
if ($willSign) {
    $password = $env:BAZAAR_SIGN_PFX_PASSWORD
    if (-not $password) { throw 'Set BAZAAR_SIGN_PFX_PASSWORD to unlock the signing certificate.' }
    $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($signerConfig.pfxPath, $password, 'EphemeralKeySet')
    $manifestXml.Package.Identity.Publisher = $cert.Subject
}
$manifestXml.Save((Join-Path $layout 'AppxManifest.xml'))

# 3. Pack.
$suffix = if ($willSign) { 'signed' } else { 'unsigned' }
$package = Join-Path $OutDir "Bazaar.Client-$msixVersion-$Runtime-$suffix.msix"
& $makeappx.FullName pack /d $layout /p $package /o
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed ($LASTEXITCODE)." }

# 4. Sign through the configured signer.
$signed = Invoke-BazaarSigner -Package $package -Config $signerConfig
if ($signed) {
    Write-Host "Signed MSIX written to $package (publisher: $($cert.Subject))"
} else {
    Write-Host "Unsigned MSIX written to $package"
    Write-Host 'WARNING: unsigned build. Publisher is a placeholder; the package cannot install until it is signed.'
}
