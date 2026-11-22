-- Agent-to-Soul binding (docs/design/07-soul-system.md "Versioning", docs/design/06 agent row).
-- Append-only: a bind, a rebind or a retirement is a new row, never an update. The current binding of an agent is its row
-- with the highest seq. There is no foreign key to soul_versions or to agent: the Souls module validates through its own
-- code, and GC and the agent runtime reach this table only through ISoulBindings (owner, 2026-11-16).

CREATE TABLE soul_agent_binding (
    seq         bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    agent_id    uuid        NOT NULL,
    soul_id     uuid        NULL,
    event       text        NOT NULL CHECK (event IN ('bound', 'retired')),
    reason      text        NOT NULL CHECK (length(btrim(reason)) > 0),
    recorded_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT soul_agent_binding_soul_by_event CHECK ((event = 'bound') = (soul_id IS NOT NULL))
);

CREATE INDEX soul_agent_binding_agent_idx ON soul_agent_binding (agent_id, seq DESC);
CREATE INDEX soul_agent_binding_soul_idx ON soul_agent_binding (soul_id) WHERE soul_id IS NOT NULL;

REVOKE ALL ON soul_agent_binding FROM PUBLIC;
REVOKE ALL ON soul_agent_binding FROM bazaar_app;
GRANT SELECT, INSERT ON soul_agent_binding TO bazaar_app;
