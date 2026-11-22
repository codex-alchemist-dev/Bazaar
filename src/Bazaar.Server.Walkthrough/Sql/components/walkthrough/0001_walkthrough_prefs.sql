-- One person's first-run walkthrough progress (docs/design/45-onboarding.md section 2.7, PROPOSED). Component migration 0001 in the
-- walkthrough namespace. The human preference `onboarding.walkthrough` of doc 02 (`prefs.set`), stored per person so it follows the
-- account to every device. Step names are validated by the route before they reach this table; the column checks keep the shape sane.
CREATE TABLE walkthrough_prefs (
    person_id   text        PRIMARY KEY CHECK (length(person_id) BETWEEN 1 AND 200),
    dismissed   boolean     NOT NULL,
    finished    boolean     NOT NULL,
    current     text        NOT NULL CHECK (current IN ('Welcome', 'CreateCompany', 'MeetAgent', 'FirstTask', 'Done')),
    completed   text[]      NOT NULL CHECK (completed <@ ARRAY['Welcome', 'CreateCompany', 'MeetAgent', 'FirstTask', 'Done']),
    updated_at  timestamptz NOT NULL DEFAULT now()
);

-- The runtime role reads and replaces its own rows.
GRANT SELECT, INSERT, UPDATE ON walkthrough_prefs TO bazaar_app;
