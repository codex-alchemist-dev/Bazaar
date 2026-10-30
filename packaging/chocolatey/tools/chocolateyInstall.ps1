$ErrorActionPreference = 'Stop'

$packageName = 'bazaar-client'
$toolsDir    = Split-Path -Parent $MyInvocation.MyCommand.Definition

# Filled by the release pipeline (docs/design/37-distribution-matrix-and-packaging.md).
# Never publish with the placeholders still in place.
$url64      = '{{PORTABLE_ZIP_URL}}'
$checksum64 = '{{PORTABLE_ZIP_SHA256}}'

if ($url64 -match '^\{\{' -or $checksum64 -match '^\{\{') {
    throw "${packageName}: release placeholders were not replaced. Refusing to install."
}

$packageArgs = @{
    PackageName    = $packageName
    Url64bit       = $url64
    UnzipLocation  = $toolsDir
    Checksum64     = $checksum64
    ChecksumType64 = 'sha256'
}
Install-ChocolateyZipPackage @packageArgs

# Start Menu shortcut only. Data under the user profile is left alone.
$exePath = Join-Path $toolsDir 'Bazaar.Client.Windows.exe'
$startMenu = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Bazaar Client.lnk'
Install-ChocolateyShortcut -ShortcutFilePath $startMenu -TargetPath $exePath
