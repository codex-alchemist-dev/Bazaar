-- Agent token budgets (doc 15 quota table, doc 48 section 11.1). Namespace agent_safety: common/ is frozen, so the
-- budget log lives in its own folder. Plain PostgreSQL; no foreign keys.
-- One row per consumed spend. The service reads each agent's rows inside the widest window and prunes rows older than
-- that window, so the table stays bounded. The runtime role may insert, read and prune spends; it never edits them.

CREATE TABLE agent_token_spend (
    id       bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    agent_id text        NOT NULL,
    at       timestamptz NOT NULL,
    tokens   bigint      NOT NULL CHECK (tokens > 0)
);
CREATE INDEX agent_token_spend_agent_at_idx ON agent_token_spend (agent_id, at);

GRANT SELECT, INSERT, DELETE ON agent_token_spend TO bazaar_app;
