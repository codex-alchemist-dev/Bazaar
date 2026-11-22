<#
.SYNOPSIS
  Clean-machine install of Bazaar.Server.Host on Windows, non-interactive and fully logged (docs 03, 37, 45 sections 1.1, 1.5, 1.9).
.DESCRIPTION
  Mirror of install-linux.sh. Uses the packaged PostgreSQL 18 + pgvector bundle (packaging/postgres): pass -PgBundle to use one that is
  already built, or -BuildBundle to build it here (needs the MSVC build tools on PATH, as in .github/workflows/postgres-bundle.yml).
  Then: .NET SDK check, initdb and start, role and database, publish, migrate, host start, setup plan through the provisioning API,
  smoke checks. Secrets are generated into files, never written to the log. Exit 0 = smoke checks passed.
  It installs no Windows service and writes only under -Prefix, so it is safe to run on a runner; it never touches other drives.
.EXAMPLE
  ./scripts/install/install-windows.ps1 -Prefix D:\bazaar-install -PgBundle .\artifacts\pg-bundle
#>
[CmdletBinding()]
param(
    [string]$Prefix = (Join-Path $env:ProgramData 'Bazaar'),
    [string]$Log = '',
    [string]$PgBundle = '',
    [switch]$BuildBundle,
    [int]$StatusPort = 7390,
    [int]$PgPort = 54329,
    [switch]$KeepRunning
)
$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Log) { $Log = Join-Path $Root ("install-{0}.log" -f (Get-Date -Format 'yyyyMMddTHHmmssZ')) }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Log), $Prefix | Out-Null
Set-Content -Path $Log -Value '' -Encoding utf8
$script:Secrets = [System.Collections.Generic.List[string]]::new()
$DataRoot = Join-Path $Prefix 'data'; $AppDir = Join-Path $Prefix 'app'; $SecretsDir = Join-Path $Prefix 'secrets'
$HostLog = Join-Path $Prefix 'host.log'; $HostErr = Join-Path $Prefix 'host.err.log'
$Status = "http://127.0.0.1:$StatusPort"
$script:HostProc = $null; $script:Step = 0; $script:StepName = ''; $script:StepT0 = Get-Date

