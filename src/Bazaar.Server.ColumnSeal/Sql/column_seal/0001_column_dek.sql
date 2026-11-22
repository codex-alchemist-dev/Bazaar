-- Column data-encryption keys (docs/design/63-encrypted-data-folder.md section 9). Shaped like soul_dek (doc 14): each DEK is
-- 32 random bytes wrapped by the host KEK with associated data that binds the scope and the dek_id. Component migration 0001
-- in the column_seal namespace (common is frozen).

CREATE TABLE column_dek (
    dek_id      uuid        PRIMARY KEY,
    scope       text        NOT NULL CHECK (scope <> ''),
    wrapped_dek bytea       NOT NULL,
    kek_id      text        NOT NULL CHECK (kek_id <> ''),
    created_at  timestamptz NOT NULL DEFAULT now(),
    retired_at  timestamptz NULL
);

-- One live DEK per scope. Retiring the old row lets a new one be inserted; retired rows stay, because old values still open.
CREATE UNIQUE INDEX column_dek_one_live_idx ON column_dek (scope) WHERE retired_at IS NULL;

GRANT SELECT, INSERT ON column_dek TO bazaar_app;
GRANT UPDATE (retired_at) ON column_dek TO bazaar_app;
