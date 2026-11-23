-- Coding harness check_runs (doc 17, data; WP2). Owner: Bazaar.Server.CodingStore. Namespace: coding.
--
-- One row per server-owned verification run on a commit. Insert-only: a run is a record of what the server measured, so a
-- later change would be a falsified result. flags holds CheckFlag codes. Counts are null when the step did not run.

CREATE TABLE check_runs (
    id              uuid        PRIMARY KEY,
    commit_sha      text        NOT NULL CHECK (btrim(commit_sha) <> ''),
    base_commit     text        NULL,
    build_exit_code integer     NULL,
    tests_passed    integer     NULL CHECK (tests_passed IS NULL OR tests_passed >= 0),
    tests_failed    integer     NULL CHECK (tests_failed IS NULL OR tests_failed >= 0),
    tests_skipped   integer     NULL CHECK (tests_skipped IS NULL OR tests_skipped >= 0),
    test_count_base integer     NULL CHECK (test_count_base IS NULL OR test_count_base >= 0),
    flags           text[]      NOT NULL DEFAULT '{}',
    ran_at          timestamptz NOT NULL
);
CREATE INDEX check_runs_commit_idx ON check_runs (commit_sha, ran_at DESC);

CREATE FUNCTION coding_check_runs_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'check_runs is insert-only (% rejected)', TG_OP USING ERRCODE = 'restrict_violation';
END
$$;

CREATE TRIGGER check_runs_guard BEFORE UPDATE OR DELETE ON check_runs
    FOR EACH ROW EXECUTE FUNCTION coding_check_runs_guard();
CREATE TRIGGER check_runs_no_truncate BEFORE TRUNCATE ON check_runs
    FOR EACH STATEMENT EXECUTE FUNCTION coding_check_runs_guard();
