-- Kodo displayed-state history and the latest status snapshot (doc 46 section 7.10).
-- state_change is derived from journal records of kind 'state' in the same transaction as the append, keyed by the journal
-- sequence, so it cannot disagree with the journal. It has no foreign key: retention prunes both tables by age on their own.
-- kodo_snapshot holds one row: the wire JSON of the newest snapshot, replaced only by a newer revision.

CREATE TABLE kodo_state_change (
    sequence   bigint   PRIMARY KEY CHECK (sequence >= 0),
    at_ticks   bigint   NOT NULL CHECK (at_ticks >= 0),
    from_state text     NOT NULL CHECK (char_length(from_state) BETWEEN 1 AND 32),
    to_state   text     NOT NULL CHECK (char_length(to_state) BETWEEN 1 AND 32)
);

CREATE INDEX kodo_state_change_at_ticks_idx ON kodo_state_change (at_ticks);

CREATE TABLE kodo_snapshot (
    id       smallint     PRIMARY KEY CHECK (id = 1),
    revision bigint       NOT NULL CHECK (revision >= 0),
    body     bytea        NOT NULL,
    saved_at timestamptz  NOT NULL
);
