-- Kanban task tables (doc 12, WP1). Owner: KB1. Builds on 0001 (gc, gc_message, gc_append_only, bazaar_app).
--
-- The task service (KB2) is the only writer of task state. Blocked is never stored: it is derived from task_edges
-- (Kanban/BlockedStatus). Foreign keys to agent and project tables are not here: those tables arrive with doc 06 and the
-- cross-table migration (0005, reserved), so project_id and owner_agent_id are plain uuids until then.

CREATE TABLE tasks (
    id                 uuid        PRIMARY KEY DEFAULT uuidv7(),
    project_id         uuid        NOT NULL,
    title              text        NOT NULL CHECK (btrim(title) <> ''),
    state              text        NOT NULL CHECK (state IN ('backlog', 'ready', 'in_progress', 'review', 'done')),
    owner_agent_id     uuid,
    gate               text        NOT NULL DEFAULT 'none' CHECK (gate IN ('none', 'council', 'owner')),
    origin_gc_id       uuid        NOT NULL REFERENCES gc (gc_id),
    origin_message_id  uuid,
    due_at             timestamptz,
    review_approved_by uuid,
    review_approved_at timestamptz,
    -- Optimistic lock: every transition writes WHERE version = expected and sets version + 1.
    version            bigint      NOT NULL DEFAULT 1 CHECK (version >= 1),
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    -- A task pinned from a message keeps that message; MATCH SIMPLE skips the check when origin_message_id is NULL.
    FOREIGN KEY (origin_gc_id, origin_message_id) REFERENCES gc_message (gc_id, message_id),
    CHECK ((review_approved_by IS NULL) = (review_approved_at IS NULL))
);
CREATE INDEX tasks_project_state_idx ON tasks (project_id, state);

-- One row per "blocker blocks blocked". Self-edges are rejected here; cycles and duplicates are the graph service's
-- job (KB2), under a per-project lock.
CREATE TABLE task_edges (
    blocker_id uuid        NOT NULL REFERENCES tasks (id),
    blocked_id uuid        NOT NULL REFERENCES tasks (id),
    created_by uuid        NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (blocker_id, blocked_id),
    CHECK (blocker_id <> blocked_id)
);
CREATE INDEX task_edges_blocked_idx ON task_edges (blocked_id);

-- Links are informational, except that In progress -> Review needs one (KB2 checks it). Each link is kept until removed.
CREATE TABLE task_links (
    task_id uuid NOT NULL REFERENCES tasks (id),
    kind    text NOT NULL CHECK (kind IN ('pr', 'commit', 'gc', 'vault_doc')),
    ref     text NOT NULL CHECK (btrim(ref) <> ''),
    PRIMARY KEY (task_id, kind, ref)
);
CREATE INDEX task_links_ref_idx ON task_links (kind, ref);

-- Append-only log of task changes, written in the same transaction as the change. seq orders events on the server; it is
-- not a client cursor (doc 02 decides that). Rolled-back transactions may leave gaps in seq.
CREATE TABLE task_events (
    seq     bigserial   PRIMARY KEY,
    task_id uuid        NOT NULL REFERENCES tasks (id),
    kind    text        NOT NULL CHECK (kind IN (
                'task.created', 'task.transitioned', 'task.blocked', 'task.unblocked', 'task.edge_changed',
                'task.guard_refused', 'task.gate_decided', 'task.due_passed')),
    actor   text        NOT NULL CHECK (btrim(actor) <> ''),
    payload jsonb       NOT NULL DEFAULT '{}',
    at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX task_events_task_idx ON task_events (task_id, seq);

CREATE TRIGGER task_events_append_only BEFORE UPDATE OR DELETE ON task_events
    FOR EACH ROW EXECUTE FUNCTION gc_append_only();
CREATE TRIGGER task_events_no_truncate BEFORE TRUNCATE ON task_events
    FOR EACH STATEMENT EXECUTE FUNCTION gc_append_only();

-- Runtime role: no UPDATE on identity or origin columns, and no DELETE on tasks or events.
GRANT SELECT, INSERT ON tasks, task_edges, task_links, task_events TO bazaar_app;
GRANT UPDATE (state, owner_agent_id, gate, due_at, review_approved_by, review_approved_at, version, updated_at) ON tasks TO bazaar_app;
GRANT DELETE ON task_edges, task_links TO bazaar_app;
GRANT USAGE ON task_events_seq_seq TO bazaar_app;
