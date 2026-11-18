-- Per-agent tool grants (doc 06, "Created": an agent row carries its grants; doc 07, "Promotion": a grant change is a Tier 2
-- approval). Namespace agent_turns (component agent-turns). Plain PostgreSQL; no cross-table foreign keys, as in 0001.
-- Grants are rows, not a column, so a revocation is recorded and never erased. A grant is active while revoked_at is NULL.

CREATE TABLE agent_tool_grant (
    agent_id    uuid        NOT NULL,
    tool        text        NOT NULL CHECK (length(btrim(tool)) > 0),
    granted_by  text        NOT NULL CHECK (length(btrim(granted_by)) > 0),
    granted_at  timestamptz NOT NULL DEFAULT now(),
    revoked_at  timestamptz,
    PRIMARY KEY (agent_id, tool)
);
CREATE INDEX agent_tool_grant_active_idx ON agent_tool_grant (agent_id) WHERE revoked_at IS NULL;

GRANT SELECT, INSERT, UPDATE ON agent_tool_grant TO bazaar_app;
