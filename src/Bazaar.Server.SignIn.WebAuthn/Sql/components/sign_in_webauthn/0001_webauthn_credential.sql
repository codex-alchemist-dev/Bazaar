-- 0001 (component sign-in-webauthn): passkey credentials for the /signin page (doc 32 section 4, doc 51 section 5).
-- Only public material is stored: the credential id, the public key (SubjectPublicKeyInfo DER), the sign count, a label and the
-- principal and device ids. There is no private key, attestation statement or authenticator secret in this schema.
-- No foreign key to the human table: the principal id is an opaque reference, so this module does not depend on the remote schema.

CREATE TABLE webauthn_credential (
    id            uuid        PRIMARY KEY,
    principal_id  uuid        NOT NULL,
    device_id     uuid        NOT NULL UNIQUE,
    credential_id bytea       NOT NULL UNIQUE CHECK (octet_length(credential_id) BETWEEN 16 AND 1023),
    public_key    bytea       NOT NULL CHECK (octet_length(public_key) BETWEEN 1 AND 512),
    sign_count    bigint      NOT NULL DEFAULT 0 CHECK (sign_count BETWEEN 0 AND 4294967295),
    label         text        NOT NULL CHECK (length(label) BETWEEN 1 AND 80),
    created_at    timestamptz NOT NULL,
    last_used_at  timestamptz,
    revoked_at    timestamptz
);

CREATE INDEX webauthn_credential_principal ON webauthn_credential (principal_id);
