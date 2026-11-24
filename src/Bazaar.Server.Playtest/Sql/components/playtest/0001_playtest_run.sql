-- Playtest run table (docs/design/26-fun-systems.md section 7 and Data and interfaces). Owner: component playtest.
-- Component migration 0001 in the playtest namespace (not common, which is frozen). The first five columns are the doc's
-- playtest_run; the Tier A columns record the headless result. No foreign keys: the table depends on nothing else.
-- input_log_ref and snapshot_blob_refs are Tier B only and stay empty for Tier A runs.

CREATE TABLE playtest_run (
    id                  uuid        PRIMARY KEY,
    engine              text        NOT NULL CHECK (engine IN ('godot', 'luau')),
    build_ref           text        NOT NULL CHECK (length(build_ref) BETWEEN 1 AND 256),
    mode                text        NOT NULL CHECK (mode IN ('tier_a')),
    input_log_ref       text        NULL,
    snapshot_blob_refs  jsonb       NOT NULL DEFAULT '[]'::jsonb,
    scenario_id         text        NOT NULL CHECK (length(scenario_id) BETWEEN 1 AND 128),
    status              text        NOT NULL CHECK (status IN ('passed', 'assertions_failed', 'project_changed', 'timed_out', 'host_failed', 'profile_rejected')),
    state_dump_hash     text        NULL CHECK (state_dump_hash IS NULL OR state_dump_hash ~ '^[0-9a-f]{64}$'),
    project_hash_before text        NULL,
    project_hash_after  text        NULL,
    assertion_specs     jsonb       NOT NULL DEFAULT '[]'::jsonb,
    timeout_seconds     integer     NOT NULL CHECK (timeout_seconds >= 1),
    assertion_results   jsonb       NOT NULL DEFAULT '[]'::jsonb,
    reputation_weight   integer     NOT NULL DEFAULT 0 CHECK (reputation_weight = 0),
    created_at          timestamptz NOT NULL DEFAULT now()
);
