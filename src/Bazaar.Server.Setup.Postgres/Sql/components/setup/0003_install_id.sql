-- The install's stable id (docs/design/45-onboarding.md section 1.2). Component migration 0003 in the setup namespace. At most one
-- row (id = 1): one install per database. The id is random, not derived from the host, the company or any person, and is never updated.
CREATE TABLE setup_install_id (
    id         smallint    PRIMARY KEY CHECK (id = 1),
    install_id uuid        NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

-- The runtime role reads the id and creates it once. It is never updated or deleted.
GRANT SELECT, INSERT ON setup_install_id TO bazaar_app;
