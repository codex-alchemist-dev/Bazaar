-- What If runs (docs/design/26-fun-systems.md section 3 and Data). Owner: component what-if, migration namespace what_if.
-- One row per branch: the doc's what_if_run(id, decision_ref, branch, metrics_spec_hash, status, result jsonb). The doc leaves the
-- key open; id is the run id shared by a run's branches, so the key is (id, branch). tasks, source_gc_id, fork_seq, abort_reason
-- and created_at are additions the engine needs to rebuild a run; the run's own state is derived from its branch rows.
-- source_gc_id carries NO foreign key: the GC tables belong to another component. The caller guarantees the link.

CREATE TABLE what_if_run (
    id                uuid        NOT NULL,
    branch            text        NOT NULL CHECK (branch ~ '^[a-z0-9_-]{1,64}$'),
    decision_ref      text        NOT NULL CHECK (length(decision_ref) BETWEEN 1 AND 256),
    source_gc_id      uuid        NOT NULL,
    fork_seq          bigint      NOT NULL CHECK (fork_seq >= 0),
    metrics_spec_hash text        NOT NULL CHECK (metrics_spec_hash ~ '^[0-9a-f]{64}$'),
    status            text        NOT NULL CHECK (status IN ('Pending', 'Running', 'Completed', 'Aborted', 'Failed')),
    tasks             jsonb       NOT NULL,
    result            jsonb,
    abort_reason      text        CHECK (abort_reason IS NULL OR length(abort_reason) <= 2000),
    position          integer     NOT NULL CHECK (position >= 0),
    created_at        timestamptz NOT NULL,
    PRIMARY KEY (id, branch)
);

CREATE INDEX what_if_run_decision_ref_idx ON what_if_run (decision_ref);
