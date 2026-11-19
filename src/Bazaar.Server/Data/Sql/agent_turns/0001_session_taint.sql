-- Session taint (doc 15, WP-TAINT). Namespace agent_turns (component agent-turns). Plain PostgreSQL; no cross-table foreign keys.
-- Append-only: each save writes a full snapshot of the session's taint (level plus every source so far). The session's
-- taint is its latest row (highest seq). Taint never decreases: a trigger rejects a level below the session's latest one.

CREATE TABLE session_taint (
    taint_row_id uuid        PRIMARY KEY DEFAULT uuidv7(),
    session_id   uuid        NOT NULL,
    seq          bigint      GENERATED ALWAYS AS IDENTITY UNIQUE,
    state        text        NOT NULL CHECK (state IN ('Clean', 'Soft', 'Hard')),
    sources      jsonb       NOT NULL DEFAULT '[]'::jsonb CHECK (jsonb_typeof(sources) = 'array'),
    recorded_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX session_taint_latest_idx ON session_taint (session_id, seq DESC);

-- Serialises writers per session, so two concurrent saves cannot both pass the no-decrease check.
CREATE FUNCTION session_taint_no_decrease() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    prior_state text;
BEGIN
    PERFORM pg_advisory_xact_lock(hashtextextended(NEW.session_id::text, 0));
    SELECT state INTO prior_state FROM session_taint WHERE session_id = NEW.session_id ORDER BY seq DESC LIMIT 1;
    IF prior_state IS NOT NULL AND
       (CASE NEW.state WHEN 'Clean' THEN 0 WHEN 'Soft' THEN 1 ELSE 2 END) <
       (CASE prior_state WHEN 'Clean' THEN 0 WHEN 'Soft' THEN 1 ELSE 2 END) THEN
        RAISE EXCEPTION 'session_taint: level for session % cannot decrease from % to %', NEW.session_id, prior_state, NEW.state
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER session_taint_no_decrease BEFORE INSERT ON session_taint
    FOR EACH ROW EXECUTE FUNCTION session_taint_no_decrease();
CREATE TRIGGER session_taint_append_only BEFORE UPDATE OR DELETE ON session_taint
    FOR EACH ROW EXECUTE FUNCTION gc_append_only();
CREATE TRIGGER session_taint_no_truncate BEFORE TRUNCATE ON session_taint
    FOR EACH STATEMENT EXECUTE FUNCTION gc_append_only();

GRANT INSERT, SELECT ON session_taint TO bazaar_app;
