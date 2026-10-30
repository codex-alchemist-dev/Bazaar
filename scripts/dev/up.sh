#!/usr/bin/env bash
# Local dev run: Postgres, migrations, then Bazaar.Server.Host with dev auth. Safe to re-run.
#   BAZAAR_DB=<npgsql connection string>  use that database; skip local Postgres setup
#   BAZAAR_DEV_TOKEN=<token>              dev auth token; generated and saved under DATA_DIR if unset
#   BAZAAR_DEV_DATA=<dir>                 data root and local secrets (default ~/.local/share/bazaar-dev)
#   BAZAAR_PG_PORT=<port>                 local Postgres port (default: the port of the first cluster pg_lsclusters lists, else 5432)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$ROOT/src/Bazaar.Server.Host/Bazaar.Server.Host.csproj"
DATA_DIR="${BAZAAR_DEV_DATA:-${XDG_DATA_HOME:-$HOME/.local/share}/bazaar-dev}"
STATUS_PORT="${BAZAAR_STATUS_PORT:-7390}"
DB_NAME=bazaar_dev
DB_ROLE=bazaar
# Fixed dev ids: the owner principal, the dev agent and the dev chat. They are not secrets; they only name local data.
DEV_OWNER_ID=00000000-0000-7000-8000-000000000001
DEV_AGENT_ID=00000000-0000-7000-8000-000000000002
DEV_GC_ID=00000000-0000-7000-8000-000000000003

die() { echo "up.sh: $*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || die "missing '$1' on PATH (see scripts/dev/README.md)"; }

# Prints "<version> <cluster> <port>" for the best Debian-style cluster (pg_lsclusters), or nothing. An online cluster
# wins; among equals the newest version wins, because the schema uses PostgreSQL 18 functions (uuidv7).
first_cluster() {
  command -v pg_lsclusters >/dev/null 2>&1 || return 0
  pg_lsclusters --no-header 2>/dev/null | awk '{
    rank = ($4 == "online" ? 1000 : 0) + $1
    if (rank > best) { best = rank; line = $1 " " $2 " " $3 }
  } END { if (line != "") print line }'
}

# Waits up to 15 seconds for Postgres on $PG_PORT to accept connections.
wait_for_pg() {
  for _ in $(seq 1 15); do
    pg_isready -h 127.0.0.1 -p "$PG_PORT" -q && return 0
    sleep 1
  done
  return 1
}

need dotnet
need openssl
mkdir -p "$DATA_DIR/data"
chmod 700 "$DATA_DIR"

# --- Postgres -------------------------------------------------------------
if [ -n "${BAZAAR_DB:-}" ]; then
  echo "Using BAZAAR_DB from the environment (local Postgres setup skipped)."
else
  need pg_isready
  need psql

  # The port: BAZAAR_PG_PORT wins; otherwise the first cluster pg_lsclusters lists (it may be 5433 when 16 holds 5432).
  read -r PG_VERSION PG_CLUSTER CLUSTER_PORT <<< "$(first_cluster)" || true
  PG_PORT="${BAZAAR_PG_PORT:-${CLUSTER_PORT:-5432}}"

  if ! pg_isready -h 127.0.0.1 -p "$PG_PORT" -q; then
    echo "Postgres is not accepting connections on 127.0.0.1:$PG_PORT; trying to start it."
    if [ -n "${PG_CLUSTER:-}" ] && command -v pg_ctlcluster >/dev/null 2>&1; then
      if [ "$(id -u)" -eq 0 ]; then SUDO=""; else SUDO="sudo -n"; fi
      $SUDO pg_ctlcluster "$PG_VERSION" "$PG_CLUSTER" start 2>/dev/null || true
    elif command -v systemctl >/dev/null 2>&1; then
      sudo -n systemctl start postgresql 2>/dev/null || true
    fi
    wait_for_pg || die "Postgres is not answering on 127.0.0.1:$PG_PORT. Start it, set BAZAAR_PG_PORT, or set BAZAAR_DB to a database you have."
  fi

  PW_FILE="$DATA_DIR/pg-password"
  if [ ! -s "$PW_FILE" ]; then
    openssl rand -hex 24 > "$PW_FILE"
    chmod 600 "$PW_FILE"
  fi
  PG_PW="$(cat "$PW_FILE")"

  # Superuser access is needed once per run to sync the role and database (idempotent).
  pg_super() {
    if [ "$(id -u)" -eq 0 ]; then runuser -u postgres -- "$@"; else sudo -n -u postgres "$@"; fi
  }
  pg_super psql -X -q -p "$PG_PORT" -v ON_ERROR_STOP=1 -v pw="$PG_PW" <<SQL
SELECT format('CREATE ROLE $DB_ROLE LOGIN PASSWORD %L', :'pw')
  WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$DB_ROLE') \gexec
SELECT format('ALTER ROLE $DB_ROLE LOGIN PASSWORD %L', :'pw') \gexec
SELECT 'CREATE DATABASE $DB_NAME OWNER $DB_ROLE'
  WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = '$DB_NAME') \gexec
