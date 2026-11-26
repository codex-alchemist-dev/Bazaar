-- Durable owner-read access log (docs/design/constellation-memory-graph.md, "Owner-read access log").
-- Namespace constellation; embedded in Bazaar.Server.Constellation (Sql/components/).
--
-- One row per read of memory by a principal other than the subject agent. Ids only: never titles, bodies, paths or query text.
-- Append only: the runtime role has INSERT and SELECT and no UPDATE or DELETE grant, and triggers refuse UPDATE, DELETE and
-- TRUNCATE for every role, so a row is never edited. audit_seq is the seq of the memory.owner_read row appended to the audit hash
-- chain (audit_log, doc 48 section 5.2) in the same transaction; the chain is the tamper evidence, this table the queryable index.
-- No foreign key to audit_log: the log namespace stays independent of this one.

CREATE TABLE constellation_memory_read_log (
    read_id          text        PRIMARY KEY CHECK (char_length(read_id) = 26),
    at               timestamptz NOT NULL,
    reader_principal text        NOT NULL CHECK (reader_principal <> ''),
    reader_session   text,
    route            text        NOT NULL CHECK (route <> ''),
    subject_agent_id text,
    operation        text        NOT NULL CHECK (operation IN ('retrieve', 'get', 'list_revisions', 'export')),
    note_ids         text[]      NOT NULL DEFAULT '{}',
    doc_ids          text[]      NOT NULL DEFAULT '{}',
    ids_truncated    boolean     NOT NULL DEFAULT false,
    result_count     integer     NOT NULL CHECK (result_count >= 0),
    taint_tags       text[]      NOT NULL DEFAULT '{}',
    purpose          text,
    audit_seq        bigint      NOT NULL CHECK (audit_seq >= 1)
);

CREATE INDEX constellation_memory_read_log_at_idx
    ON constellation_memory_read_log (at DESC, read_id DESC);

CREATE INDEX constellation_memory_read_log_subject_idx
    ON constellation_memory_read_log (subject_agent_id, at DESC, read_id DESC);

CREATE FUNCTION constellation_memory_read_log_append_only() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'constellation_memory_read_log is append only (% refused)', TG_OP
        USING ERRCODE = 'insufficient_privilege';
END;
$$;

CREATE TRIGGER constellation_memory_read_log_no_change
    BEFORE UPDATE OR DELETE ON constellation_memory_read_log
    FOR EACH ROW EXECUTE FUNCTION constellation_memory_read_log_append_only();

CREATE TRIGGER constellation_memory_read_log_no_truncate
    BEFORE TRUNCATE ON constellation_memory_read_log
    FOR EACH STATEMENT EXECUTE FUNCTION constellation_memory_read_log_append_only();

GRANT SELECT, INSERT ON constellation_memory_read_log TO bazaar_app;
