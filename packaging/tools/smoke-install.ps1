<#
.SYNOPSIS
    Silently installs the Bazaar.Server Windows MSI, checks the service, then uninstalls it.

.DESCRIPTION
    This script changes machine state: it runs msiexec against the machine, registers the
    Bazaar.Server Windows service, and removes it again. It refuses to run unless
    -ConfirmMachineInstall is passed. It must be run from an elevated PowerShell.

    Steps:
      1. Refuse unless -ConfirmMachineInstall is set.
      2. Require an elevated session.
      3. Resolve the MSI (-MsiPath, or the single .msi under packaging/windows-msi/).
      4. Install silently with msiexec /qn /norestart and a verbose log.
      5. Check that service Bazaar.Server exists and its start mode is Auto.
      6. Uninstall silently (always attempted once the install succeeded).

    Data under %ProgramData% is kept by the MSI on uninstall (doc 37, section 5).

.PARAMETER ConfirmMachineInstall
    Required. Acknowledges that the script installs and removes a machine-level service.

.PARAMETER MsiPath
    Path to the server MSI. Defaults to the single .msi found under packaging/windows-msi/.

.PARAMETER LogDir
    Directory for msiexec logs. Defaults to the system temp directory.

.EXAMPLE
    .\packaging\tools\smoke-install.ps1 -ConfirmMachineInstall

.EXAMPLE
    .\packaging\tools\smoke-install.ps1 -ConfirmMachineInstall -MsiPath C:\build\Bazaar.Server.msi
#>
[CmdletBinding()]
param(
    [switch]$ConfirmMachineInstall,
    [string]$MsiPath,
    [string]$LogDir = [System.IO.Path]::GetTempPath()
)

$ErrorActionPreference = 'Stop'

$serviceName = 'Bazaar.Server'
# msiexec success codes: 0 = success, 3010 = success, reboot required.
$msiSuccessCodes = @(0, 3010)

function Write-Step([string]$Message) {
    Write-Host "[smoke-install] $Message"
}

# 1. Refuse without explicit confirmation.
if (-not $ConfirmMachineInstall) {
    Write-Error ("Refusing to run: this script installs and removes the $serviceName service on this machine. " +
        "Re-run with -ConfirmMachineInstall to proceed.")
    exit 2
}

# 2. Require elevation.
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error 'Run this script from an elevated (Administrator) PowerShell session.'
    exit 3
}

# 3. Resolve the MSI.
if (-not $MsiPath) {
    $msiDir = Join-Path $PSScriptRoot '..\windows-msi'
    if (-not (Test-Path -LiteralPath $msiDir)) {
        Write-Error "No -MsiPath given and $msiDir does not exist."
        exit 4
    }
    $found = @(Get-ChildItem -LiteralPath $msiDir -Filter *.msi -Recurse -File)
    if ($found.Count -ne 1) {
        Write-Error "Expected exactly one .msi under $msiDir, found $($found.Count). Pass -MsiPath."
        exit 4
    }
    $MsiPath = $found[0].FullName
}
$MsiPath = (Resolve-Path -LiteralPath $MsiPath).ProviderPath
if (-not (Test-Path -LiteralPath $MsiPath -PathType Leaf) -or [IO.Path]::GetExtension($MsiPath) -ne '.msi') {
    Write-Error "Not an MSI file: $MsiPath"
    exit 4
}
Write-Step "MSI: $MsiPath"

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$installLog = Join-Path $LogDir "bazaar-server-install-$stamp.log"
$uninstallLog = Join-Path $LogDir "bazaar-server-uninstall-$stamp.log"

function Invoke-Msiexec([string[]]$Arguments, [string]$Log) {
    $args = $Arguments + @('/qn', '/norestart', '/l*v', $Log)
    $process = Start-Process -FilePath 'msiexec.exe' -ArgumentList $args -Wait -PassThru
    return $process.ExitCode
}

$exitCode = 0
$installed = $false

try {
    # 4. Silent install.
    Write-Step "Installing silently (log: $installLog)"
    $code = Invoke-Msiexec @('/i', "`"$MsiPath`"") $installLog
    if ($code -notin $msiSuccessCodes) {
        throw "msiexec install failed with exit code $code. See $installLog"
    }
    $installed = $true
    if ($code -eq 3010) { Write-Step 'Install reports a reboot is required (3010); continuing.' }

    # 5. Service checks.
    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    if (-not $service) {
        throw "Service '$serviceName' was not registered by the MSI."
    }
    Write-Step "Service '$serviceName' exists (state: $($service.State))."
    if ($service.StartMode -ne 'Auto') {
        throw "Service '$serviceName' start mode is '$($service.StartMode)', expected 'Auto'."
    }
    Write-Step "Service '$serviceName' start mode is Auto."
}
catch {
    Write-Host "[smoke-install] FAILED: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
}
finally {
    # 6. Uninstall whenever the install succeeded, even if a check failed.
    if ($installed) {
        Write-Step "Uninstalling silently (log: $uninstallLog)"
        $code = Invoke-Msiexec @('/x', "`"$MsiPath`"") $uninstallLog
        if ($code -notin $msiSuccessCodes) {
            Write-Host "[smoke-install] FAILED: msiexec uninstall exit code $code. See $uninstallLog" -ForegroundColor Red
            $exitCode = 1
        }
        else {
            $remaining = Get-CimInstance -ClassName Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
            if ($remaining) {
                Write-Host "[smoke-install] FAILED: service '$serviceName' still present after uninstall." -ForegroundColor Red
                $exitCode = 1
            }
            else {
                Write-Step 'Uninstall complete; service removed. Data under %ProgramData% is kept.'
            }
        }
    }
}

if ($exitCode -eq 0) { Write-Step 'PASS' } else { Write-Step 'FAIL' }
exit $exitCode
