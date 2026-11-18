-- Hive Mind grants and copy bookkeeping (docs/design/25-company-systems-knowledge.md, Hive row and WP7; docs/design/08 section 6).
-- Namespace "hive" (docs/design/BAZAAR-COMPONENTS.md section 5). No foreign keys: project ids are plain values.

-- The owner's grant matrix. A missing row means no copy. Changes are audited through the audit port; revoking deletes the row.
CREATE TABLE hive_grants (
    from_project text        NOT NULL CHECK (from_project <> ''),
    to_project   text        NOT NULL CHECK (to_project <> ''),
    kind         text        NOT NULL CHECK (kind IN ('procedure', 'failure', 'technical-fact')),
    granted_by   text        NOT NULL CHECK (granted_by <> ''),
    granted_at   timestamptz NOT NULL,
    PRIMARY KEY (from_project, to_project, kind),
    CHECK (from_project <> to_project)
);

-- Copies. While in_review the sanitised text is held here and is not in the Vault; on release it is written to path.
-- A copy is never deleted: revoking a grant moves it to deprecated (and the Vault front matter to status: deprecated).
CREATE TABLE hive_copy (
    copy_id       uuid        PRIMARY KEY,
    from_project  text        NOT NULL,
    to_project    text        NOT NULL,
    kind          text        NOT NULL CHECK (kind IN ('procedure', 'failure', 'technical-fact')),
    origin_memory text        NOT NULL,
    origin_kind   text        NOT NULL,
    trust         text        NOT NULL CHECK (trust IN ('T2', 'T3')),
    title         text        NOT NULL,
    body          text        NOT NULL,
    path          text        NOT NULL UNIQUE,
    state         text        NOT NULL CHECK (state IN ('in_review', 'active', 'deprecated')),
    held_at       timestamptz NOT NULL,
    review_until  timestamptz,
    released_at   timestamptz,
    deprecated_at timestamptz,
    UNIQUE (from_project, origin_memory, to_project)
);

CREATE INDEX hive_copy_due ON hive_copy (review_until) WHERE state = 'in_review' AND review_until IS NOT NULL;
CREATE INDEX hive_copy_grant ON hive_copy (from_project, to_project, kind) WHERE state <> 'deprecated';
