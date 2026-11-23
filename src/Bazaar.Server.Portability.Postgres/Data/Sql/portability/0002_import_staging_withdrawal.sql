-- Import staging and withdrawal records (docs/design/44-data-portability.md sections 8.6 to 8.8, PROPOSED).
-- Namespace "portability". Builds on 0001 (import_job). Only paths, sizes and hashes are stored, never staged content (section 8.5).
-- Rows are history: sweeping staging sets deleted_at, and a withdrawal never deletes a row (section 8.8).

-- One row per staged payload file. The file lives under the job's staging directory at relative_path.
CREATE TABLE import_staging (
    job_id        text        NOT NULL REFERENCES import_job (job_id),
    source_key    text        NOT NULL CHECK (btrim(source_key) <> ''),
    relative_path text        NOT NULL CHECK (btrim(relative_path) <> '' AND relative_path !~ '^[/\\]' AND relative_path !~ '(^|[/\\])\.\.([/\\]|$)'),
    size_bytes    bigint      NOT NULL CHECK (size_bytes >= 0),
    sha256        text        NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    staged_at     timestamptz NOT NULL,
    deleted_at    timestamptz,
    PRIMARY KEY (job_id, source_key)
);

-- The staging sweep and the quota read the job's rows that are not deleted yet.
CREATE INDEX import_staging_live_idx ON import_staging (job_id) WHERE deleted_at IS NULL;

-- One row per withdrawn record; also the resume record of a withdrawal. For kanban_task the source_key is the task id.
CREATE TABLE import_withdrawal (
    job_id       text        NOT NULL REFERENCES import_job (job_id),
    source_key   text        NOT NULL CHECK (btrim(source_key) <> ''),
    target_kind  text        NOT NULL CHECK (target_kind IN ('gc_message', 'vault_file', 'kanban_task')),
    target_id    text        NOT NULL CHECK (btrim(target_id) <> ''),
    withdrawn_at timestamptz NOT NULL,
    PRIMARY KEY (job_id, source_key, target_kind)
);
