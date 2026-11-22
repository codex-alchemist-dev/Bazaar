#!/usr/bin/env bash
# Clean-machine install of Bazaar.Server.Host on Linux, non-interactive, fully logged (doc 03, doc 37, doc 45 section 1.1/1.5/1.9).
# Starts from a bare Ubuntu or Debian machine: installs PostgreSQL 18 with pgvector and the .NET SDK from global.json,
# publishes the host, creates the database, migrates, starts the host, submits a setup plan through the provisioning API,
# then runs the smoke checks. Every step is timed and logged; secrets are generated, kept in 0600 files, and masked in the log.
#
#   sudo scripts/install/install-linux.sh [--prefix DIR] [--log FILE] [--skip-prereqs] [--keep-running]
#
# Exit 0 = every required check passed. Non-zero = the first failed step (the log says which).
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PREFIX="${BAZAAR_INSTALL_PREFIX:-/opt/bazaar}"
LOG="${BAZAAR_INSTALL_LOG:-$ROOT/install-$(date -u +%Y%m%dT%H%M%SZ).log}"
SKIP_PREREQS=0; KEEP_RUNNING=0
while [ $# -gt 0 ]; do
  case "$1" in
    --prefix) PREFIX="$2"; shift 2 ;;
    --log) LOG="$2"; shift 2 ;;
    --skip-prereqs) SKIP_PREREQS=1; shift ;;
    --keep-running) KEEP_RUNNING=1; shift ;;
    *) echo "unknown option: $1" >&2; exit 64 ;;
  esac
done

DATA_ROOT="$PREFIX/data"; APP_DIR="$PREFIX/app"; SECRETS_DIR="$PREFIX/secrets"
STATUS_PORT="${BAZAAR_STATUS_PORT:-7390}"; STATUS="http://127.0.0.1:$STATUS_PORT"
DB_NAME=bazaar; DB_ROLE=bazaar; PG_MAJOR=18
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"; export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

mkdir -p "$(dirname "$LOG")"; : > "$LOG"
SECRET_VALUES=()
mask() { local s="$1" v; for v in "${SECRET_VALUES[@]}"; do [ -n "$v" ] && s="${s//"$v"/***}"; done; printf '%s' "$s"; }
log() { printf '%s %s\n' "$(date -u +%FT%TZ)" "$(mask "$*")" | tee -a "$LOG"; }
# Runs a command, logs its output line by line (masked) and its exit code.
run() { log "\$ $*"; local out rc; out="$("$@" 2>&1)"; rc=$?; [ -n "$out" ] && while IFS= read -r l; do log "  $l"; done <<<"$out"; log "  exit=$rc"; return $rc; }
STEP=0; FAILED=""
step() { STEP=$((STEP+1)); STEP_NAME="$1"; STEP_T0=$SECONDS; log "=== step $STEP: $1"; }
ok() { log "--- ok: $STEP_NAME ($((SECONDS-STEP_T0))s)"; }
die() { log "!!! FAILED at step $STEP ($STEP_NAME): $*"; log "log: $LOG"; exit 1; }

step "preflight"
[ "$(id -u)" -eq 0 ] || die "run as root (sudo): the script installs packages and creates $PREFIX."
. /etc/os-release 2>/dev/null || true
log "os=${PRETTY_NAME:-unknown} kernel=$(uname -r) arch=$(uname -m) cpus=$(nproc) mem_mb=$(awk '/MemTotal/{print int($2/1024)}' /proc/meminfo)"
log "disk_free_mb=$(df -Pm / | awk 'NR==2{print $4}') repo=$ROOT commit=$(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || echo unknown)"
case "${ID:-}" in ubuntu|debian) ;; *) die "only Debian and Ubuntu are covered by this script (found '${ID:-unknown}')." ;; esac
[ "$(df -Pm / | awk 'NR==2{print $4}')" -gt 4000 ] || die "need at least 4 GB free."
ok

