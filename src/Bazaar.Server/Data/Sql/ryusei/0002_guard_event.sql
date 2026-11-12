-- guard_event (doc 06, "Loop guards"): one row per loop-guard trip, append-only. gc_id is null for a trip that has no GC, such as
-- a RyuseiFuda trigger trip (doc 38, WP-R8); its trigger and fire ids go in counters. Owner: WP-R8 (the runtime owns the table
-- definition; the rows are written by the guards).

CREATE TABLE guard_event (
    id         uuid        PRIMARY KEY DEFAULT uuidv7(),
    gc_id      uuid        REFERENCES gc (gc_id),
    kind       text        NOT NULL CHECK (btrim(kind) <> ''),
    counters   jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TRIGGER guard_event_append_only BEFORE UPDATE OR DELETE ON guard_event
    FOR EACH ROW EXECUTE FUNCTION gc_append_only();
CREATE TRIGGER guard_event_no_truncate BEFORE TRUNCATE ON guard_event
    FOR EACH STATEMENT EXECUTE FUNCTION gc_append_only();
