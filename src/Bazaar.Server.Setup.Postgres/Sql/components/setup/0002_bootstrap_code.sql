-- The one-time bootstrap code of the provisioning API (docs/design/45-onboarding.md section 1.9). Component migration 0002 in the
-- setup namespace. At most one row (id = 1): creating a code replaces any earlier one. Only the SHA-256 digest of the code is
-- stored, never the code, so a database read cannot admit a call. consumed_at is set on first use, so a code works once.
CREATE TABLE setup_bootstrap_code (
    id           smallint    PRIMARY KEY CHECK (id = 1),
    digest       bytea       NOT NULL CHECK (octet_length(digest) = 32),
    created_at   timestamptz NOT NULL DEFAULT now(),
    consumed_at  timestamptz
);

-- The runtime role creates (replaces), consumes and revokes the code.
GRANT SELECT, INSERT, UPDATE, DELETE ON setup_bootstrap_code TO bazaar_app;