step "prerequisites (PostgreSQL $PG_MAJOR + pgvector, .NET SDK)"
if [ "$SKIP_PREREQS" -eq 0 ]; then
  export DEBIAN_FRONTEND=noninteractive
  run apt-get update -qq || die "apt-get update"
  run apt-get install -y -qq curl ca-certificates gnupg openssl lsb-release python3 || die "base packages"
  if ! apt-cache policy "postgresql-$PG_MAJOR" 2>/dev/null | grep -q Candidate:.*[0-9]; then
    run install -d /usr/share/postgresql-common/pgdg || die "pgdg dir"
    run curl -fsS -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc https://www.postgresql.org/media/keys/ACCC4CF8.asc || die "pgdg key"
    echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt $(lsb_release -cs)-pgdg main" > /etc/apt/sources.list.d/pgdg.list
    run apt-get update -qq || die "apt-get update (pgdg)"
  fi
  run apt-get install -y -qq "postgresql-$PG_MAJOR" "postgresql-$PG_MAJOR-pgvector" || die "postgresql-$PG_MAJOR"
  SDK_VERSION="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["sdk"]["version"])' "$ROOT/global.json")"
  if ! "$DOTNET_ROOT/dotnet" --list-sdks 2>/dev/null | grep -q "^${SDK_VERSION%.*}"; then
    run curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh || die "dotnet-install.sh"
    run bash /tmp/dotnet-install.sh --version "$SDK_VERSION" --install-dir "$DOTNET_ROOT" || die ".NET SDK $SDK_VERSION"
  fi
else
  log "skipped (--skip-prereqs)"
fi
run dotnet --version || die "dotnet not on PATH"
PG_BIN="/usr/lib/postgresql/$PG_MAJOR/bin"; [ -x "$PG_BIN/postgres" ] || die "PostgreSQL $PG_MAJOR binaries not found in $PG_BIN"
ok

step "PostgreSQL cluster, role, database, pgvector"
if ! pg_lsclusters --no-header 2>/dev/null | awk -v v="$PG_MAJOR" '$1==v{f=1} END{exit !f}'; then
  run pg_createcluster "$PG_MAJOR" main || die "pg_createcluster"
fi
PG_PORT="$(pg_lsclusters --no-header | awk -v v="$PG_MAJOR" '$1==v{print $3; exit}')"
run pg_ctlcluster "$PG_MAJOR" main start || true
for _ in $(seq 1 30); do pg_isready -h 127.0.0.1 -p "$PG_PORT" -q && break; sleep 1; done
pg_isready -h 127.0.0.1 -p "$PG_PORT" -q || die "PostgreSQL $PG_MAJOR is not answering on $PG_PORT"
install -d -m 700 "$SECRETS_DIR" "$DATA_ROOT"
if [ ! -s "$SECRETS_DIR/pg-password" ]; then ( umask 077; openssl rand -hex 24 > "$SECRETS_DIR/pg-password" ); fi
PG_PW="$(cat "$SECRETS_DIR/pg-password")"; SECRET_VALUES+=("$PG_PW")
# CREATEROLE: the core migrations create the NOLOGIN roles bazaar_app and bazaar_erasure (src/Bazaar.Server/Data/Sql/common).
pgsu() { runuser -u postgres -- "$@"; }
pgsu psql -X -q -p "$PG_PORT" -v ON_ERROR_STOP=1 -v pw="$PG_PW" >>"$LOG" 2>&1 <<SQL || die "role/database setup"
SELECT format('CREATE ROLE $DB_ROLE LOGIN CREATEROLE PASSWORD %L', :'pw') WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='$DB_ROLE') \gexec
SELECT format('ALTER ROLE $DB_ROLE LOGIN CREATEROLE PASSWORD %L', :'pw') \gexec
SELECT 'CREATE DATABASE $DB_NAME OWNER $DB_ROLE' WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname='$DB_NAME') \gexec
SQL
run pgsu psql -X -q -p "$PG_PORT" -d "$DB_NAME" -v ON_ERROR_STOP=1 -c "CREATE EXTENSION IF NOT EXISTS vector" || die "pgvector extension"
run pgsu psql -X -At -p "$PG_PORT" -d "$DB_NAME" -c "select version(), (select extversion from pg_extension where extname='vector'), uuidv7() is not null" || die "PostgreSQL 18 features missing (uuidv7)"
export BAZAAR_DB="Host=127.0.0.1;Port=$PG_PORT;Database=$DB_NAME;Username=$DB_ROLE;Password=$PG_PW"
ok

