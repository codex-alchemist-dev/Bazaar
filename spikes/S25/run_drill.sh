#!/usr/bin/env bash
# S25 restore drill, one run. Usage: run_drill.sh <run-number> <work-dir>
# Source container: base backup, batch A, target time T, batch B, then killed.
# Restore container: fresh copy of the base backup, WAL replayed to T, promoted.
set -euo pipefail

RUN="$1"
WORK="$2"
HERE="$(cd "$(dirname "$0")" && pwd)"
IMG=pgvector/pgvector:pg18
A_PER_GC="${A_PER_GC:-100000}"   # batch A rows per GC, before T
B_PER_GC="${B_PER_GC:-20000}"    # batch B rows per GC, after T (the rolled-back range)
RUNDIR="$WORK/run$RUN"
SRC="s25-src-$RUN"
RST="s25-rst-$RUN"
OUT="$RUNDIR/results"

ms() { date +%s%3N; }
psql_src() { docker exec -i "$SRC" psql -X -v ON_ERROR_STOP=1 -U postgres -d postgres -At -F, "$@"; }
psql_rst() { docker exec -i "$RST" psql -X -v ON_ERROR_STOP=1 -U postgres -d postgres -At -F, "$@"; }
log() { echo "[run $RUN $(date -u +%H:%M:%S)] $*"; }

cleanup() { docker rm -fv "$SRC" "$RST" >/dev/null 2>&1 || true; }

wait_ready() {  # wait until the real server (TCP, not the init-only temp server) answers
    local c="$1"
    for _ in $(seq 1 240); do
        if docker exec "$c" pg_isready -q -h 127.0.0.1 -U postgres 2>/dev/null; then return 0; fi
        sleep 0.5
    done
    echo "timeout waiting for $c" >&2
    return 1
}

wait_archived() {  # wait until pg_stat_archiver has archived segment $1 or a later one
    local want="$1" last
    for _ in $(seq 1 1200); do
        last="$(psql_src -c "select coalesce(last_archived_wal, '') from pg_stat_archiver")"
        if [[ -n "$last" && ( "$last" > "$want" || "$last" == "$want" ) ]]; then return 0; fi
        sleep 0.25
    done
    echo "timeout waiting for archive of $want" >&2
    return 1
}

trap cleanup EXIT
cleanup
rm -rf "$RUNDIR"
mkdir -p "$RUNDIR/wal" "$OUT"
chmod -R 777 "$RUNDIR"

# ---- Source ----------------------------------------------------------------
docker run -d --name "$SRC" \
    -e POSTGRES_PASSWORD=spike -e PGDATA=/var/lib/postgresql/data \
    -v "$RUNDIR:/spike/work" -v "$HERE:/spike/code:ro" \
    "$IMG" \
    -c wal_level=replica -c archive_mode=on \
    -c "archive_command=sh /spike/code/archive.sh %p %f" \
    -c max_wal_senders=5 -c archive_timeout=0 >/dev/null
wait_ready "$SRC"
log "source ready: $(psql_src -c 'select version()' | cut -c1-40)"

psql_src -f - < "$HERE/sql/schema.sql" >/dev/null
psql_src -f - < "$HERE/sql/functions.sql" >/dev/null

t0=$(ms)
docker exec "$SRC" pg_basebackup -h /var/run/postgresql -U postgres \
    -D /spike/work/base -Fp -X stream -c fast -l "s25 run $RUN" >/dev/null
echo "base_backup_ms,$(( $(ms) - t0 ))" >> "$OUT/timings.csv"
log "base backup taken"

# ---- Batch A (before T) -----------------------------------------------------
t0=$(ms)
psql_src -c "select append_messages(g, $A_PER_GC) from generate_series(1, 4) g" >/dev/null
echo "batch_a_ms,$(( $(ms) - t0 ))" >> "$OUT/timings.csv"
seg="$(psql_src -c "select pg_walfile_name(pg_current_wal_lsn())")"
psql_src -c "select pg_switch_wal()" >/dev/null
wait_archived "$seg"
sleep 1
T="$(psql_src -c "select clock_timestamp()")"
psql_src -c "select pg_sleep(1)" >/dev/null
psql_src -f - < "$HERE/sql/snapshot.sql" > "$OUT/expected_at_T.csv"
log "batch A committed and archived; T=$T"

# ---- Batch B (after T; this is the range the rollback discards) -------------
psql_src -c "select append_messages(g, $B_PER_GC) from generate_series(1, 4) g" >/dev/null
t_b_end=$(ms)
psql_src -f - < "$HERE/sql/snapshot.sql" > "$OUT/source_before_crash.csv"
# Surviving source: the highest seq each GC reached, as clients and the offsite audit head would have seen it.
psql_src -c "select gc_id, head_seq from gc order by gc_id" > "$OUT/seen.csv"
seg="$(psql_src -c "select pg_walfile_name(pg_current_wal_lsn())")"
psql_src -c "select pg_switch_wal()" >/dev/null
wait_archived "$seg"
echo "archive_lag_ms,$(( $(ms) - t_b_end ))" >> "$OUT/timings.csv"
log "batch B archived; source killed next"

# Crash the source (no clean shutdown). Everything committed was archived above.
docker kill "$SRC" >/dev/null

