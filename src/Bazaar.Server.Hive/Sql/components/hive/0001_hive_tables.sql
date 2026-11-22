-- Hive Mind tables (docs/design/25-company-systems-knowledge.md, Hive Mind row and WP7; docs/design/08-memory-system.md section 6).
-- Component migration 0001 in the hive namespace (not common, which is frozen). Numbering is local to this namespace.
-- Text enums with CHECK, as in the vms namespace. Project values are slugs owned by other components, so no foreign keys.
-- kind holds the HiveKinds wire name, the same string as the hive/<kind>/ folder.

-- Owner grant matrix. A row means lessons of kind from from_project may be copied for to_project. No rows, no transfer.
CREATE TABLE hive_grant (
    seq          bigint      GENERATED ALWAYS AS IDENTITY,
    from_project text        NOT NULL CHECK (btrim(from_project) <> ''),
    to_project   text        NOT NULL CHECK (btrim(to_project) <> ''),
    kind         text        NOT NULL CHECK (kind IN ('procedure', 'failure', 'technical-fact')),
    granted_by   text        NOT NULL CHECK (btrim(granted_by) <> ''),
    granted_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (from_project, to_project, kind)
);

-- Sanitized copies held for the review window (hive.grant_review_days). release_at is null when only an owner approval releases one.
CREATE TABLE hive_held_copy (
    seq            bigint      GENERATED ALWAYS AS IDENTITY,
    id             uuid        PRIMARY KEY,
    origin_project text        NOT NULL CHECK (btrim(origin_project) <> ''),
    origin_note    text        NOT NULL CHECK (btrim(origin_note) <> ''),
    kind           text        NOT NULL CHECK (kind IN ('procedure', 'failure', 'technical-fact')),
    target_project text        NOT NULL CHECK (btrim(target_project) <> ''),
    slug           text        NOT NULL CHECK (btrim(slug) <> ''),
    title          text        NOT NULL,
    body           text        NOT NULL,
    origin_tier    text        NOT NULL,
    submitted_at   timestamptz NOT NULL,
    release_at     timestamptz NULL
);

-- Record of copies that were written to the Vault. Revoking a grant sets revoked; rows and files are never deleted.
CREATE SEQUENCE hive_copy_seq;
CREATE TABLE hive_copy (
    seq            bigint      NOT NULL DEFAULT nextval('hive_copy_seq'),
    path           text        NOT NULL CHECK (btrim(path) <> ''),
    origin_project text        NOT NULL CHECK (btrim(origin_project) <> ''),
    origin_note    text        NOT NULL CHECK (btrim(origin_note) <> ''),
    kind           text        NOT NULL CHECK (kind IN ('procedure', 'failure', 'technical-fact')),
    target_project text        NOT NULL CHECK (btrim(target_project) <> ''),
    revoked        boolean     NOT NULL DEFAULT false,
    PRIMARY KEY (path, target_project, origin_project, origin_note)
);
CREATE INDEX hive_copy_grant_idx ON hive_copy (origin_project, target_project, kind) WHERE NOT revoked;
