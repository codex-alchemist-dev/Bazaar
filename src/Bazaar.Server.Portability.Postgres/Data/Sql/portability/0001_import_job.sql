-- Import job store (docs/design/44-data-portability.md sections 8.1, 8.4, 8.7 and 8.9).
-- Namespace "portability" (docs/design/BAZAAR-COMPONENTS.md section 5). Owned by Bazaar.Server.Portability.Postgres.
-- Rows are history: an import, a cancel or a withdrawal never deletes them (section 8.8). Only keys, hashes and locations
-- are stored, never the plaintext of imported personal content (section 8.5).
-- No foreign keys leave this namespace, so it applies on its own and does not depend on the shared common/ tables.

CREATE TABLE import_job (
    job_id           text        PRIMARY KEY CHECK (btrim(job_id) <> '' AND length(job_id) <= 128),
    actor_id         text        NOT NULL CHECK (btrim(actor_id) <> ''),
    importer_id      text        NOT NULL CHECK (btrim(importer_id) <> ''),
    importer_version text        NOT NULL CHECK (btrim(importer_version) <> ''),
    state            text        NOT NULL CHECK (state IN (
                                     'staged', 'dry_run', 'committing', 'paused', 'stalled',
                                     'verifying', 'verified', 'failed_verify', 'failed', 'cancelled')),
    max_upload_bytes bigint      NOT NULL CHECK (max_upload_bytes > 0),
    -- Array of {"name", "size_bytes", "sha256"} (section 8.9, source manifest).
    source_manifest  jsonb       NOT NULL CHECK (jsonb_typeof(source_manifest) = 'array'),
    mapping_hash     text        CHECK (mapping_hash IS NULL OR mapping_hash ~ '^[0-9a-f]{64}$'),
    dry_run_hash     text        CHECK (dry_run_hash IS NULL OR dry_run_hash ~ '^[0-9a-f]{64}$'),
    created_at       timestamptz NOT NULL,
    started_at       timestamptz,
    last_progress_at timestamptz,
    ended_at         timestamptz,
    failure_reason   text,
    -- Terminal states carry an end time; every other state has none.
    CHECK ((state IN ('verified', 'failed_verify', 'failed', 'cancelled')) = (ended_at IS NOT NULL))
);

-- Stall scans read the jobs that have not ended.
CREATE INDEX import_job_open_idx ON import_job (last_progress_at) WHERE ended_at IS NULL;

-- One row per committed batch (section 8.7 checkpoints). item_counts is an object keyed by target ("gc_message", "vault_file").
CREATE TABLE import_batch (
    job_id           text        NOT NULL REFERENCES import_job (job_id),
    batch_number     integer     NOT NULL CHECK (batch_number >= 1),
    item_counts      jsonb       NOT NULL CHECK (jsonb_typeof(item_counts) = 'object' AND item_counts <> '{}'::jsonb),
    first_source_key text        NOT NULL CHECK (btrim(first_source_key) <> ''),
    last_source_key  text        NOT NULL CHECK (btrim(last_source_key) <> ''),
    batch_hash       text        NOT NULL CHECK (batch_hash ~ '^[0-9a-f]{64}$'),
    committed_at     timestamptz NOT NULL,
    PRIMARY KEY (job_id, batch_number)
);

-- The source-key index (section 8.4). Identity is (importer, source key, target version): a changed source gets a new
-- target version and a new row, and an unchanged one is never written twice.
CREATE TABLE import_item (
    importer_id    text        NOT NULL CHECK (btrim(importer_id) <> ''),
    source_key     text        NOT NULL CHECK (btrim(source_key) <> ''),
    content_hash   text        NOT NULL CHECK (content_hash ~ '^[0-9a-f]{64}$'),
    target_kind    text        NOT NULL CHECK (target_kind IN ('gc_message', 'vault_file')),
    target_id      text        NOT NULL CHECK (btrim(target_id) <> ''),
    target_version text        NOT NULL CHECK (btrim(target_version) <> ''),
    job_id         text        NOT NULL,
    batch_number   integer     NOT NULL,
    recorded_at    timestamptz NOT NULL,
    PRIMARY KEY (importer_id, source_key, target_version),
    FOREIGN KEY (job_id, batch_number) REFERENCES import_batch (job_id, batch_number)
);

-- A resume reads the keys a job wrote; a re-import reads the latest version of a key.
CREATE INDEX import_item_job_idx ON import_item (job_id, batch_number);
CREATE INDEX import_item_latest_idx ON import_item (importer_id, source_key, recorded_at DESC);