step "publish Bazaar.Server.Host"
# A first publish right after a fresh SDK install failed once with no output; one retry (with the output kept) covers it.
run dotnet publish "$ROOT/src/Bazaar.Server.Host/Bazaar.Server.Host.csproj" -c Release --self-contained false -o "$APP_DIR" -v:m -nologo "-clp:ErrorsOnly;Summary" \
  || run dotnet publish "$ROOT/src/Bazaar.Server.Host/Bazaar.Server.Host.csproj" -c Release --self-contained false -o "$APP_DIR" -v:m -nologo "-clp:ErrorsOnly;Summary" || die "dotnet publish"
[ -f "$APP_DIR/Bazaar.Server.Host.dll" ] || die "publish produced no Bazaar.Server.Host.dll"
ok

HOST_ARGS=("--Bazaar:Host:DataRoot=$DATA_ROOT" "--Bazaar:Host:DatabaseConnectionReference=env:BAZAAR_DB")
step "migrate the schema"
run dotnet "$APP_DIR/Bazaar.Server.Host.dll" migrate "${HOST_ARGS[@]}" || die "migrate"
ok

HOST_LOG="$PREFIX/host.log"; HOST_PID=""
stop_host() {
  [ -n "$HOST_PID" ] || return 0
  kill "$HOST_PID" 2>/dev/null
  for _ in $(seq 1 30); do kill -0 "$HOST_PID" 2>/dev/null || break; sleep 1; done   # the host is not our child; poll until it is gone
  HOST_PID=""
}
trap '[ "$KEEP_RUNNING" -eq 1 ] || stop_host' EXIT
# Starts the host, waits for /health/live, and leaves the one-time bootstrap code (printed at start while no owner exists) in $CODE.
start_host() {
  local attempt ready
  for attempt in 1 2 3 4 5 6; do
    : > "$HOST_LOG"
    ( cd "$APP_DIR"; nohup dotnet Bazaar.Server.Host.dll "${HOST_ARGS[@]}" "--Bazaar:Status:Port=$STATUS_PORT" >"$HOST_LOG" 2>&1 & echo $! > "$PREFIX/host.pid" )
    HOST_PID="$(cat "$PREFIX/host.pid")"; log "host pid=$HOST_PID (attempt $attempt), output in $HOST_LOG"
    # Ready = still running, answers /health/live, and (while no owner exists) has printed its one-time bootstrap code. The listener comes
    # up before the startup sequence finishes, so a host that then exits on the instance lock must not count as ready.
    ready=0; CODE=""
    for i in $(seq 1 90); do
      kill -0 "$HOST_PID" 2>/dev/null || break
      CODE="$(grep -o 'X-Bazaar-Bootstrap-Code header): [A-Za-z0-9_-]*' "$HOST_LOG" | awk '{print $NF}' | head -1)"
      if curl -fsS -m 2 "$STATUS/health/live" >/dev/null 2>&1 && { [ -n "$CODE" ] || [ "$i" -ge 20 ]; }; then ready=1; break; fi
      sleep 1
    done
    kill -0 "$HOST_PID" 2>/dev/null || ready=0
    [ "$ready" -eq 1 ] && break
    # A restart can race the previous process's database session: the instance lock is released a moment later. Retry that case only.
    if grep -q "instance lock" "$HOST_LOG" 2>/dev/null && [ "$attempt" -lt 6 ]; then log "instance lock still held by the previous process; retrying in 3 s"; HOST_PID=""; sleep 3; continue; fi
    log "host output:"; tail -n 40 "$HOST_LOG" | while IFS= read -r l; do log "  $(mask "$l")"; done
    die "the host did not become live"
  done
  [ -n "$CODE" ] && SECRET_VALUES+=("$CODE")
  return 0
}
step "start the host"
start_host
ok

