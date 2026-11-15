# Signer interface for release packages (Windows MSIX today; other packages later).
#
# Dot-source this file. It exposes two functions:
#   Get-BazaarSignerConfig   reads packaging/signing/signer.json, then env overrides.
#   Invoke-BazaarSigner      signs one package with the configured signer.
#
# Signers:
#   none  leave the package unsigned (default in signer.json).
#   pfx   sign with signtool using the company certificate in a PFX file.
#
# Settings (signer.json; env overrides win):
#   signer            BAZAAR_SIGNER              none | pfx
#   pfxPath           BAZAAR_SIGN_PFX_PATH       path to the company PFX (the cert location)
#   timestampUrl      BAZAAR_SIGN_TIMESTAMP_URL  RFC 3161 timestamp server (optional)
#
# The PFX password is never stored in a file or on the command line. It is read from
# BAZAAR_SIGN_PFX_PASSWORD only. The company server sets that variable; no repository
# secret exists for it.

$ErrorActionPreference = 'Stop'

function Get-BazaarSignerConfig {
    [CmdletBinding()]
    param(
        [string]$ConfigPath = (Join-Path $PSScriptRoot 'signer.json')
    )

    $config = [pscustomobject]@{ signer = 'none'; pfxPath = ''; timestampUrl = '' }
    if (Test-Path $ConfigPath) {
        $file = Get-Content $ConfigPath -Raw | ConvertFrom-Json
        foreach ($name in 'signer', 'pfxPath', 'timestampUrl') {
            if ($file.PSObject.Properties.Name -contains $name) { $config.$name = [string]$file.$name }
        }
    }

    if ($env:BAZAAR_SIGNER)             { $config.signer = $env:BAZAAR_SIGNER }
    if ($env:BAZAAR_SIGN_PFX_PATH)      { $config.pfxPath = $env:BAZAAR_SIGN_PFX_PATH }
    if ($env:BAZAAR_SIGN_TIMESTAMP_URL) { $config.timestampUrl = $env:BAZAAR_SIGN_TIMESTAMP_URL }

    if ($config.signer -notin 'none', 'pfx') {
        throw "Unknown signer '$($config.signer)'. Use 'none' or 'pfx'."
    }
    return $config
}

function Invoke-BazaarSigner {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Package,
        [Parameter(Mandatory)] [pscustomobject]$Config
    )

    switch ($Config.signer) {
        'none' {
            Write-Host "Signer 'none': $Package left unsigned."
            return $false
        }
        'pfx' {
            if (-not $Config.pfxPath) { throw "Signer 'pfx' needs pfxPath (signer.json or BAZAAR_SIGN_PFX_PATH)." }
            if (-not (Test-Path $Config.pfxPath)) { throw "Signing certificate not found: $($Config.pfxPath)" }
            if (-not $env:BAZAAR_SIGN_PFX_PASSWORD) { throw 'Set BAZAAR_SIGN_PFX_PASSWORD to unlock the signing certificate.' }
            if (-not (Test-Path $Package)) { throw "Package not found: $Package" }

            $signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
                Sort-Object FullName -Descending |
                Select-Object -First 1
            if (-not $signtool) { throw 'signtool.exe not found. Install the Windows 10/11 SDK.' }

            $signArgs = @('sign', '/fd', 'SHA256', '/f', $Config.pfxPath, '/p', $env:BAZAAR_SIGN_PFX_PASSWORD)
            if ($Config.timestampUrl) { $signArgs += @('/tr', $Config.timestampUrl, '/td', 'SHA256') }
            $signArgs += $Package

            & $signtool.FullName @signArgs
            if ($LASTEXITCODE -ne 0) { throw "signtool sign failed ($LASTEXITCODE)." }
            Write-Host "Signed $Package with the company certificate."
            return $true
        }
    }
}
