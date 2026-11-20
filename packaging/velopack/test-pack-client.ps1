# Plain-PowerShell tests for pack-client.ps1 (no Pester). Run: pwsh packaging/velopack/test-pack-client.ps1
# Only -DryRun and fail-closed paths run, so no vpk, Windows host or certificate is needed.
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'pack-client.ps1'
$work = Join-Path ([System.IO.Path]::GetTempPath()) ('pack-client-test-' + [guid]::NewGuid().ToString('N'))
$pub = Join-Path $work 'publish'
$out = Join-Path $work 'out'
$emptyPub = Join-Path $work 'empty'
New-Item -ItemType Directory -Force -Path $pub, $emptyPub | Out-Null
Set-Content -Path (Join-Path $pub 'Bazaar.Client.Windows.exe') -Value 'x'

$failures = 0
function Run([string[]]$ScriptArgs) {
    $pwsh = (Get-Process -Id $PID).Path
    $o = & $pwsh -NoProfile -File $script @ScriptArgs 2>&1 | Out-String
    return [pscustomobject]@{ Code = $LASTEXITCODE; Out = $o }
}
function Check([bool]$cond, [string]$name) {
    if ($cond) { Write-Host "ok   $name" } else { Write-Host "FAIL $name"; $script:failures++ }
}
function Base([string]$v = '0.1.0', [string]$c = 'stable', [string]$p = $pub) {
    return @('-Version', $v, '-Channel', $c, '-PublishDir', $p, '-OutputDir', $out)
}

try {
    # DryRun for every channel: exit 0, shows the vpk command with the mapped channel flag.
    foreach ($c in 'stable', 'beta', 'alpha', 'nightly', 'lts') {
        $r = Run ((Base '0.1.0' $c) + '-DryRun')
        Check ($r.Code -eq 0) "dryrun $c exits 0"
        Check ($r.Out -match 'DRYRUN: vpk pack ') "dryrun $c prints vpk pack"
        Check ($r.Out -match "--channel win-x64-$c --runtime win-x64") "dryrun $c maps to --channel win-x64-$c"
        Check ($r.Out -match '--packVersion 0\.1\.0 ') "dryrun $c passes version"
    }
    # Ring is recorded but does not change the vpk channel; default ring is all.
    $r = Run ((Base '0.2.0-beta.1' 'beta') + @('-Ring', 'canary', '-DryRun'))
    Check (($r.Out -match 'ring=canary') -and ($r.Out -match '--channel win-x64-beta --runtime')) 'ring recorded, channel unchanged'
    $r = Run ((Base) + '-DryRun')
    Check ($r.Out -match 'ring=all') 'default ring is all'
    $r = Run ((Base '0.1.0+a1b2c3d') + '-DryRun')
    Check ($r.Code -eq 0) 'build metadata version accepted'
    Check (-not (Test-Path $out)) 'dryrun creates no output dir'

    # Fail closed.
    foreach ($bad in 'v0.1.0', '1.0', '01.0.0', 'abc', '') {
        $r = Run ((Base $bad) + '-DryRun')
        Check ($r.Code -ne 0) "bad version '$bad' fails"
    }
    foreach ($bad in 'Stable', 'win', 'dev', 'latest') {
        $r = Run ((Base '0.1.0' $bad) + '-DryRun')
        Check ($r.Code -ne 0) "bad channel '$bad' fails"
    }
    $r = Run ((Base) + @('-Ring', 'Bad Ring', '-DryRun'))
    Check ($r.Code -ne 0) 'bad ring fails'
    $r = Run ((Base '0.1.0' 'stable' (Join-Path $work 'missing')) + '-DryRun')
    Check ($r.Code -ne 0) 'missing publish dir fails'
    $r = Run ((Base '0.1.0' 'stable' $emptyPub) + '-DryRun')
    Check ($r.Code -ne 0) 'empty publish dir fails'
    if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
        $r = Run (Base)
        Check (($r.Code -ne 0) -and ($r.Out -match 'vpk not found')) 'missing vpk fails closed (no dry run)'
        Check (-not (Test-Path $out)) 'missing vpk creates no output dir'
    }
    else { Write-Host 'skip missing-vpk test (vpk is installed here)' }
}
finally {
    Remove-Item -Recurse -Force -Path $work -ErrorAction SilentlyContinue
}

if ($failures -eq 0) { Write-Host 'all pack-client tests passed' } else { Write-Host "$failures failure(s)"; exit 1 }