step "non-interactive setup through the provisioning API (doc 45 section 1.5 and 1.9)"
# The bootstrap code is single use (doc 45 section 1.9: it is consumed by the first admitted call). A scripted install needs two calls
# (validate to get the plan hash, then apply), so the host is restarted between them; a restart mints a new code while no owner exists.
if [ -z "$CODE" ]; then
  log "no bootstrap code printed (an owner may already exist); setup API calls skipped"
  SETUP_RESULT="skipped"
else
  PLAN="$PREFIX/bazaar-setup.json"
  cat > "$PLAN" <<JSON
{
  "schemaVersion": 1,
  "install": { "companyName": "Clean Install Co", "companySlug": "clean-install-co", "locale": "en", "timezone": "UTC", "releaseChannel": "beta" },
  "database": { "postgresMajor": $PG_MAJOR },
  "custody": { "kekProvider": "file", "kekRef": "file:$SECRETS_DIR/kek.bin" },
  "owner": { "displayName": "Owner", "requireSecondFactor": false },
  "privacy": { "profile": "private" },
  "backups": { "acknowledgeNoTarget": true },
  "company": { "source": "template:starter@1.0.0" }
}
JSON
  log "plan written to $PLAN (references only, no secret values)"
  api() { curl -sS -m 120 -o "$PREFIX/api.out" -w '%{http_code}' -X "$1" -H "X-Bazaar-Bootstrap-Code: $CODE" -H 'Content-Type: application/json' "${@:3}" "$STATUS$2"; }
  VAL_CODE="$(api POST /setup/plans --data-binary @"$PLAN")"
  log "POST /setup/plans -> HTTP $VAL_CODE: $(head -c 700 "$PREFIX/api.out")"
  HASH="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("planHash") or "")' "$PREFIX/api.out" 2>/dev/null)"
  if [ "$VAL_CODE" = "200" ] && [ -n "$HASH" ]; then
    log "plan is valid, hash $HASH; restarting the host for a fresh bootstrap code"
    stop_host; start_host
    [ -n "$CODE" ] || die "no bootstrap code after the restart"
    APPLY_CODE="$(api POST "/setup/plans/$HASH/apply" --data-binary @"$PLAN")"
    log "POST /setup/plans/$HASH/apply -> HTTP $APPLY_CODE: $(head -c 700 "$PREFIX/api.out")"
    SETUP_RESULT="valid, apply HTTP $APPLY_CODE"
    [ "$APPLY_CODE" = "200" ] || log "NOTE: apply did not complete; doc 45 section 1.9 says no apply step is registered on main yet, so exit 3 is expected until the setup wizard apply steps land"
  else
    SETUP_RESULT="validate HTTP $VAL_CODE"
    die "the setup plan was not accepted: $SETUP_RESULT"
  fi
fi
ok

step "smoke checks"
FAIL=0
check() { local name="$1" url="$2" code; code="$(curl -sS -m 10 -o "$PREFIX/smoke.tmp" -w '%{http_code}' "$url" 2>&1)"; log "$name: HTTP $code $(head -c 400 "$PREFIX/smoke.tmp" | tr '\n' ' ')"; [ "$code" = "200" ] || { log "CHECK FAILED: $name"; FAIL=1; }; }
check "health/live" "$STATUS/health/live"
check "health/ready" "$STATUS/health/ready"
check "status" "$STATUS/status"
log "host log lines: $(grep -c . "$HOST_LOG")"
[ "$FAIL" -eq 0 ] || die "one or more smoke checks failed"
ok

log "=== SUMMARY: install ok in ${SECONDS}s; setup: ${SETUP_RESULT:-n/a}; host log $HOST_LOG; install log $LOG"
exit 0
