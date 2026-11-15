$ErrorActionPreference = 'Stop'

$packageName = 'bazaar-client'

# Remove only the shortcut this package created. The extracted files are removed by Chocolatey.
# User data under the profile is kept on purpose.
$startMenu = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Bazaar Client.lnk'
if (Test-Path -LiteralPath $startMenu) {
    Remove-Item -LiteralPath $startMenu -Force
    Write-Host "${packageName}: removed Start Menu shortcut."
}
