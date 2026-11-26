-- Shared handoff-code store (docs/design/32-remote-access-auth-and-privacy.md sections 4 and 5; docs/design/51-web-admin-module.md
-- Session, PROPOSED). Namespace "signin". Owned by Bazaar.Server.SignIn.Postgres. Lets the server and the separate bazaar-tui
-- process share one single-use, 60 s code table. Only the SHA-256 hash (64 hex characters) of a code is stored, never the code.
-- A row is deleted when it is redeemed (DELETE ... RETURNING), so a code redeems once even with two processes racing, and a wrong
-- audience or a late attempt burns the code as well. No foreign keys leave this namespace.

CREATE TABLE handoff_code (
    code_hash      text        PRIMARY KEY CHECK (code_hash ~ '^[0-9A-F]{64}$'),
    principal_id   uuid        NOT NULL,
    principal_kind text        NOT NULL CHECK (principal_kind IN ('Human', 'Agent', 'Service')),
    device_id      uuid        NOT NULL,
    audience       text        NOT NULL CHECK (audience IN ('native', 'admin')),
    created_at     timestamptz NOT NULL,
    expires_at     timestamptz NOT NULL CHECK (expires_at > created_at)
);

-- Sweeps and the capacity trim read by expiry.
CREATE INDEX handoff_code_expires_at ON handoff_code (expires_at);
