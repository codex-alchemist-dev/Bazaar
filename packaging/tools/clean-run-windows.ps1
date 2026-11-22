<#
.SYNOPSIS
    Checks that a published Windows client exe starts cleanly on a "machine that never ran Bazaar"
    (docs/design/37-distribution-matrix-and-packaging.md, section 4d).

.DESCRIPTION
    Runs the published Bazaar.Client.Windows.exe with an isolated data directory (BAZAAR_DATA_DIR plus
    USERPROFILE, APPDATA, LOCALAPPDATA and TEMP, all under -WorkDir, for the child process only). It writes no
    registry key, installs no service and registers no handler. It never modifies -PublishDir.

    Checks:
      1. files      the entry exe exists; every hash in SHA256SUMS next to it matches (when that file exists)
      2. self-check Bazaar.Client.Windows.exe --self-check exits 0 inside -TimeoutSeconds, report is valid
      3. log        the client log has no ERROR line
      4. gui        (-Gui only) the exe starts normally and gets a main window, then is closed

    On any failure it runs --support-bundle and prints the zip path. Exit code 0 only when no check failed.
    The script never deletes -WorkDir.

.PARAMETER PublishDir
    The release publish output (same flags as release.yml and publish-smoke.yml).

.PARAMETER WorkDir
    Required. Scratch folder for the isolated profile, report and bundle. Refused on the system drive
    unless -AllowSystemDrive is given (hosted CI runners only; never the owner's PC).

.EXAMPLE
    .\packaging\tools\clean-run-windows.ps1 -PublishDir V:\out\client -WorkDir V:\scratch\clean-run
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDir,
    [Parameter(Mandatory)][string]$WorkDir,
    [string]$ExeName = 'Bazaar.Client.Windows.exe',
    [int]$TimeoutSeconds = 60,
    [switch]$Gui,
    [switch]$AllowSystemDrive
)

$ErrorActionPreference = 'Stop'
$results = [System.Collections.Generic.List[object]]::new()

function Add-Result([string]$Name, [string]$Status, [string]$Detail) {
    $results.Add([pscustomobject]@{ Name = $Name; Status = $Status; Detail = $Detail })
    Write-Host ("{0,-5} {1,-11} {2}" -f $Status, $Name, $Detail)
}

$systemDrive = [System.IO.Path]::GetPathRoot($env:SystemRoot)
$fullWork = [System.IO.Path]::GetFullPath($WorkDir)
if (-not $AllowSystemDrive -and $fullWork.StartsWith($systemDrive, [System.StringComparison]::OrdinalIgnoreCase)) {
    Write-Error "Refusing -WorkDir '$fullWork' on the system drive $systemDrive. Use another drive, or -AllowSystemDrive on a disposable CI runner."
    exit 3
}

$exe = Join-Path (Resolve-Path -LiteralPath $PublishDir).ProviderPath $ExeName
$data = Join-Path $fullWork 'data'
$report = Join-Path $fullWork 'selfcheck.json'
$bundle = Join-Path $fullWork 'bundle.zip'
New-Item -ItemType Directory -Force -Path $data, (Join-Path $fullWork 'home'), (Join-Path $fullWork 'temp') | Out-Null

# The child process sees an empty profile. Our own session is untouched.
$isolation = @{
    BAZAAR_DATA_DIR = $data
    USERPROFILE     = Join-Path $fullWork 'home'
    APPDATA         = Join-Path $fullWork 'home\AppData\Roaming'
    LOCALAPPDATA    = Join-Path $fullWork 'home\AppData\Local'
    TEMP            = Join-Path $fullWork 'temp'
    TMP             = Join-Path $fullWork 'temp'
}

function Invoke-Client([string[]]$Arguments, [int]$Seconds) {
    $saved = @{}
    foreach ($k in $isolation.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $isolation[$k]) }
    try {
        $p = Start-Process -FilePath $exe -ArgumentList $Arguments -PassThru -WindowStyle Hidden
        if (-not $p.WaitForExit($Seconds * 1000)) {
            & taskkill.exe /PID $p.Id /T /F | Out-Null
            return @{ TimedOut = $true; ExitCode = $null }
        }
        return @{ TimedOut = $false; ExitCode = $p.ExitCode }
    }
    finally {
        foreach ($k in $isolation.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
    }
}

