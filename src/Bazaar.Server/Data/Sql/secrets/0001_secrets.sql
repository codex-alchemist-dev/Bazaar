-- secrets/0001: the secret store (docs/design/14, Bazaar.Secrets). Namespace secrets: new migrations go here, not common/ (frozen).
--
-- Envelope encryption. secret_scope_key holds one data key per scope, wrapped under the KEK named by kek_id. Crypto-erase
-- sets destroyed_at and clears wrapped_key, so every secret_record under that scope is unreadable even if the row delete that
-- follows is interrupted. secret_record holds sealed secret versions (AES-256-GCM under the scope's data key). secret_agent holds
-- agent lifecycle state. Plaintext secret values and KEK bytes are never stored here.
--
-- The runtime role bazaar_app gets the DML the store needs. Agents never reach these tables: they are denied at the store.

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'bazaar_app') THEN
        CREATE ROLE bazaar_app NOLOGIN;
    END IF;
EXCEPTION WHEN duplicate_object OR unique_violation THEN
    NULL; -- a concurrent run created it first
END
$$;

CREATE TABLE secret_scope_key (
    scope_key    text        PRIMARY KEY CHECK (scope_key <> ''),
    kek_id       text        NOT NULL CHECK (kek_id <> ''),
    wrapped_key  bytea       NULL,
    created_at   timestamptz NOT NULL,
    destroyed_at timestamptz NULL,
    CONSTRAINT secret_scope_key_wrapped_while_live CHECK (destroyed_at IS NOT NULL OR wrapped_key IS NOT NULL)
);

CREATE TABLE secret_record (
    scope_key  text        NOT NULL CHECK (scope_key <> ''),
    name       text        NOT NULL CHECK (name <> ''),
    version    integer     NOT NULL CHECK (version >= 1),
    nonce      bytea       NOT NULL CHECK (octet_length(nonce) = 12),
    ciphertext bytea       NOT NULL,
    tag        bytea       NOT NULL CHECK (octet_length(tag) = 16),
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (scope_key, name)
);

CREATE TABLE secret_agent (
    agent_id   text        PRIMARY KEY CHECK (agent_id ~ '^[a-z0-9][a-z0-9-]{0,62}$'),
    state      text        NOT NULL CHECK (state IN ('Active', 'Quarantined', 'Revoked')),
    updated_at timestamptz NOT NULL
);

REVOKE ALL ON secret_scope_key, secret_record, secret_agent FROM PUBLIC;
REVOKE ALL ON secret_scope_key, secret_record, secret_agent FROM bazaar_app;
GRANT SELECT, INSERT, UPDATE ON secret_scope_key TO bazaar_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON secret_record TO bazaar_app;
GRANT SELECT, INSERT, UPDATE ON secret_agent TO bazaar_app;
