#!/usr/bin/env pwsh
# Pull request build and test limited to the projects the change can affect (see affected.py).
# Usage: build-affected.ps1 -Projects "<csproj paths>" -Tests "<test csproj paths>"  (space separated)
# Same steps as build-affected.sh.
param([string]$Projects = '', [string]$Tests = '')
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '../..')
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
foreach ($p in ($Projects -split '\s+' | Where-Object { $_ -and $_ -notmatch 'Android' })) {
    Invoke-Step "build $p" { dotnet build $p -c Release --no-restore }
}
foreach ($t in ($Tests -split '\s+' | Where-Object { $_ })) {
    Invoke-Step "test $t" { dotnet test --project $t -c Release --no-build }
}
Invoke-Step 'format' { dotnet format Bazaar.slnx --verify-no-changes --no-restore }
