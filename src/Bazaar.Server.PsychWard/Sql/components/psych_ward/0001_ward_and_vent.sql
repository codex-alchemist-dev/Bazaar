-- Psych Ward and vent tables (docs/design/26-fun-systems.md, "Data and interfaces").
-- Component migration 0001 in the psych_ward namespace (not common, which is frozen). Agent ids are uuids owned by the agents
-- component, so no foreign keys. Text enums use CHECK, as in the hive namespace.
-- Vent rows hold only the sanitized fields: the raw draft is never stored here. Per-person encryption of vent text (decision C01)
-- is applied by the cipher component when it lands; until then these columns are the sanitized text the doc allows to leave a
-- private scope.

CREATE TABLE ward_stay (
    id            uuid        PRIMARY KEY,
    agent_id      uuid        NOT NULL,
    trigger       text        NOT NULL CHECK (trigger IN
                      ('loop_guard', 'repeated_failure', 'denied_call_or_canary', 'token_hard_stop', 'self_referral', 'owner_referral')),
    detail        text        NULL,
    admitted_at   timestamptz NOT NULL,
    check_in      jsonb       NULL,
    discharged_at timestamptz NULL,
    reviewer_id   uuid        NULL
);

-- One active stay per agent, enforced by the database so two racing triggers cannot open two.
CREATE UNIQUE INDEX ward_stay_one_active_per_agent ON ward_stay (agent_id) WHERE discharged_at IS NULL;
CREATE INDEX ward_stay_agent_admitted ON ward_stay (agent_id, admitted_at DESC);

CREATE TABLE vent (
    failure_ref      text        PRIMARY KEY CHECK (btrim(failure_ref) <> ''),
    agent_id         uuid        NOT NULL,
    what_happened    text        NOT NULL,
    what_would_change text       NOT NULL,
    unsure           text        NOT NULL,
    status           text        NOT NULL CHECK (status IN ('draft', 'held', 'published')),
    created_at       timestamptz NOT NULL,
    publish_target   text        NULL
);
CREATE INDEX vent_status_created ON vent (status, created_at);

-- Failures that still need a vent (vent.required_before_card). A row goes away once its vent exists.
CREATE TABLE vent_open_failure (
    failure_ref text        PRIMARY KEY CHECK (btrim(failure_ref) <> ''),
    agent_id    uuid        NOT NULL,
    noted_at    timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX vent_open_failure_agent ON vent_open_failure (agent_id);
