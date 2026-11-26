-- Kill switch drill results (doc 15, doc 48 section 11). Namespace agent_safety: common/ is frozen.
-- One row per drill run. Runs are append-only: the runtime role may read and insert, never update or delete. Steps are jsonb
-- (step name, passed flag, fixed reason text); no secret values are stored. No foreign keys.

CREATE TABLE drill_result (
    id          uuid        PRIMARY KEY,
    started_at  timestamptz NOT NULL,
    duration_ms int         NOT NULL CHECK (duration_ms >= 0),
    passed      bool        NOT NULL,
    steps       jsonb       NOT NULL
);

-- The latest-run read orders by start time.
CREATE INDEX drill_result_started_at ON drill_result (started_at DESC);

GRANT SELECT, INSERT ON drill_result TO bazaar_app;
