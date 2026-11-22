-- The install's one company profile (docs/design/45-onboarding.md steps 1 and 6). Component migration 0001 in the company namespace
-- (common/ is frozen). At most one row (id = 1): one company per install (doc 45 decided inputs). The row holds the name, slug,
-- locale and time zone the setup plan chose, and the owner's recorded second-factor choice. It holds no secret and no owner name:
-- the owner is a human row (remote namespace), the agents are agent rows (common 0010).
CREATE TABLE company_profile (
    id                           smallint    PRIMARY KEY CHECK (id = 1),
    name                         text        NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    slug                         text        NOT NULL CHECK (slug ~ '^[a-z0-9][a-z0-9-]{0,62}$'),
    locale                       text        NOT NULL CHECK (length(locale) > 0),
    timezone                     text        NOT NULL CHECK (length(timezone) > 0),
    owner_second_factor_required boolean     NOT NULL DEFAULT true,
    created_at                   timestamptz NOT NULL DEFAULT now()
);

-- The runtime role reads and creates the row; setup rollback removes a row the same apply created. It is never updated.
GRANT SELECT, INSERT, DELETE ON company_profile TO bazaar_app;
