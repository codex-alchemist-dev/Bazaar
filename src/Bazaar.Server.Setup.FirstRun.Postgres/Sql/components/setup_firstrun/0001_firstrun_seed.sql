-- First-run seed markers and the owner's Constitution acceptance (docs/design/45-onboarding.md section 1.15). Component migration
-- 0001 in the setup_firstrun namespace (common/ is frozen). It runs after the component namespaces, so gc, agent and human exist.

-- One row per seeded thing, so a second apply finds what the first made and changes nothing. detail holds the operator's free
-- text for the agent (its purpose, shown on its profile); it is never written to the audit log.
CREATE TABLE firstrun_seed (
    kind       text        PRIMARY KEY CHECK (kind IN ('general_gc', 'default_agent')),
    entity_id  uuid        NOT NULL,
    detail     jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now()
);

-- The owner's own act of accepting one Constitution commit (doc 24: no activation without an accepted commit). Never inferred.
CREATE TABLE constitution_acceptance (
    commit_sha  char(40)    NOT NULL CHECK (commit_sha ~ '^[0-9a-f]{40}$'),
    accepted_by uuid        NOT NULL,
    accepted_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (commit_sha, accepted_by)
);

-- Roles are cluster-wide, so create only if missing (as in common 0001).
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'bazaar_app') THEN
        CREATE ROLE bazaar_app NOLOGIN;
    END IF;
EXCEPTION WHEN duplicate_object OR unique_violation THEN
    NULL;
END
$$;

-- A marker is removed when its seed is rolled back; an acceptance is only ever added.
GRANT SELECT, INSERT, DELETE ON firstrun_seed TO bazaar_app;
GRANT SELECT, INSERT ON constitution_acceptance TO bazaar_app;