# ---- Restore to T -----------------------------------------------------------
RESTORE_DIR="$RUNDIR/restore"
cp -a "$RUNDIR/base" "$RESTORE_DIR"
cat >> "$RESTORE_DIR/postgresql.auto.conf" <<EOF
restore_command = 'cp /spike/work/wal/%f %p'
recovery_target_time = '$T'
recovery_target_action = 'promote'
archive_mode = 'off'
EOF
touch "$RESTORE_DIR/recovery.signal"

t0=$(ms)
docker run -d --name "$RST" \
    -e POSTGRES_PASSWORD=spike -e PGDATA=/spike/work/restore \
    -v "$RUNDIR:/spike/work" -v "$HERE:/spike/code:ro" \
    "$IMG" >/dev/null
for _ in $(seq 1 2400); do
    state="$(docker exec "$RST" psql -X -U postgres -d postgres -At -c "select pg_is_in_recovery()" 2>/dev/null || true)"
    if [[ "$state" == "f" ]]; then break; fi
    sleep 0.25
done
[[ "$state" == "f" ]] || { echo "restore did not promote" >&2; exit 1; }
echo "restore_to_promote_ms,$(( $(ms) - t0 ))" >> "$OUT/timings.csv"
log "restored and promoted"

# P1: match at T
psql_rst -f - < "$HERE/sql/snapshot.sql" > "$OUT/restored_at_T.csv"
replay_ts="$(psql_rst -c "select pg_last_xact_replay_timestamp() <= '$T'::timestamptz")"
if diff -q "$OUT/expected_at_T.csv" "$OUT/restored_at_T.csv" >/dev/null; then p1_counts=true; else p1_counts=false; fi
p1_replay="$replay_ts"
p1_after_t="$(psql_rst -c "select count(*) = 0 from gc_message where created_at > '$T'::timestamptz")"

# P2: apply the floor and recovery entry per GC (restored_at = now on the restored server)
while IFS=, read -r gc seen; do
    psql_rst -c "select apply_recovery($gc, $seen, clock_timestamp())" >/dev/null </dev/null
done < "$OUT/seen.csv"

# P2 and P3 from the system entries
psql_rst -f - < "$HERE/sql/verify_recovery.sql" > "$OUT/verify_recovery.csv"

# P4: the first append after recovery must be seen_head + 2
psql_rst -c "select append_messages(g, 1) from generate_series(1, 4) g" >/dev/null
psql_rst -c "select gc_id, max(seq) from gc_message group by gc_id order by gc_id" > "$OUT/max_after_append.csv"
psql_rst -f - < "$HERE/sql/verify_recovery.sql" > "$OUT/verify_after_append.csv"

# ---- Judge ------------------------------------------------------------------
p2_p3_ok=true
if grep -q ",f" "$OUT/verify_recovery.csv" || cut -d, -f5-8 "$OUT/verify_after_append.csv" | grep -q f; then p2_p3_ok=false; fi
[[ "$(wc -l < "$OUT/verify_recovery.csv")" -eq 4 ]] || p2_p3_ok=false
p4_ok=true
while IFS=, read -r gc seen; do
    got="$(awk -F, -v g="$gc" '$1==g {print $2}' "$OUT/max_after_append.csv")"
    [[ "$got" == "$((seen + 2))" ]] || p4_ok=false
done < "$OUT/seen.csv"

# Expected restored head per GC and lost range, from the snapshot at T
lost_ok=true
while IFS=, read -r gc n maxseq head; do
    seen="$(awk -F, -v g="$gc" '$1==g {print $2}' "$OUT/seen.csv")"
    [[ "$maxseq" == "$head" ]] || lost_ok=false
    [[ "$seen" -gt "$head" ]] || lost_ok=false
done < "$OUT/expected_at_T.csv"

restored_head_matches=true
diff -q "$OUT/expected_at_T.csv" "$OUT/restored_at_T.csv" >/dev/null || restored_head_matches=false

p1_ok=true
[[ "$p1_counts" == true && "$p1_after_t" == "t" && "$p1_replay" == "t" ]] || p1_ok=false
verdict=pass
if [[ "$p1_ok" != true || "$p2_p3_ok" != true || "$p4_ok" != true || "$lost_ok" != true || "$restored_head_matches" != true ]]; then
    verdict=fail
fi

{
    echo "run,$RUN"
    echo "T,$T"
    echo "p1_restored_matches_T,$p1_ok"
    echo "p1_replay_ts_le_T,$p1_replay"
    echo "p1_no_rows_after_T,$p1_after_t"
    echo "p2_p3_recovery_and_no_gap,$p2_p3_ok"
    echo "p4_next_seq_is_seen_plus_2,$p4_ok"
    echo "lost_range_is_seen_minus_restored,$lost_ok"
    echo "verdict,$verdict"
} > "$OUT/judge.csv"

# Sizes for the record
psql_rst -c "select pg_size_pretty(pg_database_size('postgres'))" > "$OUT/db_size_restored.txt"
echo "wal_files_archived,$(ls "$RUNDIR/wal" | grep -c '^[0-9A-F]\{24\}$')" >> "$OUT/timings.csv"
echo "wal_bytes_archived,$(cat "$RUNDIR"/wal/[0-9A-F]* | wc -c)" >> "$OUT/timings.csv"
log "verdict: $verdict"
cat "$OUT/judge.csv" "$OUT/timings.csv"
