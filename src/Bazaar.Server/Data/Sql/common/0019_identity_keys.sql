-- Identity keys, credentials and browser profiles (docs/design/14 section "Data and interfaces", owner WP 14 WP1).
-- Ciphertext is AEAD output made with a per-Soul or install-scope DEK; each DEK is wrapped by the host KEK and
-- stored in soul_dek. No plaintext secret is ever stored here. No foreign keys to agent, soul or principal tables:
-- those keys arrive with the cross-table migration (0005, reserved). Ids are supplied by the writer.
-- Status: the sketch in doc 14 is PROPOSED; this migration builds only the columns the sketch names, plus CHECKs.

-- Data-encryption keys. scope is a soul id as text, or 'install' for provider and GitHub App keys.
-- Crypto-shred: setting retired_at and clearing wrapped_dek makes every ciphertext under the DEK unreadable.
CREATE TABLE soul_dek (
    dek_id      uuid        PRIMARY KEY,
    scope       text        NOT NULL CHECK (scope <> ''),
    wrapped_dek bytea       NULL,
    kek_id      text        NOT NULL CHECK (kek_id <> ''),
    created_at  timestamptz NOT NULL DEFAULT now(),
    retired_at  timestamptz NULL,
    CONSTRAINT soul_dek_wrapped_while_live CHECK (retired_at IS NOT NULL OR wrapped_dek IS NOT NULL)
);

-- One live DEK per scope. Rotation retires the old row and inserts a new one.
CREATE UNIQUE INDEX soul_dek_one_live_idx ON soul_dek (scope) WHERE retired_at IS NULL;

-- Stored secrets: provider keys, App keys, owner-class credentials, OAuth refresh tokens, cookies, SSH keys, TOTP
-- seeds and recovery codes. handle is a non-secret label the caller uses to find the row.
CREATE TABLE credential (
    credential_id uuid        PRIMARY KEY,
    soul_id       text        NULL,
    service       text        NOT NULL CHECK (service <> ''),
    kind          text        NOT NULL CHECK (kind IN ('provider_key', 'app_key', 'owner', 'oauth', 'cookie', 'ssh', 'totp', 'recovery')),
    handle        text        NOT NULL CHECK (handle <> ''),
    ciphertext    bytea       NOT NULL,
    nonce         bytea       NOT NULL CHECK (octet_length(nonce) = 12),
    dek_id        uuid        NOT NULL REFERENCES soul_dek (dek_id),
    tier          text        NOT NULL CHECK (tier IN ('ordinary', 'sensitive')),
    status        text        NOT NULL CHECK (status IN ('active', 'rotated', 'revoked')),
    created_at    timestamptz NOT NULL DEFAULT now(),
    rotated_at    timestamptz NULL,
    expires_at    timestamptz NULL,
    CONSTRAINT credential_expiry_after_create CHECK (expires_at IS NULL OR expires_at > created_at)
);

-- At most one active credential per service, handle and scope.
CREATE UNIQUE INDEX credential_active_handle_idx ON credential (service, handle, COALESCE(soul_id, '')) WHERE status = 'active';

-- Runtime leases handed to an agent for one credential (doc 14, RequestAccess). Ended by expiry or revocation.
CREATE TABLE credential_lease (
    lease_id      uuid        PRIMARY KEY,
    agent_id      uuid        NOT NULL,
    credential_id uuid        NOT NULL REFERENCES credential (credential_id),
    scope         text        NOT NULL CHECK (scope <> ''),
    issued_at     timestamptz NOT NULL DEFAULT now(),
    expires_at    timestamptz NOT NULL,
    revoked_at    timestamptz NULL,
    CONSTRAINT credential_lease_span CHECK (expires_at > issued_at)
);

CREATE INDEX credential_lease_agent_idx ON credential_lease (agent_id, expires_at) WHERE revoked_at IS NULL;

-- Browser profiles per agent. profile_kind: 'anonymous' (full Chromium, no account) or 'account' (Passport-backed).
-- The ciphertext holds the profile archive; cookies are materialised only when a lease allows it (doc 14).
CREATE TABLE browser_profile (
    profile_id   uuid        PRIMARY KEY,
    agent_id     uuid        NOT NULL,
    profile_kind text        NOT NULL CHECK (profile_kind IN ('anonymous', 'account')),
    ciphertext   bytea       NOT NULL,
    nonce        bytea       NOT NULL CHECK (octet_length(nonce) = 12),
    dek_id       uuid        NOT NULL REFERENCES soul_dek (dek_id),
    updated_at   timestamptz NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX browser_profile_anonymous_idx ON browser_profile (agent_id) WHERE profile_kind = 'anonymous';
