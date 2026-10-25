#!/usr/bin/env pwsh
# Restore, build, test and format-check the Bazaar solution. Exits non-zero on any failure.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Invoke-Step {
    param([string]$Description, [scriptblock]$Command)
    Write-Host "==> $Description"
    & $Command
    if ($LASTEXITCODE -ne 0) {
        Write-Error "$Description failed with exit code $LASTEXITCODE"
        exit $LASTEXITCODE
    }
}

Invoke-Step 'restore' { dotnet restore Bazaar.slnx }
Invoke-Step 'build' { dotnet build Bazaar.slnx -c Release --no-restore }
Invoke-Step 'test' { dotnet test --solution Bazaar.slnx -c Release --no-build }
Invoke-Step 'format' { dotnet format Bazaar.slnx --verify-no-changes --no-restore }
