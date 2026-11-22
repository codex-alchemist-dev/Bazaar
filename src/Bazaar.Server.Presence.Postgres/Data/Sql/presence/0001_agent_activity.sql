-- Presence rows (docs/design/23-observatory-and-usage.md, "Activity and presence" and the data model).
-- Namespace "presence" (docs/design/BAZAAR-COMPONENTS.md section 5). Owned by Bazaar.Server.Presence.Postgres.
-- One row per agent, replaced in place; the per-agent revision is monotonic and orders the deltas clients receive.
-- Enum columns are text with CHECK (doc 04). agent_id has no foreign key, so the namespace applies on its own, like the
-- portability namespace; the projector only writes rows for agents it was given events for.

CREATE TABLE agent_activity (
    agent_id      uuid         PRIMARY KEY,
    revision      bigint       NOT NULL CHECK (revision >= 0),
    availability  text         NOT NULL CHECK (availability IN ('on_shift', 'draining', 'break', 'off', 'vacation')),
    -- 'none', or a comma-separated list of overlays in a fixed order (see PresenceColumnCodec).
    overlay       text         NOT NULL CHECK (overlay ~ '^(none|(on_break|waiting_for_approval|throttled|covered|ward|idle)(,(on_break|waiting_for_approval|throttled|covered|ward|idle))*)$'),
    presence      text         NOT NULL CHECK (btrim(presence) <> ''),
    activity_kind text         NOT NULL CHECK (activity_kind IN ('idle', 'model_call', 'tool', 'waiting', 'drafting')),
    gc_id         uuid,
    turn_id       uuid,
    label         varchar(80),
    since         timestamptz  NOT NULL,
    updated_at    timestamptz  NOT NULL
);
