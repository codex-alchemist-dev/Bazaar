-- The install's one company profile and the owner's recorded setup choices (docs/design/45-onboarding.md steps 1 and 6). Component
-- migration 0001 in the company namespace (common/ is frozen). Both tables hold at most one row (id = 1): one company and one owner
-- per install. Neither holds a secret nor the owner's name: the owner is a human row (remote namespace).
CREATE TABLE company_profile (
    id         smallint    PRIMARY KEY CHECK (id = 1),
    name       text        NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    slug       text        NOT NULL CHECK (slug ~ '^[a-z0-9][a-z0-9-]{0,62}$'),
    locale     text        NOT NULL CHECK (length(locale) > 0),
    timezone   text        NOT NULL CHECK (length(timezone) > 0),
    created_at timestamptz NOT NULL DEFAULT now()
);

-- What the plan decided about the owner. recovery_bundle_ref is a secret://bazaar/ reference to the encrypted recovery bundle,
-- never the bundle, a code or a key. Kodo's owner.secondFactor check reads second_factor_required.
CREATE TABLE owner_profile (
    id                     smallint    PRIMARY KEY CHECK (id = 1),
    second_factor_required boolean     NOT NULL DEFAULT true,
    recovery_bundle_ref    text        CHECK (recovery_bundle_ref IS NULL OR recovery_bundle_ref LIKE 'secret://bazaar/%'),
    recovery_printed_codes boolean     NOT NULL DEFAULT false,
    created_at             timestamptz NOT NULL DEFAULT now()
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

-- The runtime role reads and creates the rows; setup rollback removes a row the same apply created. They are never updated.
GRANT SELECT, INSERT, DELETE ON company_profile, owner_profile TO bazaar_app;
