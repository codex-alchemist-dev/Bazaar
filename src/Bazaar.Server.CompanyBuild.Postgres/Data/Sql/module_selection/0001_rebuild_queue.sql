-- Module selection catalogue and rebuild queue (docs/design/37-distribution-matrix-and-packaging.md section 4a; PROPOSED).
-- Namespace "module_selection". Owned by Bazaar.Server.CompanyBuild.Postgres. The selection itself is NOT here: it is the settings
-- row with key 'modules.selection' (value {"revision":N,"modules":[...]}), written only by this module with compare-and-set on revision.
-- module_catalogue is a snapshot the server publishes at start, so the separate bazaar-tui process can read the module list
-- without referencing the host. module_rebuild is the queue: the selection revision is the idempotency key (one rebuild per revision).
CREATE TABLE module_catalogue (
    module_id  text        PRIMARY KEY,
    name       text        NOT NULL,
    version    text        NOT NULL,
    tag        text        NOT NULL,
    depends_on text[]      NOT NULL DEFAULT '{}',
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE module_rebuild (
    build_id           uuid        PRIMARY KEY,
    selection_revision bigint      NOT NULL UNIQUE,
    idempotency_key    text        NOT NULL UNIQUE,
    state              text        NOT NULL CHECK (state IN ('queued', 'running', 'succeeded', 'failed')),
    requested_by       uuid        NOT NULL,
    queued_at          timestamptz NOT NULL,
    started_at         timestamptz,
    finished_at        timestamptz,
    report_summary     text,
    attempt            integer     NOT NULL DEFAULT 0
);

-- One concurrent rebuild: at most one row may be queued or running.
CREATE UNIQUE INDEX module_rebuild_one_active ON module_rebuild ((true)) WHERE state IN ('queued', 'running');
