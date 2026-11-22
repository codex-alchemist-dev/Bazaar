-- Lucky runs (docs/design/26-fun-systems.md, section 6). Component migration 0001 in the lucky namespace, not common (common is frozen).
-- The daily cap counts rows per owner per UTC day. Ideas are kept as JSON until the idea-recording path lands.
CREATE TABLE lucky_run (
    run_id        text        PRIMARY KEY CHECK (btrim(run_id) <> ''),
    project_id    uuid        NOT NULL,
    owner_id      uuid        NOT NULL,
    run_day       date        NOT NULL,
    prompt        text        NOT NULL CHECK (btrim(prompt) <> ''),
    started_at    timestamptz NOT NULL,
    ends_at       timestamptz NOT NULL,
    participants  jsonb       NOT NULL DEFAULT '[]'::jsonb,
    ideas         jsonb       NOT NULL DEFAULT '[]'::jsonb,
    gc_id         uuid,
    idea_messages jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_at    timestamptz NOT NULL DEFAULT now(),
    CHECK (ends_at > started_at)
);
CREATE INDEX lucky_run_owner_day_idx ON lucky_run (owner_id, run_day);
