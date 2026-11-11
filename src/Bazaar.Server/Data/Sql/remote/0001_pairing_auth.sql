-- 0001 (component remote): humans, devices, pairing, sign-in credentials, privacy records and owner approval keys.
-- Doc 32 "Data and interfaces" (pairing, tokens, sign-in, privacy and approval keys) and section 5 (refresh rotation).
-- Owner: the pairing, tokens and authorization thread (group 32, WP1).
--
-- Choices made here, against doc 32's table list:
--   * Refresh rotation lives in refresh_credential (one row per issued refresh secret, hash only). device carries no
--     refresh_hash column: a current-hash column would duplicate the "current" row and drift from it. Reuse detection
--     is "hash matches a retired row", so the chain keeps every retired hash until the device is deleted.
--   * Only the hash of every bearer-like secret is stored (refresh, handoff, user_code, device_code). Plain values never
--     reach this schema.
--   * No foreign keys to GC, agent or erasure tables. human and device are the only cross-row references in this file.
--   * Each table is append-friendly: revocation sets revoked_at rather than deleting.
--
-- Migration namespace: remote/ (owned by RemoteComponent). The runner applies it as a component namespace.

CREATE TABLE human (
    id           uuid        PRIMARY KEY,
    display_name text        NOT NULL CHECK (length(display_name) BETWEEN 1 AND 200),
    role         text        NOT NULL CHECK (role IN ('owner', 'coworker')),
    status       text        NOT NULL CHECK (status IN ('active', 'suspended', 'removed')),
    created_at   timestamptz NOT NULL DEFAULT now()
);

-- One owner per instance in v1 (doc 32 section 6, open question 1). The partial unique index makes a second owner an error.
CREATE UNIQUE INDEX human_one_owner ON human (role) WHERE role = 'owner';

CREATE TABLE device (
    id          uuid        PRIMARY KEY,
    human_id    uuid        NOT NULL REFERENCES human (id),
    label       text        NOT NULL CHECK (length(label) BETWEEN 1 AND 200),
    class       text        NOT NULL CHECK (class IN ('windows', 'linux', 'android', 'terminal', 'other')),
    revision    integer     NOT NULL DEFAULT 1 CHECK (revision >= 1),
    created_at  timestamptz NOT NULL DEFAULT now(),
    revoked_at  timestamptz
);

CREATE INDEX device_human ON device (human_id);

-- Refresh secrets, rotated on each use (doc 32 section 5). Exactly one live row per device (retired_at IS NULL).
CREATE TABLE refresh_credential (
    id          uuid        PRIMARY KEY,
    device_id   uuid        NOT NULL REFERENCES device (id),
    hash        bytea       NOT NULL UNIQUE CHECK (octet_length(hash) = 32),
    issued_at   timestamptz NOT NULL DEFAULT now(),
    retired_at  timestamptz,
    expires_at  timestamptz NOT NULL
);

CREATE UNIQUE INDEX refresh_credential_live ON refresh_credential (device_id) WHERE retired_at IS NULL;

CREATE TABLE pairing_offer (
    id              uuid        PRIMARY KEY,
    created_by      uuid        NOT NULL REFERENCES human (id),
    role            text        NOT NULL CHECK (role IN ('owner', 'coworker')),
    user_code_hash  bytea       NOT NULL UNIQUE CHECK (octet_length(user_code_hash) = 32),
    device_code_hash bytea      UNIQUE CHECK (device_code_hash IS NULL OR octet_length(device_code_hash) = 32),
    expires_at      timestamptz NOT NULL,
    state           text        NOT NULL DEFAULT 'pending'
                    CHECK (state IN ('pending', 'approved', 'redeemed', 'expired', 'burned', 'denied')),
    attempts        integer     NOT NULL DEFAULT 0 CHECK (attempts >= 0)
);

CREATE TABLE passkey_credential (
    id            uuid        PRIMARY KEY,
    human_id      uuid        NOT NULL REFERENCES human (id),
    credential_id bytea       NOT NULL UNIQUE,
    public_key    bytea       NOT NULL,
    sign_count    bigint      NOT NULL DEFAULT 0 CHECK (sign_count >= 0),
    created_at    timestamptz NOT NULL DEFAULT now(),
    revoked_at    timestamptz
);

CREATE TABLE recovery_code (
    id       uuid        PRIMARY KEY,
    human_id uuid        NOT NULL REFERENCES human (id),
    hash     bytea       NOT NULL CHECK (octet_length(hash) = 32),
    used_at  timestamptz,
    UNIQUE (human_id, hash)
);

-- One record per instance (doc 32 section 9). The instance_id is the single row's key.
CREATE TABLE privacy_settings (
    instance_id text  PRIMARY KEY CHECK (length(instance_id) > 0),
    profile     text  NOT NULL DEFAULT 'personal' CHECK (profile IN ('personal', 'company-global')),
    overlays    jsonb NOT NULL DEFAULT '["eu-uk","california","philippines"]'::jsonb,
    contact     text,
    retention   jsonb NOT NULL DEFAULT '{}'::jsonb
);

CREATE TABLE privacy_request (
    id           uuid        PRIMARY KEY,
    subject_ref  uuid        NOT NULL REFERENCES human (id),
    kind         text        NOT NULL CHECK (kind IN ('access', 'deletion', 'portability', 'correction')),
    status       text        NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'completed', 'rejected')),
    due_at       timestamptz NOT NULL,
    completed_at timestamptz
);

CREATE INDEX privacy_request_open_due ON privacy_request (due_at) WHERE status = 'open';

-- Owner approval signing keys (doc 32 section 5a). Public half only; the private key never reaches the server.
CREATE TABLE approval_key (
    id         uuid        PRIMARY KEY,
    human_id   uuid        NOT NULL REFERENCES human (id),
    device_id  uuid        NOT NULL REFERENCES device (id),
    public_key bytea       NOT NULL,
    alg        text        NOT NULL DEFAULT 'ecdsa-p256' CHECK (alg IN ('ecdsa-p256')),
    created_at timestamptz NOT NULL DEFAULT now(),
    revoked_at timestamptz
);

CREATE INDEX approval_key_device_live ON approval_key (device_id) WHERE revoked_at IS NULL;
