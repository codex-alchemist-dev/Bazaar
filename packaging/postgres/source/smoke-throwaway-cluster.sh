#!/usr/bin/env bash
# Starts a THROWAWAY PostgreSQL cluster from a staged Linux source bundle, the way the host starts a managed cluster,
# creates the pgvector extension, runs a halfvec query, then stops the cluster and deletes its data directory.
# It never connects to a system or shared database. The data directory is a new temp directory and the port is
# chosen at random on loopback.
#
#   smoke-throwaway-cluster.sh --bundle artifacts/pg-src-bundle/18
set -euo pipefail

bundle=""
while [ $# -gt 0 ]; do
  case "$1" in
    --bundle) bundle="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
[ -n "$bundle" ] && [ -x "$bundle/bin/postgres" ] || { echo "--bundle must be a staged bundle with bin/postgres" >&2; exit 2; }
bundle="$(cd "$bundle" && pwd)"
bin="$bundle/bin"

data="$(mktemp -d "${TMPDIR:-/tmp}/bazaar-pg-smoke.XXXXXX")"
pwfile="$data.pw"
log="$data.log"
cleanup() {
  "$bin/pg_ctl" -D "$data/cluster" -m fast stop >/dev/null 2>&1 || true
  rm -rf "$data" "$pwfile" "$log"
}
trap cleanup EXIT

port=$(( (RANDOM % 20000) + 30000 ))
printf 'throwaway-smoke-password' > "$pwfile"

# The bundled tools find their libraries through lib/, as the host sets it at run time.
export LD_LIBRARY_PATH="$bundle/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

"$bin/initdb" -D "$data/cluster" -U postgres -A scram-sha-256 --pwfile="$pwfile" --encoding=UTF8 >"$log" 2>&1 \
  || { cat "$log" >&2; exit 1; }
printf "listen_addresses = '127.0.0.1'\nport = %s\n" "$port" >> "$data/cluster/postgresql.conf"
"$bin/pg_ctl" -D "$data/cluster" -l "$data/server.log" -w -t 120 start >"$log" 2>&1 \
  || { cat "$log" "$data/server.log" >&2 2>/dev/null || true; exit 1; }

export PGPASSWORD='throwaway-smoke-password'
psql_bin="$bin/psql"
[ -x "$psql_bin" ] || psql_bin="psql"
"$psql_bin" -h 127.0.0.1 -p "$port" -U postgres -v ON_ERROR_STOP=1 -qc "CREATE EXTENSION IF NOT EXISTS vector" \
  -At -c "SELECT extversion FROM pg_extension WHERE extname = 'vector'" \
  -c "SELECT '[1,2,3]'::halfvec(3)"
echo "Throwaway cluster smoke test passed (port $port, removed on exit)."
