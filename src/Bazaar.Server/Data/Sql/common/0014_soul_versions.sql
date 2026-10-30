-- 0014: Soul versions (doc 07 "Versioning", WP2). Owner: SOUL1.
--
-- One chain per Soul. Each row is a full snapshot: the charter is a Vault file (doc 07 Q7), so the row keeps its SHA-256,
-- and the preferences are versioned JSON held here. A Soul's soulVersion, which messages and judgments record, is the
-- version number. Rows are appended, never updated: runtime role bazaar_app gets INSERT and SELECT only.
--
-- Chain rules, enforced here and by SoulVersioningService (which locks the Soul before it reads the head):
--   version 1 has no parent; version n > 1 has parent n - 1.
--   (soul_id, version) is unique, so a stale writer that races past the lock still cannot fork the chain.
-- content_sha256 hashes the charter hash and the canonical preferences JSON; the service computes it.

CREATE TABLE soul_versions (
    soul_id        uuid        NOT NULL,
    version        integer     NOT NULL CHECK (version >= 1),
    parent_version integer     CHECK (parent_version >= 1),
    charter_sha256 text        NOT NULL CHECK (charter_sha256 ~ '^[0-9a-f]{64}$'),
    preferences    text        NOT NULL CHECK (preferences::jsonb IS NOT NULL), -- canonical JSON text, kept byte-for-byte (jsonb would reorder keys)
    content_sha256 text        NOT NULL CHECK (content_sha256 ~ '^[0-9a-f]{64}$'),
    author         text        NOT NULL CHECK (length(btrim(author)) > 0),
    reason         text        NOT NULL CHECK (length(btrim(reason)) > 0),
    decision_ref   text        CHECK (decision_ref IS NULL OR length(btrim(decision_ref)) > 0),
    created_at     timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (soul_id, version),
    CHECK ((version = 1 AND parent_version IS NULL) OR parent_version = version - 1)
);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'bazaar_app') THEN
        CREATE ROLE bazaar_app NOLOGIN;
    END IF;
EXCEPTION WHEN duplicate_object OR unique_violation THEN
    NULL; -- a concurrent run created it first
END
$$;

REVOKE ALL ON soul_versions FROM PUBLIC;
REVOKE ALL ON soul_versions FROM bazaar_app;
GRANT INSERT, SELECT ON soul_versions TO bazaar_app;
