# Per-channel Velopack packaging for the Windows client (doc 28 item 12, doc 30 section 3, doc 37 section 4b).
# Pipeline step only: it packs, signs the installer through the signer interface, and writes a manifest.
# It never uploads or publishes anything.
#
# Usage (from the repository root, on a Windows build host with vpk installed):
#   pwsh packaging/velopack/pack-client.ps1 -Version 0.1.0 -Channel stable -PublishDir <dir> -OutputDir <dir> [-Ring all] [-DryRun]
#
# Inputs
#   -Version     SemVer 2 without a leading "v" (0.1.0, 0.2.0-beta.1, 0.1.0+a1b2c3d).
#   -Channel     stable | beta | nightly | lts (doc 49 section 6). resolve-channel.sh
#                maps the deprecated -alpha.N tag alias to nightly before this script runs, so alpha is not
#                accepted here. Each channel gets its own feed. Anything else fails closed.
#   -Ring        staged ring label (default all), same rule as resolve-channel.sh. A ring is a rollout stage of
#                one build within one channel, so it does NOT change the vpk channel. It is recorded in the manifest.
#   -PublishDir  output of dotnet publish for Bazaar.Client.Windows (must exist and not be empty).
#   -OutputDir   where vpk writes the packages and feed, and where FEED-MANIFEST.txt goes.
#   -Runtime     vpk target RID (default win-x64). -PackId and -MainExe name the app (assumed values).
#   -DryRun      print the vpk command and stop: no vpk needed, nothing written. Tests use it.
#
# Channel to flag mapping: vpk channel = "<Runtime>-<Channel>", for example win-x64-beta, so each
# OS/architecture/stream has its own feed (the pattern Velopack's channel docs show). Velopack then
# writes releases.<vpk channel>.json and, for legacy clients, RELEASES-<vpk channel>.
#
# Signing: pack first, then sign every produced *.exe (Setup.exe) through packaging/signing/signer.ps1
# (signer none leaves it unsigned). No certificate is in the repository. LIMITATION: this signs the
# installer only, not the app binaries inside the package; that needs vpk --signTemplate, not wired yet.
# Hashes in the manifest are taken after signing.
#
# Flag status
#   Verified against docs.velopack.io (vpk pack reference and channels page, fetched 2026-11-20):
#     --packId, --packVersion, --packDir, --mainExe, --outputDir, --channel, --runtime; the default
#     channel is the OS name; each build writes releases.{channel}.json.
#   NOT verified: exact Setup.exe and .nupkg names per channel (docs do not say), whether
#     RELEASES-<channel> is written for a win-x64-* channel, and vpk exit-code behaviour. The manifest
#     lists whatever vpk really produced, so it guesses no names. No real vpk run has happened yet.

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$Channel,
    [Parameter(Mandatory)] [string]$PublishDir,
    [Parameter(Mandatory)] [string]$OutputDir,
    [string]$Ring = 'all',
    [string]$Runtime = 'win-x64',
    [string]$PackId = 'Bazaar.Client.Windows',
    [string]$MainExe = 'Bazaar.Client.Windows.exe',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$semverRe = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-((0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$'
$channels = @('stable', 'beta', 'nightly', 'lts')

if ($Version -cnotmatch $semverRe) { throw "Version '$Version' is not SemVer (expected MAJOR.MINOR.PATCH[-pre][+build], no leading v)." }
if ($Channel -cnotin $channels)    { throw "Channel '$Channel' is not one of: $($channels -join ', ')." }
if ($Ring -cnotmatch '^[a-z][a-z0-9-]{0,31}$') { throw "Ring '$Ring' must be 1-32 lowercase letters, digits or hyphens, starting with a letter." }
if ($Runtime -cnotmatch '^[a-z0-9-]+$')        { throw "Runtime '$Runtime' is not a valid RID." }
if ($PackId -cnotmatch '^[A-Za-z0-9._-]+$')    { throw "PackId '$PackId' has invalid characters." }
if ($MainExe -cnotmatch '^[^\\/]+$')           { throw "MainExe '$MainExe' must be a file name, not a path." }

if (-not (Test-Path -LiteralPath $PublishDir -PathType Container)) { throw "Publish directory not found: $PublishDir" }
if (-not (Get-ChildItem -LiteralPath $PublishDir -Force | Select-Object -First 1)) { throw "Publish directory is empty: $PublishDir" }

$vpkChannel = "$Runtime-$Channel"
$vpkArgs = @(
    'pack',
    '--packId', $PackId,
    '--packVersion', $Version,
    '--packDir', $PublishDir,
    '--mainExe', $MainExe,
    '--outputDir', $OutputDir,
    '--channel', $vpkChannel,
    '--runtime', $Runtime
)

if ($DryRun) {
    Write-Output ('DRYRUN: vpk ' + ($vpkArgs -join ' '))
    Write-Output "DRYRUN: channel=$Channel ring=$Ring version=$Version vpkChannel=$vpkChannel"
    return
}

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw "vpk not found on PATH. Install it on the build host with 'dotnet tool install -g vpk'."
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

& vpk @vpkArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)." }

# Sign the produced executables (Setup.exe) through the signer interface, after packing.
. (Join-Path $PSScriptRoot '..\signing\signer.ps1')
$signerConfig = Get-BazaarSignerConfig
Get-ChildItem -LiteralPath $OutputDir -Filter '*.exe' -File | ForEach-Object {
    [void](Invoke-BazaarSigner -Package $_.FullName -Config $signerConfig)
}

$files = @(Get-ChildItem -LiteralPath $OutputDir -File | Where-Object { $_.Name -ne 'FEED-MANIFEST.txt' } | Sort-Object Name)
if ($files.Count -eq 0) { throw "vpk produced no files in $OutputDir." }
if (-not ($files | Where-Object { $_.Name -eq "releases.$vpkChannel.json" })) {
    throw "Expected feed releases.$vpkChannel.json was not produced; refusing to write a manifest."
}

$lines = @(
    "channel=$Channel",
    "vpk-channel=$vpkChannel",
    "ring=$Ring",
    "version=$Version",
    "signer=$($signerConfig.signer)",
    ''
)
foreach ($f in $files) {
    $hash = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $lines += "$hash  $($f.Name)"
}
Set-Content -LiteralPath (Join-Path $OutputDir 'FEED-MANIFEST.txt') -Value $lines -Encoding utf8
Write-Output "Packed $PackId $Version for channel $vpkChannel (ring $Ring): $($files.Count) files, manifest written. Nothing was uploaded."
