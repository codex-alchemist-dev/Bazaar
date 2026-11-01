-- Company Calendar (docs/design/24 section 4, DECISIONS-LOG round 12 C45). One table of vacations, planning, releases,
-- freezes and milestones. Cancel is a status change, never a delete. Each change bumps `version` and writes a GC line,
-- so the GC log holds the history; this table keeps only the current row. The calendar writes no availability: doc 09's
-- availability machine reads rows with affects_availability.
-- The title is a personal field (C01, doc 04): stored as (title_key_id, title_ct, title_nonce), never as plaintext.
-- agent_id references agent (0010). project_id stays a plain uuid until the project table arrives, as in 0012.
-- source_gc_id references gc (0001).
CREATE TABLE calendar_event (
    id                   uuid        PRIMARY KEY,
    kind                 text        NOT NULL CHECK (kind IN ('vacation', 'planning', 'release', 'freeze', 'milestone')),
    title_key_id         uuid        NOT NULL,
    title_ct             bytea       NOT NULL CHECK (octet_length(title_ct) > 0),
    title_nonce          bytea       NULL,
    starts_at            timestamptz NOT NULL,
    ends_at              timestamptz NOT NULL,
    all_day              boolean     NOT NULL DEFAULT false,
    tz                   text        NOT NULL CHECK (length(tz) BETWEEN 1 AND 100),
    agent_id             uuid        NULL REFERENCES agent (agent_id) ON DELETE RESTRICT,
    project_id           uuid        NULL,
    affects_availability boolean     NOT NULL,
    status               text        NOT NULL CHECK (status IN ('proposed', 'confirmed', 'cancelled')),
    source_gc_id         uuid        NULL REFERENCES gc (gc_id),
    version              bigint      NOT NULL DEFAULT 1 CHECK (version >= 1),
    CONSTRAINT calendar_event_range CHECK (ends_at > starts_at),
    -- A vacation is always an availability block and always belongs to one agent.
    CONSTRAINT calendar_event_vacation_shape CHECK (kind <> 'vacation' OR (affects_availability AND agent_id IS NOT NULL))
);

CREATE INDEX calendar_event_range_idx ON calendar_event (starts_at, ends_at);
CREATE INDEX calendar_event_agent_idx ON calendar_event (agent_id, starts_at) WHERE agent_id IS NOT NULL;
