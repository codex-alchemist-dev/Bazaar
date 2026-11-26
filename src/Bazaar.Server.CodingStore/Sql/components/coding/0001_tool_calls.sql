-- Coding harness tool_calls (doc 17, data; WP2). Owner: Bazaar.Server.CodingStore. Namespace: coding.
--
-- Append-only and host-written. Arguments are stored as a SHA-256 hash only, never as text. The one allowed change to a row
-- is its outcome: status, exit_code and ended_at may be set once, while the row still has status 'unknown' (an allowed call is
-- appended before it runs, so its outcome is unknown until the host records it). Everything else is immutable, and rows are
-- never deleted or truncated.

CREATE TABLE tool_calls (
    id               uuid        PRIMARY KEY,
    session_id       text        NOT NULL,
    tool             text        NOT NULL,
    capability_class text        NOT NULL,
    source_trust     text        NOT NULL,
    tainted_at_write boolean     NOT NULL,
    status           text        NOT NULL CHECK (status IN ('succeeded', 'failed', 'timed_out', 'unknown', 'refused')),
    exit_code        integer     NULL,
    args_sha256      text        NOT NULL,
    started_at       timestamptz NOT NULL,
    ended_at         timestamptz NULL
);
CREATE INDEX tool_calls_session_idx ON tool_calls (session_id, started_at);

CREATE FUNCTION coding_tool_calls_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN
        RAISE EXCEPTION 'tool_calls is append-only (% rejected)', TG_OP USING ERRCODE = 'restrict_violation';
    END IF;
    IF NEW.id IS DISTINCT FROM OLD.id
       OR NEW.session_id IS DISTINCT FROM OLD.session_id
       OR NEW.tool IS DISTINCT FROM OLD.tool
       OR NEW.capability_class IS DISTINCT FROM OLD.capability_class
       OR NEW.source_trust IS DISTINCT FROM OLD.source_trust
       OR NEW.tainted_at_write IS DISTINCT FROM OLD.tainted_at_write
       OR NEW.args_sha256 IS DISTINCT FROM OLD.args_sha256
       OR NEW.started_at IS DISTINCT FROM OLD.started_at THEN
        RAISE EXCEPTION 'tool_calls is append-only (only the outcome may be set)' USING ERRCODE = 'restrict_violation';
    END IF;
    IF OLD.status <> 'unknown' THEN
        RAISE EXCEPTION 'tool_calls outcome is already set' USING ERRCODE = 'restrict_violation';
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER tool_calls_guard BEFORE UPDATE OR DELETE ON tool_calls
    FOR EACH ROW EXECUTE FUNCTION coding_tool_calls_guard();
CREATE TRIGGER tool_calls_no_truncate BEFORE TRUNCATE ON tool_calls
    FOR EACH STATEMENT EXECUTE FUNCTION coding_tool_calls_guard();
