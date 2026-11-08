# S25 restore drill, Windows service-account half, one run. Same SQL and the same P1 to P4 as run_drill.ps1.
# The source is the registered Windows service "postgres-s25" (NetworkService), already running on port 54420.
# Each run gets its own database in that cluster; the base backup, WAL archive and restore are as in run_drill.ps1.
# Usage: pwsh -File run_drill_service.ps1 -Run 1 -PgBin <...\pgsql\bin> -Work <dir outside the repo> -Data <service data dir> -Wal <archive dir>
param([int]$Run, [string]$PgBin, [string]$Work, [string]$Data, [string]$Wal, [int]$SrcPort = 54420, [int]$APerGc = 100000, [int]$BPerGc = 20000)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$sql = Join-Path (Split-Path -Parent $here) 'sql'
$db = "s25_r$Run"
$rundir = Join-Path $Work "svc-run$Run"
$out = Join-Path $rundir 'results'
$base = Join-Path $rundir 'base'
$rstDir = Join-Path $rundir 'restore'
$rstPort = 55460 + $Run
function Log($m) { Write-Host ("[svc run $Run {0:HH:mm:ss}] $m" -f (Get-Date).ToUniversalTime()) }
function Ms { [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() }
function Psql($port, [string]$database, [string[]]$a, $inputFile) {
    $args2 = @('-X', '-v', 'ON_ERROR_STOP=1', '-h', '127.0.0.1', '-p', $port, '-U', 'postgres', '-d', $database, '-At', '-F', ',') + $a
    if ($inputFile) { $r = Get-Content $inputFile -Raw | & "$PgBin\psql.exe" @args2 -f - } else { $r = & "$PgBin\psql.exe" @args2 }
    if ($LASTEXITCODE -ne 0) { throw "psql failed: $a" }
    $r
}
function Start-PgCtl($dir, $log, $port) {
    Start-Process "$PgBin\pg_ctl.exe" -ArgumentList @('start', '-D', "`"$dir`"", '-l', "`"$log`"") -WindowStyle Hidden -RedirectStandardOutput "$dir.ctl.txt"
    for ($i = 0; $i -lt 240; $i++) { & "$PgBin\pg_isready.exe" -q -h 127.0.0.1 -p $port; if ($LASTEXITCODE -eq 0) { return }; Start-Sleep -Milliseconds 500 }
    throw "server on $port did not start"
}
function Stop-Pg($dir) { & "$PgBin\pg_ctl.exe" stop -D $dir -m immediate 2>&1 | Out-Null }

& "$PgBin\pg_isready.exe" -q -h 127.0.0.1 -p $SrcPort
if ($LASTEXITCODE -ne 0) { throw "service cluster on $SrcPort is not running" }
if (Test-Path $rundir) { Remove-Item $rundir -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null
$timings = @()

# ---- Source: a fresh database in the service cluster ----
Psql $SrcPort 'postgres' @('-c', "DROP DATABASE IF EXISTS $db WITH (FORCE)") | Out-Null
Psql $SrcPort 'postgres' @('-c', "CREATE DATABASE $db") | Out-Null
Psql $SrcPort $db @('-q') "$sql\schema.sql" | Out-Null
Psql $SrcPort $db @('-q') "$sql\functions.sql" | Out-Null
Log "service source ready, database $db"

$t0 = Ms
& "$PgBin\pg_basebackup.exe" -h 127.0.0.1 -p $SrcPort -U postgres -D $base -Fp -X stream -c fast -l "s25 svc run $Run"
if ($LASTEXITCODE -ne 0) { throw 'pg_basebackup failed' }
$timings += "base_backup_ms,$((Ms) - $t0)"
Log 'base backup taken'

function Wait-Archived($want) {
    for ($i = 0; $i -lt 1200; $i++) {
        $last = (Psql $SrcPort 'postgres' @('-c', "select coalesce(last_archived_wal,'') from pg_stat_archiver")) -join ''
        if ($last -and $last -ge $want) { return }
        Start-Sleep -Milliseconds 250
    }
    throw "timeout waiting for archive of $want"
}

# ---- Batch A (before T) ----
$t0 = Ms
Psql $SrcPort $db @('-c', "select append_messages(g, $APerGc) from generate_series(1,4) g") | Out-Null
$timings += "batch_a_ms,$((Ms) - $t0)"
$seg = (Psql $SrcPort 'postgres' @('-c', 'select pg_walfile_name(pg_current_wal_lsn())')) -join ''
Psql $SrcPort 'postgres' @('-c', 'select pg_switch_wal()') | Out-Null
Wait-Archived $seg
Start-Sleep 1
$T = (Psql $SrcPort $db @('-c', 'select clock_timestamp()')) -join ''
Psql $SrcPort $db @('-c', 'select pg_sleep(1)') | Out-Null
Psql $SrcPort $db @() "$sql\snapshot.sql" | Set-Content "$out\expected_at_T.csv"
Log "batch A archived; T=$T"

# ---- Batch B (after T; the range the rollback discards) ----
Psql $SrcPort $db @('-c', "select append_messages(g, $BPerGc) from generate_series(1,4) g") | Out-Null
$tBEnd = Ms
Psql $SrcPort $db @() "$sql\snapshot.sql" | Set-Content "$out\source_before_crash.csv"
Psql $SrcPort $db @('-c', 'select gc_id, head_seq from gc order by gc_id') | Set-Content "$out\seen.csv"
$seg = (Psql $SrcPort 'postgres' @('-c', 'select pg_walfile_name(pg_current_wal_lsn())')) -join ''
Psql $SrcPort 'postgres' @('-c', 'select pg_switch_wal()') | Out-Null
Wait-Archived $seg
$timings += "archive_lag_ms,$((Ms) - $tBEnd)"
Log 'batch B archived; service postmaster killed next'

# Crash the service postmaster tree (no clean shutdown). Killing a NetworkService process needs elevation.
$pmPid = (Get-Content "$Data\postmaster.pid" -TotalCount 1)
& sudo taskkill.exe /F /T /PID $pmPid | Out-Null
for ($i = 0; $i -lt 60; $i++) { & "$PgBin\pg_isready.exe" -q -h 127.0.0.1 -p $SrcPort; if ($LASTEXITCODE -ne 0) { break }; Start-Sleep -Milliseconds 250 }
Log 'service postmaster killed'

# ---- Restore to T ----
Copy-Item $base $rstDir -Recurse
$walConf = $Wal -replace '\\', '\\'
Add-Content "$rstDir\postgresql.auto.conf" @"
port = $rstPort
restore_command = 'copy "$walConf\\%f" "%p"'
recovery_target_time = '$T'
recovery_target_action = 'promote'
archive_mode = 'off'
"@
New-Item "$rstDir\recovery.signal" -ItemType File | Out-Null
$t0 = Ms
Start-PgCtl $rstDir "$rundir\restore.log" $rstPort
$state = ''
for ($i = 0; $i -lt 2400; $i++) {
    try { $state = (Psql $rstPort 'postgres' @('-c', 'select pg_is_in_recovery()')) -join '' } catch { $state = '' }
    if ($state -eq 'f') { break }
    Start-Sleep -Milliseconds 250
}
if ($state -ne 'f') { throw 'restore did not promote' }
$timings += "restore_to_promote_ms,$((Ms) - $t0)"
Log 'restored and promoted'

# P1
Psql $rstPort $db @() "$sql\snapshot.sql" | Set-Content "$out\restored_at_T.csv"
$replay = (Psql $rstPort 'postgres' @('-c', "select pg_last_xact_replay_timestamp() <= '$T'::timestamptz")) -join ''
$afterT = (Psql $rstPort $db @('-c', "select count(*) = 0 from gc_message where created_at > '$T'::timestamptz")) -join ''
$expected = Get-Content "$out\expected_at_T.csv"
$restored = Get-Content "$out\restored_at_T.csv"
$countsEq = (($expected -join "`n") -eq ($restored -join "`n"))

# P2 / P3
foreach ($line in Get-Content "$out\seen.csv") {
    $gc, $seen = $line -split ','
    Psql $rstPort $db @('-c', "select apply_recovery($gc, $seen, clock_timestamp())") | Out-Null
}
Psql $rstPort $db @() "$sql\verify_recovery.sql" | Set-Content "$out\verify_recovery.csv"
Psql $rstPort $db @('-c', 'select append_messages(g, 1) from generate_series(1,4) g') | Out-Null
Psql $rstPort $db @('-c', 'select gc_id, max(seq) from gc_message group by gc_id order by gc_id') | Set-Content "$out\max_after_append.csv"
Psql $rstPort $db @() "$sql\verify_recovery.sql" | Set-Content "$out\verify_after_append.csv"

# ---- Judge (same rules as run_drill.ps1) ----
$ver = @(Get-Content "$out\verify_recovery.csv")
$aft = @(Get-Content "$out\verify_after_append.csv")
$p23 = ($ver.Count -eq 4) -and -not ($ver | Where-Object { $_ -match ',f' }) -and -not ($aft | ForEach-Object { ($_ -split ',')[4..7] } | Where-Object { $_ -eq 'f' })
$maxAfter = @{}; foreach ($l in Get-Content "$out\max_after_append.csv") { $a, $b = $l -split ','; $maxAfter[$a] = [int64]$b }
$seenH = @{}; foreach ($l in Get-Content "$out\seen.csv") { $a, $b = $l -split ','; $seenH[$a] = [int64]$b }
$p4 = $true; foreach ($k in $seenH.Keys) { if ($maxAfter[$k] -ne $seenH[$k] + 2) { $p4 = $false } }
$lostOk = $true
foreach ($l in $expected) { $gc, $n, $mx, $hd = $l -split ','; if ($mx -ne $hd -or $seenH[$gc] -le [int64]$hd) { $lostOk = $false } }
$p1 = $countsEq -and ($afterT -eq 't') -and ($replay -eq 't')
$verdict = if ($p1 -and $p23 -and $p4 -and $lostOk) { 'pass' } else { 'fail' }
@("run,$Run", "T,$T", "p1_restored_matches_T,$p1", "p1_replay_ts_le_T,$replay", "p1_no_rows_after_T,$afterT",
  "p2_p3_recovery_and_no_gap,$p23", "p4_next_seq_is_seen_plus_2,$p4", "lost_range_is_seen_minus_restored,$lostOk", "verdict,$verdict") | Set-Content "$out\judge.csv"

Psql $rstPort $db @('-c', "select pg_size_pretty(pg_database_size('$db'))") | Set-Content "$out\db_size_restored.txt"
$walFiles = Get-ChildItem $Wal -File | Where-Object { $_.Name -match '^[0-9A-F]{24}$' }
$timings += "wal_files_archived,$($walFiles.Count)"
$timings | Set-Content "$out\timings.csv"
Stop-Pg $rstDir
Log "verdict: $verdict"
Get-Content "$out\judge.csv", "$out\timings.csv"
