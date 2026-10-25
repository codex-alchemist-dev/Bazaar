-- Background job queue (doc 03, "Background jobs"). Due jobs are claimed with FOR UPDATE SKIP LOCKED.
-- Leases and due times use database now(), never the host clock. An expired lease makes a job claimable again,
-- so handlers must be idempotent. dedupe_key is one-shot: once a key is used, later enqueues with it are ignored.
CREATE TABLE job (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    kind         text        NOT NULL,
    dedupe_key   text        UNIQUE,
    run_at       timestamptz NOT NULL,
    state        text        NOT NULL CHECK (state IN ('queued', 'running', 'succeeded', 'failed')),
    lease_owner  text,
    lease_until  timestamptz,
    attempts     integer     NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    payload      jsonb       NOT NULL DEFAULT '{}'::jsonb,
    last_error   text,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT job_lease_matches_state CHECK ((state = 'running') = (lease_owner IS NOT NULL AND lease_until IS NOT NULL))
);

CREATE INDEX job_queued_run_at_idx ON job (run_at, id) WHERE state = 'queued';
CREATE INDEX job_running_lease_idx ON job (lease_until) WHERE state = 'running';
