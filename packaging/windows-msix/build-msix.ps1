# Builds an UNSIGNED MSIX for Bazaar.Client.Windows (doc 37, sections 2 and 7).
# Signing is parked: this script never signs. Install of the output needs a trusted
# signature (or developer mode with a matching test certificate) and is out of scope here.
#
# Usage (from the repository root):
#   pwsh packaging/windows-msix/build-msix.ps1 [-Configuration Release] [-OutDir <path>]

param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutDir = (Join-Path $PSScriptRoot '..\..\artifacts\msix')
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
$manifestXml.Save((Join-Path $layout 'AppxManifest.xml'))

# 3. Pack. No signing step by design (doc 37 section 7: parked).
$package = Join-Path $OutDir "Bazaar.Client-$msixVersion-$Runtime-unsigned.msix"
& $makeappx.FullName pack /d $layout /p $package /o
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed ($LASTEXITCODE)." }

Write-Host "Unsigned MSIX written to $package"
Write-Host 'WARNING: unsigned build. Publisher is a placeholder; the package cannot install until it is signed.'