# 1. files
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    Add-Result 'files' 'FAIL' "missing $ExeName in the publish directory"
}
else {
    $sums = Join-Path (Split-Path $exe) 'SHA256SUMS'
    if (Test-Path -LiteralPath $sums) {
        $bad = @()
        foreach ($line in Get-Content -LiteralPath $sums) {
            if ($line -match '^([0-9a-fA-F]{64})\s+\*?(.+)$') {
                $file = Join-Path (Split-Path $exe) $Matches[2]
                if (-not (Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $Matches[1].ToUpperInvariant()) { $bad += $Matches[2] }
            }
        }
        if ($bad.Count -gt 0) { Add-Result 'files' 'FAIL' ("hash mismatch or missing: " + ($bad -join ', ')) }
        else { Add-Result 'files' 'PASS' 'exe present, SHA256SUMS match' }
    }
    else { Add-Result 'files' 'PASS' 'exe present (no SHA256SUMS to check)' }
}

# 2. self-check
if (($results | Where-Object Status -eq 'FAIL').Count -eq 0) {
    $run = Invoke-Client @('--self-check', '--report', $report) $TimeoutSeconds
    if ($run.TimedOut) { Add-Result 'self-check' 'FAIL' "did not exit within $TimeoutSeconds s" }
    elseif ($run.ExitCode -ne 0) {
        $detail = "exit code $($run.ExitCode)"
        if (Test-Path -LiteralPath $report) {
            $failed = (Get-Content -LiteralPath $report -Raw | ConvertFrom-Json).checks | Where-Object { -not $_.passed }
            if ($failed) { $detail += ': ' + (($failed | ForEach-Object { "$($_.id) ($($_.reason))" }) -join '; ') }
        }
        Add-Result 'self-check' 'FAIL' $detail
    }
    elseif (-not (Test-Path -LiteralPath $report) -or (Get-Content -LiteralPath $report -Raw | ConvertFrom-Json).schema -ne 'bazaar.selfcheck/1') {
        Add-Result 'self-check' 'FAIL' 'exit 0 but no valid bazaar.selfcheck/1 report'
    }
    else { Add-Result 'self-check' 'PASS' 'exit 0, report valid' }

    # 3. log
    $log = Join-Path $data 'logs\client.log'
    if (-not (Test-Path -LiteralPath $log)) { Add-Result 'log' 'FAIL' 'no client log was written' }
    elseif (Select-String -LiteralPath $log -Pattern ' ERROR ' -Quiet) { Add-Result 'log' 'FAIL' 'client log has an ERROR line' }
    else { Add-Result 'log' 'PASS' 'no ERROR line' }

    # 4. gui (optional)
    if ($Gui) {
        $saved = @{}
        foreach ($k in $isolation.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $isolation[$k]) }
        try {
            $p = Start-Process -FilePath $exe -PassThru
            $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
            while ((Get-Date) -lt $deadline -and -not $p.HasExited -and $p.MainWindowHandle -eq 0) { Start-Sleep -Milliseconds 500; $p.Refresh() }
            if ($p.HasExited) { Add-Result 'gui' 'FAIL' "exited early with code $($p.ExitCode)" }
            elseif ($p.MainWindowHandle -eq 0) { Add-Result 'gui' 'FAIL' "no main window within $TimeoutSeconds s" }
            else { Add-Result 'gui' 'PASS' 'main window appeared' }
            if (-not $p.HasExited) { & taskkill.exe /PID $p.Id /T /F | Out-Null }
        }
        finally { foreach ($k in $isolation.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) } }
    }
    else { Add-Result 'gui' 'SKIP' 'pass -Gui on a machine with an interactive desktop' }
}

$failed = @($results | Where-Object Status -eq 'FAIL')
if ($failed.Count -gt 0) {
    if (Test-Path -LiteralPath $exe) {
        $b = Invoke-Client @('--support-bundle', $bundle) $TimeoutSeconds
        if (Test-Path -LiteralPath $bundle) { Write-Host "Support bundle: $bundle" } else { Write-Host 'Support bundle could not be written.' }
    }
    Write-Host 'FAIL'
    exit 1
}
Write-Host 'PASS'
exit 0
