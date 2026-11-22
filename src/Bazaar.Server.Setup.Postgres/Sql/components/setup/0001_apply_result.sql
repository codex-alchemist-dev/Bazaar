-- Durable result of each setup apply, keyed by plan hash (docs/design/45-onboarding.md section 1.9: "A repeat call with the same hash
-- returns the stored result"). Component migration 0001 in the setup namespace (common/ is frozen). The first saved result for a
-- hash is the one kept, so replay is idempotent. The row holds the exit code, the applied step ids and the issue list. Issues name
-- keys and rules, never values (SetupValidationIssue), so no secret reaches this table.
CREATE TABLE setup_apply_result (
    plan_hash   text        PRIMARY KEY CHECK (length(plan_hash) BETWEEN 1 AND 128),
    exit_code   smallint    NOT NULL CHECK (exit_code BETWEEN 0 AND 3),
    result      jsonb       NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);

-- The runtime role only reads and inserts; a stored result is never changed or removed.
GRANT SELECT, INSERT ON setup_apply_result TO bazaar_app;
