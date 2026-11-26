-- Owner device enrolment (docs/design/32-remote-access-auth-and-privacy.md section 5b, PROPOSED). Component migration 0001 in the
-- device_enrolment namespace (common/ is frozen). Needs the remote namespace first (human, device, approval_key) and common 0004
-- (audit_log); the component declares both in its manifest.
--
--   * enrolment_challenge: the single-use nonce of ceremony step 1, bound to one human and one device, valid 5 minutes.
--   * enrolment_code: the one-time console code. Only its SHA-256 is stored; the code itself never reaches the database.
--   * biometric_credential: the platform credential of doc 48 section 8.1. One live row per device.
-- approval_key (remote/0001) is written by the same transaction; this file adds no change to it.

CREATE TABLE enrolment_challenge (
    nonce      bytea       PRIMARY KEY CHECK (octet_length(nonce) = 32),
    human_id   uuid        NOT NULL REFERENCES human (id),
    device_id  uuid        NOT NULL REFERENCES device (id),
    expires_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    spent_at   timestamptz
);

CREATE INDEX enrolment_challenge_expires ON enrolment_challenge (expires_at);

CREATE TABLE enrolment_code (
    code_hash  bytea       PRIMARY KEY CHECK (octet_length(code_hash) = 32),
    human_id   uuid        NOT NULL REFERENCES human (id),
    expires_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    spent_at   timestamptz
);

CREATE INDEX enrolment_code_expires ON enrolment_code (expires_at);

CREATE TABLE biometric_credential (
    credential_id    bytea       PRIMARY KEY CHECK (octet_length(credential_id) BETWEEN 1 AND 1024),
    human_id         uuid        NOT NULL REFERENCES human (id),
    device_id        uuid        NOT NULL REFERENCES device (id),
    public_key       bytea       NOT NULL CHECK (octet_length(public_key) > 0),
    sign_count       bigint      NOT NULL DEFAULT 0 CHECK (sign_count BETWEEN 0 AND 4294967295),
    attested_factors text[]      NOT NULL DEFAULT '{}'
        CHECK (attested_factors <@ ARRAY['face-depth', 'face-multi', 'voice', 'fingerprint', 'hardware-key']::text[]),
    created_at       timestamptz NOT NULL DEFAULT now(),
    revoked_at       timestamptz
);

-- A device holds one live pair; re-enrolment needs a revoked first row (doc 32 section 5b, lifecycle).
CREATE UNIQUE INDEX biometric_credential_device_live ON biometric_credential (device_id) WHERE revoked_at IS NULL;
CREATE INDEX biometric_credential_human ON biometric_credential (human_id);

-- The runtime role reads and inserts spend state; challenge and code rows are updated only to mark them spent.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'bazaar_app') THEN
        GRANT SELECT, INSERT, UPDATE ON enrolment_challenge, enrolment_code TO bazaar_app;
        GRANT SELECT, INSERT, UPDATE ON biometric_credential TO bazaar_app;
        GRANT SELECT, INSERT ON approval_key TO bazaar_app;
        GRANT SELECT ON device TO bazaar_app;
    END IF;
END
$$;
