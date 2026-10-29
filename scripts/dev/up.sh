#!/usr/bin/env bash
# Local dev run: Postgres, migrations, then Bazaar.Server.Host with dev auth. Safe to re-run.
#   BAZAAR_DB=<npgsql connection string>  use that database; skip local Postgres setup
#   BAZAAR_DEV_TOKEN=<token>              dev auth token; generated and saved under DATA_DIR if unset
#   BAZAAR_DEV_DATA=<dir>                 data root and local secrets (default ~/.local/share/bazaar-dev)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$ROOT/src/Bazaar.Server.Host/Bazaar.Server.Host.csproj"
DATA_DIR="${BAZAAR_DEV_DATA:-${XDG_DATA_HOME:-$HOME/.local/share}/bazaar-dev}"
STATUS_PORT="${BAZAAR_STATUS_PORT:-7390}"
PG_PORT=5432
DB_NAME=bazaar_dev
DB_ROLE=bazaar

die() { echo "up.sh: $*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || die "missing '$1' on PATH (see scripts/dev/README.md)"; }

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
  if ! pg_isready -h 127.0.0.1 -p "$PG_PORT" -q; then
    echo "Postgres is not accepting connections on 127.0.0.1:$PG_PORT; trying to start it."
    if command -v pg_ctlcluster >/dev/null 2>&1; then
      sudo -n pg_ctlcluster "$(ls /usr/lib/postgresql | sort -n | tail -1)" main start 2>/dev/null || true
    elif command -v systemctl >/dev/null 2>&1; then
      sudo -n systemctl start postgresql 2>/dev/null || true
    fi
    sleep 2
    pg_isready -h 127.0.0.1 -p "$PG_PORT" -q || die "Postgres is down. Start it, or set BAZAAR_DB to a database you have."
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
  pg_super psql -X -q -v ON_ERROR_STOP=1 -v pw="$PG_PW" <<SQL
SELECT format('CREATE ROLE $DB_ROLE LOGIN PASSWORD %L', :'pw')
  WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$DB_ROLE') \gexec
SELECT format('ALTER ROLE $DB_ROLE LOGIN PASSWORD %L', :'pw') \gexec
SELECT 'CREATE DATABASE $DB_NAME OWNER $DB_ROLE'
  WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = '$DB_NAME') \gexec
SQL
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
export Bazaar__DevAuth__Token="$BAZAAR_DEV_TOKEN"

# --- Build, migrate, run ----------------------------------------------------
dotnet build "$PROJECT" -v quiet --nologo

echo "Running migrations..."
dotnet run --project "$PROJECT" --no-build -- migrate "${HOST_ARGS[@]}" || die "migrate failed (see output above)"

echo
echo "Status URL:      http://127.0.0.1:$STATUS_PORT/status"
echo "Readiness URL:   http://127.0.0.1:$STATUS_PORT/health/ready"
echo "Terminal client: none on this branch yet (no Bazaar.Client.* terminal head exists)."
echo

if curl -fsS -o /dev/null "http://127.0.0.1:$STATUS_PORT/health/live" 2>/dev/null; then
  echo "A host is already answering on port $STATUS_PORT; not starting a second one."
  exit 0
fi

exec dotnet run --project "$PROJECT" --no-build -- "${HOST_ARGS[@]}"
