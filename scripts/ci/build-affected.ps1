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

# One temporary solution that lists only the affected projects (same idea as build-affected.sh): one restore, a parallel
# build, parallel tests, and a format check that loads only these projects. Android projects have their own CI job.
$slice = 'Bazaar.affected.slnx'
$all = @(($Projects + ' ' + $Tests) -split '\s+' | Where-Object { $_ -and $_ -notmatch 'Android' } | ForEach-Object { $_.Replace('\', '/') } | Sort-Object -Unique)
if ($all.Count -eq 0) { Write-Error 'no projects to put in the slice'; exit 1 }
$lines = @('<Solution>') + ($all | ForEach-Object { "  <Project Path=`"$_`" />" }) + @('</Solution>')
[System.IO.File]::WriteAllText((Join-Path (Get-Location) $slice), (($lines -join "`n") + "`n"))

Invoke-Step 'restore' { dotnet restore $slice }
Invoke-Step 'build' { dotnet build $slice -c Release --no-restore }
if ($Tests.Trim()) { Invoke-Step 'test' { dotnet test --solution $slice -c Release --no-build } }
Invoke-Step 'format' { dotnet format $slice --verify-no-changes --no-restore }