SQL
  # The migrations create the pgvector extension, which the app role may not do. The superuser creates it once here.
  pg_super psql -X -q -p "$PG_PORT" -d "$DB_NAME" -v ON_ERROR_STOP=1 -c "CREATE EXTENSION IF NOT EXISTS vector" \
    || die "could not create the pgvector extension in $DB_NAME (install postgresql-<version>-pgvector)."
  export BAZAAR_DB="Host=127.0.0.1;Port=$PG_PORT;Database=$DB_NAME;Username=$DB_ROLE;Password=$PG_PW"
  echo "Postgres ready: 127.0.0.1:$PG_PORT database $DB_NAME (password in $PW_FILE)."
fi

# --- Dev token --------------------------------------------------------------
if [ -z "${BAZAAR_DEV_TOKEN:-}" ]; then
  TOKEN_FILE="$DATA_DIR/dev-token"
  if [ ! -s "$TOKEN_FILE" ]; then
    openssl rand -hex 32 > "$TOKEN_FILE"
    chmod 600 "$TOKEN_FILE"
    echo "Generated BAZAAR_DEV_TOKEN (shown once; saved in $TOKEN_FILE, never in git):"
    echo "  $(cat "$TOKEN_FILE")"
  else
    echo "Using dev token saved in $TOKEN_FILE (set BAZAAR_DEV_TOKEN to override)."
  fi
  BAZAAR_DEV_TOKEN="$(cat "$TOKEN_FILE")"
fi

# Config keys: the host reads env vars as Bazaar:Section:Key; migrate reads command-line args.
HOST_ARGS=(
  "--Bazaar:Host:DataRoot=$DATA_DIR/data"
  "--Bazaar:Host:DatabaseConnectionReference=env:BAZAAR_DB"
)
export Bazaar__DevAuth__Enabled=true
export Bazaar__DevAuth__TokenSecret=env:BAZAAR_DEV_TOKEN
export Bazaar__DevAuth__PrincipalId="$DEV_OWNER_ID"
export BAZAAR_DEV_TOKEN

# The dev agent answers mentions and DMs in the dev chat as its own member. Its model route is the responder default.
export Bazaar__AgentResponder__Agents__0__AgentId="$DEV_AGENT_ID"
export Bazaar__AgentResponder__Agents__0__Name="Charles"
# FreeLLMAPI for the dev agent, only when a base URL is given. The sidecar thread supplies these. The key is read from the
# environment by reference, never written to a file or the log.
if [ -n "${BAZAAR_FREELLMAPI_URL:-}" ]; then
  [ -n "${BAZAAR_FREELLMAPI_KEY:-}" ] || die "BAZAAR_FREELLMAPI_URL is set but BAZAAR_FREELLMAPI_KEY is empty."
  export BAZAAR_FREELLMAPI_KEY
  export Bazaar__Gateway__FreeLlmApi__Enabled=true
  export Bazaar__Gateway__FreeLlmApi__BaseUrl="$BAZAAR_FREELLMAPI_URL"
  export Bazaar__Gateway__FreeLlmApi__ApiKey=env:BAZAAR_FREELLMAPI_KEY
  export Bazaar__Gateway__FreeLlmApi__DefaultModel="${BAZAAR_FREELLMAPI_MODEL:-groq/llama-3.3-70b}"
fi
export Bazaar__AgentResponder__Agents__0__SystemPrompt="You are Charles, a dev agent in the local Bazaar chat. Reply briefly and plainly."

# --- Build, migrate, run ----------------------------------------------------
dotnet build "$PROJECT" -v quiet --nologo

echo "Running migrations..."
dotnet run --project "$PROJECT" --no-build -- migrate "${HOST_ARGS[@]}" || die "migrate failed (see output above)"

# The dev chat: the owner and the dev agent as members of one general GC. Idempotent; uses the app role's grants.
SEED_ARGS=(-X -q -v ON_ERROR_STOP=1 -v owner_id="$DEV_OWNER_ID" -v agent_id="$DEV_AGENT_ID" -v gc_id="$DEV_GC_ID" -f "$ROOT/scripts/dev/seed-dev-chat.sql")
if [ -n "${PG_PW:-}" ]; then
  need psql
  PGPASSWORD="$PG_PW" psql -h 127.0.0.1 -p "$PG_PORT" -U "$DB_ROLE" -d "$DB_NAME" "${SEED_ARGS[@]}" || die "could not seed the dev chat (see output above)"
else
  echo "BAZAAR_DB is set, so the dev chat is not seeded. Run scripts/dev/seed-dev-chat.sql against it with owner_id, agent_id and gc_id set to the DEV_* values in up.sh."
fi

echo
echo "Status URL:      http://127.0.0.1:$STATUS_PORT/status"
echo "Readiness URL:   http://127.0.0.1:$STATUS_PORT/health/ready"
echo "Terminal client (owner):  dotnet run --project src/Bazaar.Client.Terminal -- --server ws://127.0.0.1:$STATUS_PORT/ws --gc $DEV_GC_ID --agent $DEV_AGENT_ID=Charles"
echo "                          token in \$BAZAAR_TOKEN (the same value as BAZAAR_DEV_TOKEN)"
echo

if curl -fsS -o /dev/null "http://127.0.0.1:$STATUS_PORT/health/live" 2>/dev/null; then
  echo "A host is already answering on port $STATUS_PORT; not starting a second one."
  exit 0
fi

exec dotnet run --project "$PROJECT" --no-build -- "${HOST_ARGS[@]}"
