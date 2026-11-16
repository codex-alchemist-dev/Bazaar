-- 0003 (component remote): single-use nonces of owner-signed payloads (doc 32 section 5a).
-- A nonce is spent once, so the row outlives the payload's expiry only until the purge below removes it. Replay
-- protection survives a restart because the row is durable. Only the nonce is stored; the payload is not.

CREATE TABLE owner_nonce (
    nonce      bytea       PRIMARY KEY CHECK (octet_length(nonce) = 16),
    expires_at timestamptz NOT NULL
);

CREATE INDEX owner_nonce_expires ON owner_nonce (expires_at);
