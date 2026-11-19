-- Kill switch and containment state (doc 15, "Audit log and kill switch"). Namespace agent_safety: common/ is frozen, so the
-- stop state lives in its own folder. Plain PostgreSQL; no foreign keys.
-- The engine writes each engage and resume here before it changes its in-memory cache, and reloads these rows on start, so a
-- restart keeps every stop and every contained agent. Rows are replaced (delete, then insert) on re-engage and removed on
-- resume. The audit chain keeps the history; these tables hold only the current state.

-- At most one global stop: the singleton column is always true and is the primary key.
CREATE TABLE kill_switch_global (
    singleton  boolean     PRIMARY KEY DEFAULT true CHECK (singleton),
    actor_id   text        NOT NULL,
    reason     text        NOT NULL,
    engaged_at timestamptz NOT NULL
);

CREATE TABLE kill_switch_agent_stop (
    agent_id   text        PRIMARY KEY,
    scope      text        NOT NULL DEFAULT 'agent' CHECK (scope = 'agent'),
    reason     text        NOT NULL,
    actor_id   text        NOT NULL,
    engaged_at timestamptz NOT NULL
);

-- Agents whose VMs and tokens are held by an agent stop. actor_id is the actor the host recorded for the containment.
CREATE TABLE agent_containment (
    agent_id     text        PRIMARY KEY,
    contained_at timestamptz NOT NULL,
    actor_id     text        NOT NULL
);

GRANT SELECT, INSERT, DELETE ON kill_switch_global TO bazaar_app;
GRANT SELECT, INSERT, DELETE ON kill_switch_agent_stop TO bazaar_app;
GRANT SELECT, INSERT, DELETE ON agent_containment TO bazaar_app;
