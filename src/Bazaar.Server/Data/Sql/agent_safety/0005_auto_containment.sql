-- Automatic containment records (doc 48 section 5.6). Namespace agent_safety: common/ is frozen.
-- One row per automatic containment: which agent, which rule and severity, when, and until when the owner may undo it with a
-- signed release. No alert detail and no message content is stored. No foreign keys.

CREATE TABLE auto_containment (
    id           uuid        PRIMARY KEY,
    agent_id     text        NOT NULL,
    rule_id      text        NOT NULL,
    severity     text        NOT NULL CHECK (severity IN ('Low', 'Medium', 'High', 'Critical')),
    contained_at timestamptz NOT NULL,
    undo_until   timestamptz NOT NULL,
    state        text        NOT NULL CHECK (state IN ('InForce', 'Undone', 'WindowExpired', 'Failed', 'Released')),
    CHECK (undo_until >= contained_at)
);

-- At most one in-force automatic containment per agent, so a burst of alerts cannot create two.
CREATE UNIQUE INDEX auto_containment_one_in_force ON auto_containment (agent_id) WHERE state = 'InForce';

-- Latest record per agent, and the expiry sweep.
CREATE INDEX auto_containment_agent_at ON auto_containment (agent_id, contained_at DESC);
CREATE INDEX auto_containment_due ON auto_containment (undo_until) WHERE state = 'InForce';

-- Rows are never deleted; the runtime role may only move a record's state.
GRANT SELECT, INSERT ON auto_containment TO bazaar_app;
GRANT UPDATE (state) ON auto_containment TO bazaar_app;
