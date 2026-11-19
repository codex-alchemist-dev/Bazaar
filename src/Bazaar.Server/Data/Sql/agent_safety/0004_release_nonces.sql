-- Pending owner release nonces (doc 15, signed release). Namespace agent_safety: common/ is frozen.
-- A nonce is single-use and expires after two minutes. It is stored so a restart cannot make a used or pending nonce valid
-- again: the consume is one DELETE ... RETURNING, so two concurrent releases cannot both win. Plain PostgreSQL; no foreign keys.

CREATE TABLE release_nonces (
    nonce      bytea       PRIMARY KEY CHECK (octet_length(nonce) = 16),
    agent_id   text        NOT NULL,
    device_id  text        NOT NULL,
    expires_at timestamptz NOT NULL
);

-- The prune on add deletes expired rows by expiry instant.
CREATE INDEX release_nonces_expires_at ON release_nonces (expires_at);

GRANT SELECT, INSERT, DELETE ON release_nonces TO bazaar_app;
