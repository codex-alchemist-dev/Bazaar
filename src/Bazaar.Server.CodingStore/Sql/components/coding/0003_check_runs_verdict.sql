-- Coding harness check_runs: verdict and per-tool exit codes (doc 17, data; PROPOSED). Owner: Bazaar.Server.CodingStore. Namespace: coding.
--
-- Additive only. verdict is the server-computed overall result (pass, fail, error); null on rows written before this migration.
-- tool_exit_codes maps a short tool name (build, test, luau-analyze, selene, ...) to its exit code, or to null when the tool did not run.
-- The table stays insert-only: adding columns does not touch the guard trigger.

ALTER TABLE check_runs
    ADD COLUMN verdict text NULL CHECK (verdict IS NULL OR verdict IN ('pass', 'fail', 'error')),
    ADD COLUMN tool_exit_codes jsonb NULL CHECK (tool_exit_codes IS NULL OR jsonb_typeof(tool_exit_codes) = 'object');
