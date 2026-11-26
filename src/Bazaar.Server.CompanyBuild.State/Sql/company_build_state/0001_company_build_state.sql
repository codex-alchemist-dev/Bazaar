-- Company module selection and build history (docs/design/51-web-admin-module.md section 5, PROPOSED subsection "Where the
-- selection and build state live"). Migration namespace: company_build_state. No foreign keys: principal ids are opaque keys.
--
-- company_module_selection holds exactly one row. Every commit is a compare-and-set on revision.
-- company_build is the build history. A repeated idempotency key never makes a second row, and the partial unique index lets
-- at most one build be running at a time, so two concurrent rebuilds cannot both start.

CREATE TABLE company_module_selection (
    id                        smallint    PRIMARY KEY CHECK (id = 1),
    revision                  bigint      NOT NULL CHECK (revision >= 0),
    selected_module_ids       text[]      NOT NULL DEFAULT '{}',
    pending_approval_modules  text[]      NOT NULL DEFAULT '{}',
    pending_approval_ids      uuid[]      NOT NULL DEFAULT '{}',
    updated_by                uuid        NULL,
    updated_at                timestamptz NULL,
    CHECK (cardinality(pending_approval_modules) = cardinality(pending_approval_ids))
);
INSERT INTO company_module_selection (id, revision) VALUES (1, 0);

CREATE TABLE company_build (
    id                  uuid        PRIMARY KEY,
    selection_revision  bigint      NOT NULL,
    status              text        NOT NULL CHECK (status IN ('queued', 'running', 'succeeded', 'failed')),
    started_by          uuid        NOT NULL,
    started_at          timestamptz NOT NULL,
    finished_at         timestamptz NULL,
    idempotency_key     text        NOT NULL UNIQUE CHECK (btrim(idempotency_key) <> '' AND length(idempotency_key) <= 128),
    attempts            integer     NOT NULL DEFAULT 0,
    result_summary      text        NULL CHECK (length(result_summary) <= 500),
    CHECK ((status IN ('queued', 'running')) = (finished_at IS NULL))
);
CREATE UNIQUE INDEX company_build_one_running_idx ON company_build ((status)) WHERE status = 'running';
CREATE INDEX company_build_started_idx ON company_build (started_at DESC);

GRANT SELECT, UPDATE ON company_module_selection TO bazaar_app;
GRANT SELECT, INSERT, UPDATE ON company_build TO bazaar_app;
