-- Outbox lease: a dispatcher claims due rows for a while, so two server instances do not file the same issue twice.
ALTER TABLE crash_action_outbox ADD COLUMN locked_until timestamptz NULL;

-- Auto-diagnosis backoff: a group whose diagnosis keeps failing waits longer each time, so it cannot starve the others.
CREATE TABLE crash_diagnosis_backoff (
    fingerprint     text        PRIMARY KEY REFERENCES crash_group (fingerprint) ON DELETE CASCADE,
    failures        integer     NOT NULL CHECK (failures > 0),
    next_attempt_at timestamptz NOT NULL
);
GRANT SELECT, INSERT, UPDATE, DELETE ON crash_diagnosis_backoff TO bazaar_app;
