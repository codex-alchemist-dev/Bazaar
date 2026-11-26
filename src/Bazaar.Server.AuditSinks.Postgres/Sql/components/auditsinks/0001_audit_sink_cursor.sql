-- Per-sink delivery cursor of the audit secondary sinks (docs/design/48-gap-decisions-security-identity.md section 5.4).
-- Component migration 0001 in the auditsinks namespace. One row per sink name; a sink added later has no row and starts at
-- zero. The cursor only moves forward (the store saves with GREATEST), so a late or repeated save cannot re-deliver history.
CREATE TABLE audit_sink_cursor (
    sink_name            text        PRIMARY KEY CHECK (sink_name <> ''),
    last_row_seq         bigint      NOT NULL DEFAULT 0 CHECK (last_row_seq >= 0),
    last_checkpoint_seq  bigint      NOT NULL DEFAULT 0 CHECK (last_checkpoint_seq >= 0),
    updated_at           timestamptz NOT NULL DEFAULT now()
);

-- The runtime role reads and advances cursors. It never deletes one.
GRANT SELECT, INSERT, UPDATE ON audit_sink_cursor TO bazaar_app;
