-- Per-agent interrupt stack (docs/design/24 section 3, DECISIONS-LOG round 9 C35). One row per frame. Depth is at most two
-- non-terminal frames per agent (one active, one suspended); the service enforces it under a per-agent advisory lock, and
-- the partial unique index is the backstop for "one active frame per agent". The stack is rebuilt from these rows after a
-- crash. No foreign keys to the agent or GC tables yet: those tables arrive in later migrations, and cross-table keys
-- belong to 0005.
CREATE TABLE interrupt_frame (
    id              uuid        PRIMARY KEY,
    agent_id        uuid        NOT NULL,
    parent_frame_id uuid        NULL REFERENCES interrupt_frame (id),
    source          text        NOT NULL CHECK (source IN ('owner', 'system_build', 'system_security')),
    reason          text        NOT NULL CHECK (length(reason) BETWEEN 1 AND 500),
    gc_id           uuid        NOT NULL,
    state           text        NOT NULL CHECK (state IN ('active', 'suspended', 'done', 'cancelled')),
    resume_turn_id  uuid        NULL,
    pushed_at       timestamptz NOT NULL,
    ttl_at          timestamptz NULL,
    version         bigint      NOT NULL DEFAULT 1 CHECK (version >= 1),
    CONSTRAINT interrupt_frame_not_own_parent CHECK (parent_frame_id IS NULL OR parent_frame_id <> id)
);

CREATE UNIQUE INDEX interrupt_frame_one_active_idx ON interrupt_frame (agent_id) WHERE state = 'active';
CREATE INDEX interrupt_frame_live_idx ON interrupt_frame (agent_id, pushed_at, id) WHERE state IN ('active', 'suspended');
