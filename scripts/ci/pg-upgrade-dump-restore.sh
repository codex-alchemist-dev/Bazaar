#!/usr/bin/env bash
# Upgrade test for the managed PostgreSQL layer (#491): data written on one PostgreSQL major is dumped,
# restored into a newer major, and checked with a content checksum. Uses the dump and restore path that
# `bazaar db upgrade` uses, not an in-place pg_upgrade. Both images must have pgvector installed.
#
# Usage: pg-upgrade-dump-restore.sh <old-image> <new-image>
#   e.g. pg-upgrade-dump-restore.sh pgvector/pgvector:pg18 bazaar-pg19rc
set -euo pipefail

OLD_IMAGE="${1:?usage: $0 <old-image> <new-image>}"
NEW_IMAGE="${2:?usage: $0 <old-image> <new-image>}"
OLD=bazaar-upgrade-old
NEW=bazaar-upgrade-new
WORK="$(mktemp -d)"
DUMP="$WORK/upgrade.dump"

cleanup() {
  docker rm -f "$OLD" "$NEW" >/dev/null 2>&1 || true
  rm -rf "$WORK"
}
trap cleanup EXIT

start() { # name image
  docker run -d --name "$1" -e POSTGRES_PASSWORD=bazaar "$2" >/dev/null
  # Check over TCP: the official entrypoint runs a temporary server on the Unix socket only, and
  # a socket check can pass before the real server has started.
  for _ in $(seq 1 60); do
    if docker exec "$1" pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1; then return 0; fi
    sleep 1
  done
  echo "error: $1 ($2) did not start" >&2
  docker logs "$1" >&2 || true
  return 1
}

sql() { # container, then SQL on stdin; prints tuples only
  docker exec -i "$1" psql -U postgres -d postgres -v ON_ERROR_STOP=1 -tAq
}

# A checksum over every row, so the comparison catches lost, changed or reordered data.
CHECKSUM_SQL="SELECT count(*) || '|' || sum(id) || '|' || md5(string_agg(note || '|' || embedding::text, ',' ORDER BY id)) FROM upgrade_probe;"

echo "== 1. Start the old major ($OLD_IMAGE) and write probe data"
start "$OLD" "$OLD_IMAGE"
sql "$OLD" <<'SQL'
CREATE EXTENSION IF NOT EXISTS vector;
CREATE TABLE upgrade_probe (id int PRIMARY KEY, note text NOT NULL, embedding vector(3) NOT NULL);
INSERT INTO upgrade_probe
  SELECT g, 'row ' || g, ('[' || g || ',' || (g + 1) || ',' || (g + 2) || ']')::vector
  FROM generate_series(1, 500) AS g;
CREATE INDEX upgrade_probe_hnsw ON upgrade_probe USING hnsw (embedding vector_l2_ops);
SQL
BEFORE="$(echo "$CHECKSUM_SQL" | sql "$OLD")"
echo "old checksum: $BEFORE"

echo "== 2. Dump the old cluster (custom format)"
docker exec "$OLD" pg_dump -U postgres -d postgres --no-owner -Fc > "$DUMP"
test -s "$DUMP"

echo "== 3. Start the new major ($NEW_IMAGE) and restore"
start "$NEW" "$NEW_IMAGE"
docker cp "$DUMP" "$NEW:/tmp/upgrade.dump"
docker exec "$NEW" pg_restore -U postgres -d postgres --no-owner --exit-on-error /tmp/upgrade.dump

echo "== 4. Compare the checksum on the new major"
AFTER="$(echo "$CHECKSUM_SQL" | sql "$NEW")"
VERSION="$(echo "SHOW server_version;" | sql "$NEW")"
echo "new server version: $VERSION"
echo "new checksum: $AFTER"

if [ "$BEFORE" != "$AFTER" ]; then
  echo "error: data changed across the upgrade" >&2
  exit 1
fi

echo "== 5. Check that the HNSW index works on the new major"
PLAN="$(echo "SET enable_seqscan = off; EXPLAIN SELECT id FROM upgrade_probe ORDER BY embedding <-> '[1,2,3]' LIMIT 5;" | sql "$NEW")"
if ! grep -q "upgrade_probe_hnsw" <<<"$PLAN"; then
  echo "error: HNSW index is not used on the new major" >&2
  echo "$PLAN" >&2
  exit 1
fi

echo "upgrade test passed: ${OLD_IMAGE} -> ${NEW_IMAGE} (${VERSION})"
