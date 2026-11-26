-- Device enrolment state (doc 32 section 5b, doc 48 section 8.1). Namespace device_enrolment, owned by the
-- Bazaar.Server.DeviceEnrolment.Postgres module. It runs after the remote namespace, which owns human, device and approval_key.
--
-- enrolment_challenge and enrolment_code are single-use: spent_at is set in the commit transaction and never cleared.
-- Codes are stored only as SHA-256 hashes. biometric_credential holds the public half of a platform credential and what its
-- attestation allowed; the verifier reads it and advances sign_count.

CREATE TABLE enrolment_challenge (
    nonce      bytea       PRIMARY KEY CHECK (octet_length(nonce) = 32),
    human_id   uuid        NOT NULL REFERENCES human (id),
    device_id  uuid        NOT NULL,
    expires_at timestamptz NOT NULL,
    spent_at   timestamptz
);
CREATE INDEX enrolment_challenge_expiry ON enrolment_challenge (expires_at);

CREATE TABLE enrolment_code (
    code_hash  bytea       PRIMARY KEY CHECK (octet_length(code_hash) = 32),
    human_id   uuid        NOT NULL REFERENCES human (id),
    expires_at timestamptz NOT NULL,
    spent_at   timestamptz
);
CREATE INDEX enrolment_code_expiry ON enrolment_code (expires_at);

CREATE TABLE biometric_credential (
    credential_id    bytea       PRIMARY KEY CHECK (octet_length(credential_id) BETWEEN 1 AND 1024),
    human_id         uuid        NOT NULL REFERENCES human (id),
    device_id        uuid        NOT NULL REFERENCES device (id),
    public_key       bytea       NOT NULL CHECK (octet_length(public_key) > 0),
    sign_count       bigint      NOT NULL DEFAULT 0 CHECK (sign_count BETWEEN 0 AND 4294967295),
    attested_factors text[]      NOT NULL DEFAULT '{}',
    created_at       timestamptz NOT NULL,
    revoked_at       timestamptz
);

-- A device holds one live credential at a time (doc 32 section 5b).
CREATE UNIQUE INDEX biometric_credential_live_device ON biometric_credential (device_id) WHERE revoked_at IS NULL;
CREATE INDEX biometric_credential_human ON biometric_credential (human_id);
