-- 0013: reputation ledger (doc 11, WP1). Owner: REP1.
--
-- Assumptions:
--   agent_id and reviewer/author ids are plain text. No foreign key to an agent table, which does not exist yet
--   (doc 06). card_id is a plain uuid. No foreign key to a kanban table, which does not exist yet.
--   outcome_event is one row per evidence item. Corrections are new rows (supersedes_id on review, a new
--   outcome_event with its own dedupe_key), never edits.
--   Every table is append-only. Roles follow 0004: bazaar_app gets INSERT and SELECT only.

CREATE TABLE review (
    id                uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    pr_ref            text        NOT NULL,
    card_id           uuid,
    reviewer_agent_id text        NOT NULL,
    author_agent_id   text        NOT NULL,
    reviewer_family   text,
    author_family     text,
    blinded           boolean     NOT NULL,
    tag               text        NOT NULL,
    overall           smallint    NOT NULL CHECK (overall BETWEEN 1 AND 5),
    rubric            jsonb,
    rationale_ref     text,
    supersedes_id     uuid        REFERENCES review (id),
    created_at        timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE outcome_event (
    id           uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    kind         text        NOT NULL CHECK (kind IN ('ci_first_run', 'revert', 'post_merge_defect',
                                                      'owner_accept', 'owner_reject', 'manual')),
    pr_ref       text        NOT NULL,
    x            numeric     NOT NULL CHECK (x BETWEEN 0 AND 1),
    w            numeric     NOT NULL,
    dedupe_key   text        NOT NULL UNIQUE,
    source_ref   text,
    occurred_at  timestamptz NOT NULL,
    recorded_at  timestamptz NOT NULL DEFAULT now()
);

-- Who an outcome is attributed to, per skill tag. Looked up by agent when scores are computed.
CREATE TABLE outcome_attribution (
    outcome_id  uuid    NOT NULL REFERENCES outcome_event (id),
    agent_id    text    NOT NULL,
    role        text    NOT NULL,
    tag         text    NOT NULL,
    w           numeric NOT NULL,
    PRIMARY KEY (outcome_id, agent_id, tag)
);

CREATE INDEX outcome_attribution_agent_tag_idx ON outcome_attribution (agent_id, tag);

CREATE TABLE reviewer_calibration (
    reviewer_agent_id text        NOT NULL,
    tag               text        NOT NULL,
    r                 numeric     NOT NULL,
    n                 int         NOT NULL CHECK (n >= 0),
    weights_version   int         NOT NULL,
    computed_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (reviewer_agent_id, tag, weights_version)
);

-- Scoring values in force. Every change to the reputation settings writes a new version (doc 11, Settings).
CREATE TABLE weights_version (
    version  int         PRIMARY KEY CHECK (version >= 1),
    body     jsonb       NOT NULL,
    set_by   text        NOT NULL,
    reason   text        NOT NULL,
    set_at   timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE score_snapshot (
    agent_id         text        NOT NULL,
    tag              text        NOT NULL,
    s                numeric     NOT NULL,
    lcb              numeric     NOT NULL,
    n_eff            numeric     NOT NULL,
    weights_version  int         NOT NULL REFERENCES weights_version (version),
    computed_at      timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (agent_id, tag, computed_at)
);

CREATE TABLE reputation_event (
    id        uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    agent_id  text        NOT NULL,
    tag       text        NOT NULL,
    kind      text        NOT NULL,
    reason    text,
    actor     text        NOT NULL,
    at        timestamptz NOT NULL DEFAULT now()
);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'bazaar_app') THEN
        CREATE ROLE bazaar_app NOLOGIN;
    END IF;
EXCEPTION WHEN duplicate_object OR unique_violation THEN
    NULL; -- a concurrent run created it first
END
$$;

REVOKE ALL ON review, outcome_event, outcome_attribution, reviewer_calibration, weights_version, score_snapshot,
              reputation_event FROM PUBLIC;
REVOKE ALL ON review, outcome_event, outcome_attribution, reviewer_calibration, weights_version, score_snapshot,
              reputation_event FROM bazaar_app;
GRANT INSERT, SELECT ON review, outcome_event, outcome_attribution, reviewer_calibration, weights_version,
                        score_snapshot, reputation_event TO bazaar_app;

-- Same shape as 0004: privileges refuse UPDATE, DELETE and TRUNCATE for bazaar_app (SQLSTATE 42501), and these
-- triggers also refuse the owner and superusers. Only a deliberate DISABLE TRIGGER lets a change through.
CREATE FUNCTION reputation_refuse_change() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'reputation ledger is append-only: % refused on %', TG_OP, TG_TABLE_NAME
        USING ERRCODE = 'insufficient_privilege';
END
$$;

CREATE TRIGGER review_no_update_or_delete BEFORE UPDATE OR DELETE ON review
    FOR EACH ROW EXECUTE FUNCTION reputation_refuse_change();
CREATE TRIGGER review_no_truncate BEFORE TRUNCATE ON review
    FOR EACH STATEMENT EXECUTE FUNCTION reputation_refuse_change();

CREATE TRIGGER outcome_event_no_update_or_delete BEFORE UPDATE OR DELETE ON outcome_event
    FOR EACH ROW EXECUTE FUNCTION reputation_refuse_change();
CREATE TRIGGER outcome_event_no_truncate BEFORE TRUNCATE ON outcome_event
    FOR EACH STATEMENT EXECUTE FUNCTION reputation_refuse_change();

CREATE TRIGGER outcome_attribution_no_update_or_delete BEFORE UPDATE OR DELETE ON outcome_attribution
    FOR EACH ROW EXECUTE FUNCTION reputation_refuse_change();
CREATE TRIGGER outcome_attribution_no_truncate BEFORE TRUNCATE ON outcome_attribution
    FOR EACH STATEMENT EXECUTE FUNCTION reputation_refuse_change();

CREATE TRIGGER reviewer_calibration_no_update_or_delete BEFORE UPDATE OR DELETE ON reviewer_calibration
    FOR EACH ROW EXECUTE FUNCTION reputation_refuse_change();
CREATE TRIGGER reviewer_calibration_no_truncate BEFORE TRUNCATE ON reviewer_calibration
    FOR EACH STATEMENT EXECUTE FUNCTION reputation_refuse_change();

CREATE TRIGGER weights_version_no_update_or_delete BEFORE UPDATE OR DELETE ON weights_version
    FOR EACH ROW EXECUTE FUNCTION reputation_refuse_change();
CREATE TRIGGER weights_version_no_truncate BEFORE TRUNCATE ON weights_version
    FOR EACH STATEMENT EXECUTE FUNCTION reputation_refuse_change();

CREATE TRIGGER score_snapshot_no_update_or_delete BEFORE UPDATE OR DELETE ON score_snapshot
    FOR EACH ROW EXECUTE FUNCTION reputation_refuse_change();
CREATE TRIGGER score_snapshot_no_truncate BEFORE TRUNCATE ON score_snapshot
    FOR EACH STATEMENT EXECUTE FUNCTION reputation_refuse_change();

CREATE TRIGGER reputation_event_no_update_or_delete BEFORE UPDATE OR DELETE ON reputation_event
    FOR EACH ROW EXECUTE FUNCTION reputation_refuse_change();
CREATE TRIGGER reputation_event_no_truncate BEFORE TRUNCATE ON reputation_event
    FOR EACH STATEMENT EXECUTE FUNCTION reputation_refuse_change();
