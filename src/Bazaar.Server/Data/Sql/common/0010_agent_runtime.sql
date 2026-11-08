-- Agent runtime tables (docs/design/06-agent-runtime.md, work package 1). Owner: AR1.
-- Owns agent, inbox_item, turn, turn_step and agent_lease (decision C04). No foreign keys to GC, Soul, model-policy
-- or settings tables: the cross-table keys arrive in 0005. Conventions (doc 04): UUIDv7 ids, timestamptz,
-- text enums with CHECK, RESTRICT foreign keys, JSONB only for opaque payloads.
-- Leases and due times use database now(), never the host clock (as in 0006).

CREATE TABLE agent (
    agent_id        uuid        PRIMARY KEY DEFAULT uuidv7(),
    name            text        NOT NULL CHECK (length(name) BETWEEN 1 AND 64),
    -- Soul version (doc 07) and model policy (agent_model_policy, doc 05) are referenced by agent_id, not stored here.
    soul_version_id uuid,
    state           text        NOT NULL CHECK (state IN ('created', 'onboarding', 'active', 'resting', 'retired')),
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now()
);

-- One row per wake that is waiting. kind is a wake reason from doc 06 ("Wake model"); later docs add kinds without a
-- migration, so the check is only the naming shape. A GC-backed item carries (gc_id, gc_seq); other items carry source_ref.
CREATE TABLE inbox_item (
    inbox_item_id uuid        PRIMARY KEY DEFAULT uuidv7(),
    agent_id      uuid        NOT NULL REFERENCES agent (agent_id) ON DELETE RESTRICT,
    kind          text        NOT NULL CHECK (kind ~ '^[a-z][a-z_]*$'),
    gc_id         uuid,
    gc_seq        bigint,
    source_ref    text        CHECK (source_ref IS NULL OR length(source_ref) BETWEEN 1 AND 256),
    -- Lower is higher priority. Computed from kind by the inbox code (doc 06 priority order).
    priority      smallint    NOT NULL CHECK (priority >= 0),
    state         text        NOT NULL DEFAULT 'pending' CHECK (state IN ('pending', 'claimed', 'done')),
    due_at        timestamptz NOT NULL DEFAULT now(),
    claimed_by    text,
    claimed_at    timestamptz,
    done_at       timestamptz,
    created_at    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT inbox_item_gc_pair CHECK ((gc_id IS NULL) = (gc_seq IS NULL)),
    CONSTRAINT inbox_item_claim_matches_state CHECK ((state = 'claimed') = (claimed_by IS NOT NULL AND claimed_at IS NOT NULL)),
    CONSTRAINT inbox_item_done_matches_state CHECK ((state = 'done') = (done_at IS NOT NULL))
);

-- Duplicate enqueue is a no-op (doc 06, doc 13). Partial indexes: NULLs must not collapse unrelated non-GC items.
CREATE UNIQUE INDEX inbox_item_gc_dedupe_idx ON inbox_item (agent_id, gc_id, gc_seq, kind) WHERE gc_id IS NOT NULL;
CREATE UNIQUE INDEX inbox_item_ref_dedupe_idx ON inbox_item (agent_id, kind, source_ref)
    WHERE gc_id IS NULL AND source_ref IS NOT NULL;
CREATE INDEX inbox_item_pending_idx ON inbox_item (agent_id, priority, due_at, inbox_item_id) WHERE state = 'pending';
CREATE INDEX inbox_item_claimed_by_idx ON inbox_item (claimed_by) WHERE state = 'claimed';

CREATE TABLE turn (
    turn_id          uuid        PRIMARY KEY DEFAULT uuidv7(),
    agent_id         uuid        NOT NULL REFERENCES agent (agent_id) ON DELETE RESTRICT,
    -- Taint is keyed by session (doc 06 "Session key", doc 15). Subagents inherit the parent's session_id.
    session_id       uuid        NOT NULL,
    role             text        NOT NULL CHECK (role IN ('actor', 'reader')),
    parent_turn_id   uuid        REFERENCES turn (turn_id) ON DELETE RESTRICT,
    depth            integer     NOT NULL DEFAULT 0 CHECK (depth >= 0),
    wake_reason      text        NOT NULL CHECK (wake_reason ~ '^[a-z][a-z_]*$'),
    status           text        NOT NULL DEFAULT 'running' CHECK (status IN ('running', 'completed', 'failed', 'abandoned')),
    started_at       timestamptz NOT NULL DEFAULT now(),
    ended_at         timestamptz,
    context_manifest jsonb       NOT NULL DEFAULT '{}'::jsonb,
    CONSTRAINT turn_end_matches_status CHECK ((status = 'running') = (ended_at IS NULL)),
    CONSTRAINT turn_depth_matches_parent CHECK ((parent_turn_id IS NULL) = (depth = 0))
);

CREATE INDEX turn_agent_running_idx ON turn (agent_id) WHERE status = 'running';
CREATE INDEX turn_session_idx ON turn (session_id, started_at);

-- Each step is written before the next action (doc 06, "Concurrency and recovery"). A step starts pending and is
-- completed in place; an abandoned or unknown step is kept for audit, and a re-issued call gets a new n.
CREATE TABLE turn_step (
    turn_id      uuid        NOT NULL REFERENCES turn (turn_id) ON DELETE RESTRICT,
    n            integer     NOT NULL CHECK (n >= 1),
    kind         text        NOT NULL CHECK (kind IN ('model', 'tool', 'post', 'memory')),
    status       text        NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'completed', 'abandoned', 'unknown')),
    request_hash text        NOT NULL CHECK (length(request_hash) BETWEEN 1 AND 128),
    -- Only a completed step has a response; personal data in it is encrypted per person by the caller (doc 04, C01).
    response     jsonb,
    idempotent   boolean     NOT NULL DEFAULT false,
    started_at   timestamptz NOT NULL DEFAULT now(),
    completed_at timestamptz,
    PRIMARY KEY (turn_id, n),
    CONSTRAINT turn_step_completed_has_response CHECK (
        status <> 'completed' OR (response IS NOT NULL AND completed_at IS NOT NULL)),
    CONSTRAINT turn_step_open_has_no_completion CHECK (status <> 'pending' OR completed_at IS NULL)
);

-- One active turn per agent. The row is the lease; a second holder may take it only after expires_at (doc 06).
CREATE TABLE agent_lease (
    agent_id    uuid        PRIMARY KEY REFERENCES agent (agent_id) ON DELETE RESTRICT,
    holder      text        NOT NULL CHECK (length(holder) BETWEEN 1 AND 128),
    acquired_at timestamptz NOT NULL DEFAULT now(),
    expires_at  timestamptz NOT NULL
);

CREATE INDEX agent_lease_expiry_idx ON agent_lease (expires_at);