function Mask([string]$s) { foreach ($v in $script:Secrets) { if ($v) { $s = $s.Replace($v, '***') } }; $s }
function Log([string]$m) { $ts = (Get-Date).ToUniversalTime().ToString('s') + 'Z'; $line = "$ts $(Mask $m)"; Add-Content -Path $Log -Value $line -Encoding utf8; Write-Host $line }
function Step([string]$name) { $script:Step++; $script:StepName = $name; $script:StepT0 = Get-Date; Log "=== step $($script:Step): $name" }
function Ok { Log ("--- ok: {0} ({1}s)" -f $script:StepName, [int]((Get-Date) - $script:StepT0).TotalSeconds) }
function Fail([string]$m) { Log "!!! FAILED at step $($script:Step) ($($script:StepName)): $m"; Log "log: $Log"; Stop-Host; exit 1 }
function Stop-Host {
    if ($script:HostProc -and -not $script:HostProc.HasExited -and -not $KeepRunning) { Stop-Process -Id $script:HostProc.Id -Force -ErrorAction SilentlyContinue; $script:HostProc.WaitForExit(30000) | Out-Null }
    $script:HostProc = $null
}
# Runs a program with stdio captured to files and a hard timeout (a server that inherits a pipe would hold the step open forever).
function Run([string]$Exe, [string[]]$Arguments, [int]$TimeoutSeconds = 600, [switch]$AllowFail) {
    Log ('$ ' + $Exe + ' ' + ($Arguments -join ' '))
    $out = [IO.Path]::GetTempFileName(); $err = [IO.Path]::GetTempFileName()
    # Start-Process joins the array with plain spaces, so quote any argument that holds whitespace or a quote (Windows command-line rules).
    $quoted = $Arguments | ForEach-Object { if ($_ -match '[\s"]' -or $_ -eq '') { '"' + ($_ -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"' } else { $_ } }
    $p = Start-Process -FilePath $Exe -ArgumentList $quoted -NoNewWindow -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) { $p.Kill(); Log '  timed out'; $code = 124 } else { $p.WaitForExit(); $code = $p.ExitCode }
    foreach ($f in $out, $err) { Get-Content $f -ErrorAction SilentlyContinue | ForEach-Object { Log "  $_" }; Remove-Item $f -ErrorAction SilentlyContinue }
    Log "  exit=$code"
    if ($code -ne 0 -and -not $AllowFail) { Fail "$([IO.Path]::GetFileName($Exe)) exited $code" }
    $code
}
function New-Secret { $b = [byte[]]::new(24); [Security.Cryptography.RandomNumberGenerator]::Fill($b); ($b | ForEach-Object { $_.ToString('x2') }) -join '' }
function Read-Shared([string]$Path) {
    if (-not (Test-Path $Path)) { return '' }
    $fs = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite'); try { (New-Object IO.StreamReader $fs).ReadToEnd() } finally { $fs.Dispose() }
}

Step 'preflight'
Log ("os={0} arch={1} cpus={2} commit={3}" -f [Environment]::OSVersion.VersionString, $env:PROCESSOR_ARCHITECTURE, [Environment]::ProcessorCount, (& git -C $Root rev-parse --short HEAD 2>$null))
Log "prefix=$Prefix repo=$Root"
if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') { Fail 'this script is for Windows; use install-linux.sh on Linux' }
Ok

Step 'prerequisites (PostgreSQL 18 + pgvector bundle, .NET SDK)'
if ($BuildBundle) {
    $PgBundle = Join-Path $Prefix 'pg-bundle'
    Run 'pwsh' @('-NoProfile', '-File', (Join-Path $Root 'packaging\postgres\build-pgvector-windows.ps1'), '-OutDir', $PgBundle, '-Work', (Join-Path $Prefix 'pg-bundle-work')) 3000 | Out-Null
}
if (-not $PgBundle) { Fail 'pass -PgBundle <dir containing 18\bin> or -BuildBundle' }
$PgBin = Join-Path (Resolve-Path $PgBundle) '18\bin'
if (-not (Test-Path (Join-Path $PgBin 'initdb.exe'))) { Fail "no PostgreSQL 18 bundle at $PgBin" }
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)
if (-not $dotnet) { Fail '.NET SDK not found on PATH (install the SDK named in global.json, for example with dotnet-install.ps1)' }
Run $dotnet.Source @('--version') | Out-Null
Ok

Step 'PostgreSQL cluster, role, database, pgvector'
New-Item -ItemType Directory -Force -Path $SecretsDir, $DataRoot | Out-Null
$pwFile = Join-Path $SecretsDir 'pg-password'
if (-not (Test-Path $pwFile)) { Set-Content -Path $pwFile -Value (New-Secret) -NoNewline }
# Only the current user may read the secrets folder.
Run 'icacls' @($SecretsDir, '/inheritance:r', '/grant:r', "$($env:USERNAME):(OI)(CI)F") -AllowFail | Out-Null
$pgPw = (Get-Content $pwFile -Raw).Trim(); $script:Secrets.Add($pgPw)
$pgData = Join-Path $Prefix 'pgdata'
if (-not (Test-Path (Join-Path $pgData 'PG_VERSION'))) {
    $su = Join-Path $SecretsDir 'pg-superuser'; Set-Content -Path $su -Value $pgPw -NoNewline
    Run (Join-Path $PgBin 'initdb.exe') @('-D', $pgData, '-U', 'postgres', '-A', 'scram-sha-256', "--pwfile=$su", '--encoding=UTF8') | Out-Null
    Remove-Item $su
    Add-Content -Path (Join-Path $pgData 'postgresql.conf') -Value "listen_addresses = '127.0.0.1'`nport = $PgPort"
}
Run (Join-Path $PgBin 'pg_ctl.exe') @('-D', $pgData, '-l', (Join-Path $Prefix 'pg.log'), '-w', 'start') 120 | Out-Null
$env:PGPASSWORD = $pgPw
$psql = Join-Path $PgBin 'psql.exe'
function Sql([string]$Db, [string]$Q) { Run $psql @('-h', '127.0.0.1', '-p', "$PgPort", '-U', 'postgres', '-d', $Db, '-X', '-At', '-v', 'ON_ERROR_STOP=1', '-c', $Q) | Out-Null }
# CREATEROLE: the core migrations create the NOLOGIN roles bazaar_app and bazaar_erasure.
Sql 'postgres' "DO `$`$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='bazaar') THEN CREATE ROLE bazaar LOGIN CREATEROLE PASSWORD '$pgPw'; END IF; END `$`$;"
Sql 'postgres' "ALTER ROLE bazaar LOGIN CREATEROLE PASSWORD '$pgPw'"
if ((& $psql -h 127.0.0.1 -p $PgPort -U postgres -d postgres -At -c "SELECT 1 FROM pg_database WHERE datname='bazaar'") -ne '1') { Sql 'postgres' 'CREATE DATABASE bazaar OWNER bazaar' }
Sql 'bazaar' 'CREATE EXTENSION IF NOT EXISTS vector'
Sql 'bazaar' 'SELECT version(), uuidv7() IS NOT NULL, (SELECT extversion FROM pg_extension WHERE extname=''vector'')'
$env:BAZAAR_DB = "Host=127.0.0.1;Port=$PgPort;Database=bazaar;Username=bazaar;Password=$pgPw"
Ok

Step 'publish Bazaar.Server.Host'
Run $dotnet.Source @('publish', (Join-Path $Root 'src\Bazaar.Server.Host\Bazaar.Server.Host.csproj'), '-c', 'Release', '--self-contained', 'false', '-o', $AppDir, '-v:m', '-nologo', '-clp:ErrorsOnly;Summary') 1800 | Out-Null
if (-not (Test-Path (Join-Path $AppDir 'Bazaar.Server.Host.dll'))) { Fail 'publish produced no Bazaar.Server.Host.dll' }
Ok

$HostArgs = @('Bazaar.Server.Host.dll', "--Bazaar:Host:DataRoot=$DataRoot", '--Bazaar:Host:DatabaseConnectionReference=env:BAZAAR_DB', "--Bazaar:Status:Port=$StatusPort")
Step 'migrate the schema'
Run $dotnet.Source (@((Join-Path $AppDir 'Bazaar.Server.Host.dll'), 'migrate', "--Bazaar:Host:DataRoot=$DataRoot", '--Bazaar:Host:DatabaseConnectionReference=env:BAZAAR_DB')) | Out-Null
Ok

# Starts the host and waits until it is live and (while no owner exists) has printed its one-time bootstrap code.
function Start-BazaarHost {
    for ($attempt = 1; $attempt -le 6; $attempt++) {
        Remove-Item $HostLog, $HostErr -ErrorAction SilentlyContinue
        $script:HostProc = Start-Process -FilePath $dotnet.Source -ArgumentList $HostArgs -WorkingDirectory $AppDir -NoNewWindow -PassThru -RedirectStandardOutput $HostLog -RedirectStandardError $HostErr
        Log "host pid=$($script:HostProc.Id) (attempt $attempt), output in $HostLog"
        $script:Code = ''; $ready = $false
        for ($i = 1; $i -le 90; $i++) {
            if ($script:HostProc.HasExited) { break }
            if ((Read-Shared $HostLog) -match 'X-Bazaar-Bootstrap-Code header\): ([A-Za-z0-9_-]+)') { $script:Code = $Matches[1] }
            try { $live = (Invoke-WebRequest "$Status/health/live" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200 } catch { $live = $false }
            if ($live -and ($script:Code -or $i -ge 20)) { $ready = $true; break }
            Start-Sleep -Seconds 1
        }
        if ($ready -and -not $script:HostProc.HasExited) { if ($script:Code) { $script:Secrets.Add($script:Code) }; return }
        if ((Read-Shared $HostLog) -match 'instance lock' -and $attempt -lt 6) { Log 'instance lock still held by the previous process; retrying in 3 s'; Stop-Host; Start-Sleep -Seconds 3; continue }
        Log 'host output:'; (Read-Shared $HostLog) -split "`n" | Select-Object -Last 40 | ForEach-Object { Log "  $_" }
        Fail 'the host did not become live'
    }
}

Step 'start the host'
Start-BazaarHost
Ok

Step 'non-interactive setup through the provisioning API (doc 45 section 1.5 and 1.9)'
$setupResult = 'skipped'
if (-not $script:Code) { Log 'no bootstrap code printed (an owner may already exist); setup API calls skipped' }
else {
    $kek = (Join-Path $SecretsDir 'kek.bin') -replace '\\', '/'
    $plan = [ordered]@{
        schemaVersion = 1
        install = [ordered]@{ companyName = 'Clean Install Co'; companySlug = 'clean-install-co'; locale = 'en'; timezone = 'UTC'; releaseChannel = 'beta' }
        database = @{ postgresMajor = 18 }
        custody = [ordered]@{ kekProvider = 'file'; kekRef = "file:$kek" }
        owner = [ordered]@{ displayName = 'Owner'; requireSecondFactor = $false }
        privacy = @{ profile = 'private' }
        backups = @{ acknowledgeNoTarget = $true }
        company = @{ source = 'template:starter@1.0.0' }
    }
    $planJson = $plan | ConvertTo-Json -Depth 6
    $planFile = Join-Path $Prefix 'bazaar-setup.json'; Set-Content -Path $planFile -Value $planJson -Encoding utf8
    Log "plan written to $planFile (references only, no secret values)"
    function Api([string]$Method, [string]$Path, [string]$Body) {
        try { $r = Invoke-WebRequest "$Status$Path" -Method $Method -Body $Body -ContentType 'application/json' -Headers @{ 'X-Bazaar-Bootstrap-Code' = $script:Code } -UseBasicParsing -TimeoutSec 120; [pscustomobject]@{ Code = [int]$r.StatusCode; Body = $r.Content } }
        catch { $resp = $_.Exception.Response; $b = ''; if ($resp) { try { $b = (New-Object IO.StreamReader $resp.GetResponseStream()).ReadToEnd() } catch { $b = $_.ToString() } }; [pscustomobject]@{ Code = $(if ($resp) { [int]$resp.StatusCode } else { 0 }); Body = $b } }
    }
    $v = Api 'POST' '/setup/plans' $planJson
    Log "POST /setup/plans -> HTTP $($v.Code): $($v.Body)"
    $hash = if ($v.Code -eq 200) { ($v.Body | ConvertFrom-Json).planHash } else { '' }
    if (-not $hash) { Fail "the setup plan was not accepted: validate HTTP $($v.Code)" }
    Log "plan is valid, hash $hash; restarting the host for a fresh bootstrap code (the code is single use)"
    Stop-Host; Start-BazaarHost
    if (-not $script:Code) { Fail 'no bootstrap code after the restart' }
    $a = Api 'POST' "/setup/plans/$hash/apply" $planJson
    Log "POST /setup/plans/$hash/apply -> HTTP $($a.Code): $($a.Body)"
    $setupResult = "valid, apply HTTP $($a.Code)"
    if ($a.Code -ne 200) { Log 'NOTE: apply did not complete; doc 45 section 1.9 says no apply step is registered on main yet, so exit 3 is expected until the setup wizard apply steps land' }
}
Ok

Step 'smoke checks'
$fail = $false
foreach ($c in @(@('health/live', '/health/live'), @('health/ready', '/health/ready'), @('status', '/status'))) {
    try { $r = Invoke-WebRequest "$Status$($c[1])" -UseBasicParsing -TimeoutSec 10; $code = [int]$r.StatusCode; $body = $r.Content }
    catch { $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }; $body = '' }
    $shown = if ($body.Length -gt 400) { $body.Substring(0, 400) } else { $body }
    Log "$($c[0]): HTTP $code $shown"
    if ($code -ne 200) { Log "CHECK FAILED: $($c[0])"; $fail = $true }
}
if ($fail) { Fail 'one or more smoke checks failed' }
Ok

Log ("=== SUMMARY: install ok in {0}s; setup: {1}; host log {2}; install log {3}" -f [int]((Get-Date) - $script:StepT0).TotalSeconds, $setupResult, $HostLog, $Log)
Stop-Host
Run (Join-Path $PgBin 'pg_ctl.exe') @('-D', $pgData, '-m', 'fast', '-w', 'stop') 120 -AllowFail | Out-Null
exit 0
