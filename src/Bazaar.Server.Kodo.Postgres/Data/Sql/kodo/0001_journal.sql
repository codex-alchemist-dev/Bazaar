-- Kodo journal (docs/design/46-kodo-health-module.md sections 6.5, 7.5 and 7.10).
-- Namespace "kodo" (docs/design/BAZAAR-COMPONENTS.md section 5). Owned by Bazaar.Server.Kodo.Postgres.
-- Hash-chained, append-only records. record_hash is SHA-256(previous_hash || canonical bytes); the unique key on it makes replaying
-- the local spill idempotent (INSERT ... ON CONFLICT DO NOTHING). at_ticks holds the exact UTC ticks the hash covers, because
-- timestamptz would round to microseconds and break verification on read. Rows are never updated; only the retention job deletes.

CREATE TABLE kodo_journal (
    sequence      bigint       PRIMARY KEY CHECK (sequence >= 0),
    at_ticks      bigint       NOT NULL CHECK (at_ticks >= 0),
    kind          text         NOT NULL CHECK (octet_length(kind) BETWEEN 1 AND 65535),
    payload       bytea        NOT NULL,
    previous_hash bytea        NOT NULL CHECK (octet_length(previous_hash) = 32),
    record_hash   bytea        NOT NULL CHECK (octet_length(record_hash) = 32),
    stored_at     timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT kodo_journal_record_hash_key UNIQUE (record_hash)
);

-- Retention (kodo.retention.journal_days) deletes by age.
CREATE INDEX kodo_journal_at_ticks_idx ON kodo_journal (at_ticks);

CREATE FUNCTION kodo_journal_no_update() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'kodo_journal rows are append-only';
END;
$$;

CREATE TRIGGER kodo_journal_append_only BEFORE UPDATE ON kodo_journal
    FOR EACH ROW EXECUTE FUNCTION kodo_journal_no_update();
