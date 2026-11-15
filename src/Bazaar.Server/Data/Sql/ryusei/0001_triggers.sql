-- RyuseiFuda schedule tables (doc 38, Layer 2, WP-R3). Owner: WP-R3.
-- Builds on common/0001 (gc, gc_append_only) and common/0012 (tasks). Plain PostgreSQL; no extensions.
-- Triggers and fires are durable rows: no in-memory timers. ryusei_fire is append-only, like task_events.

CREATE TABLE ryusei_trigger (
    trigger_id           uuid        PRIMARY KEY DEFAULT uuidv7(),
    project_id           uuid        NOT NULL,
    created_by           uuid        NOT NULL,
    kind                 text        NOT NULL CHECK (kind IN ('timer', 'interval', 'watcher', 'event')),
    target_agent_ids     uuid[]      NOT NULL CHECK (cardinality(target_agent_ids) BETWEEN 1 AND 8),
    purpose              text        NOT NULL CHECK (btrim(purpose) <> ''),
    spec                 jsonb       NOT NULL,
    task_id              uuid        REFERENCES tasks (id),
    attach_gc_id         uuid        REFERENCES gc (gc_id),
    ping_mode            text        NOT NULL CHECK (ping_mode IN ('none', 'attach', 'new_gc')),
    state                text        NOT NULL CHECK (state IN ('active', 'paused', 'fired', 'expired', 'cancelled')),
    next_fire_at         timestamptz,
    fire_count           bigint      NOT NULL DEFAULT 0,
    consecutive_refusals int         NOT NULL DEFAULT 0,
    max_fires            bigint,
    expires_at           timestamptz,
    chain_depth          int         NOT NULL DEFAULT 0 CHECK (chain_depth BETWEEN 0 AND 3),
    version              bigint      NOT NULL DEFAULT 1,
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now(),
    -- attach_gc_id is set exactly when ping_mode is 'attach': an attach ping needs a GC, and the other modes forbid one.
    CHECK ((ping_mode = 'attach') = (attach_gc_id IS NOT NULL))
);
CREATE INDEX ryusei_trigger_due_idx ON ryusei_trigger (next_fire_at) WHERE state = 'active';

-- Append-only record of every fire, including refusals and skips. The UNIQUE key makes a crash-retried fire a no-op.
CREATE TABLE ryusei_fire (
    fire_id       uuid        PRIMARY KEY DEFAULT uuidv7(),
    trigger_id    uuid        NOT NULL REFERENCES ryusei_trigger (trigger_id),
    scheduled_for timestamptz NOT NULL,
    outcome       text        NOT NULL CHECK (outcome IN ('woke', 'coalesced', 'refused_cap', 'refused_cooldown',
                                                          'refused_loop', 'skipped_inflight', 'skipped_resting', 'error')),
    cause         jsonb,
    gc_seq        bigint,           -- set when ping_mode is attach or new_gc (the system line)
    at            timestamptz NOT NULL DEFAULT now(),
    UNIQUE (trigger_id, scheduled_for)
);
CREATE TRIGGER ryusei_fire_append_only BEFORE UPDATE OR DELETE ON ryusei_fire
    FOR EACH ROW EXECUTE FUNCTION gc_append_only();
CREATE TRIGGER ryusei_fire_no_truncate BEFORE TRUNCATE ON ryusei_fire
    FOR EACH STATEMENT EXECUTE FUNCTION gc_append_only();
