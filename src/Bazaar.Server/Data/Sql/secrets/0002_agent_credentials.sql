-- secrets/0002: per-agent credentials (IAgentCredentialIssuer). Namespace secrets: new migrations go here, not common/ (frozen).
--
-- Only a SHA-256 hash of each token is stored, so a database read cannot replay a credential. revoked_at is set once and never
-- cleared. Issuance and revocation are audited in the shared chain, not in this table.

CREATE TABLE secret_agent_credential (
    issuance_id text        PRIMARY KEY CHECK (issuance_id <> ''),
    agent_id    text        NOT NULL REFERENCES secret_agent (agent_id),
    token_hash  bytea       NOT NULL CHECK (octet_length(token_hash) = 32),
    scope       text        NOT NULL CHECK (scope <> '' AND length(scope) <= 96),
    issued_at   timestamptz NOT NULL,
    expires_at  timestamptz NOT NULL,
    revoked_at  timestamptz NULL,
    CONSTRAINT secret_agent_credential_lifetime CHECK (expires_at > issued_at)
);

CREATE INDEX secret_agent_credential_agent_idx ON secret_agent_credential (agent_id);

REVOKE ALL ON secret_agent_credential FROM PUBLIC;
REVOKE ALL ON secret_agent_credential FROM bazaar_app;
GRANT SELECT, INSERT, UPDATE ON secret_agent_credential TO bazaar_app;
