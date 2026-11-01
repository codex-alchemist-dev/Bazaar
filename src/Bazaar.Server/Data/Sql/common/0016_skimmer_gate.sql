-- Skimmer Bouncer gate tables (docs/design/13-skimmer-bouncer.md, work package 1). Owner: SKM1.
-- Owns gate_config, wake_decisions and gate_labels. Conventions (doc 04): UUIDv7 ids, timestamptz, text enums with
-- CHECK, RESTRICT foreign keys, JSONB only for opaque payloads.
--
-- Not here, on purpose:
--   * Mode, thresholds, scored-wake cap and the other gate tunables are settings (doc 13 "MOVED TO SETTINGS"). They live
--     in the `setting` table (0003) with scope 'agent'. No column holds them.
--   * gate_run_override is not created: doc 13 question 7 is open (it is an alternative to the brainstorm wake reason).
--   * No GRANTs and no append-only triggers: doc 13 does not state them. Decide with the role model (doc 04) first.
--
-- Scored wakes stay off: no column or default here enables them. subscriptions defaults to an empty array, which
-- scores nothing.

CREATE TABLE gate_config (
    agent_id      uuid  PRIMARY KEY REFERENCES agent (agent_id) ON DELETE RESTRICT,
    -- Scored-path subscriptions (a GC, tag, path or Kanban card). Opaque to the database; read by the scorer.
    subscriptions jsonb NOT NULL DEFAULT '[]'::jsonb
);

-- One row per (message, agent) decision. The unique key is the idempotency key from doc 13 ("Idempotency"): a duplicate
-- NOTIFY writes nothing new. prompt_version is NOT NULL so the rules stage must write a version too (a NULL would not
-- collide in a UNIQUE key, so duplicates would slip through).
CREATE TABLE wake_decisions (
    id             uuid        PRIMARY KEY DEFAULT uuidv7(),
    gc_id          uuid        NOT NULL,
    seq            bigint      NOT NULL CHECK (seq >= 1),
    agent_id       uuid        NOT NULL REFERENCES agent (agent_id) ON DELETE RESTRICT,
    path           text        NOT NULL CHECK (path IN ('mention', 'dm', 'critical', 'quarantine', 'scored', 'default')),
    decision       text        NOT NULL CHECK (decision IN ('wake', 'defer')),
    shadow         boolean     NOT NULL,
    -- Basis points, 0 to 10000. NULL on the rules paths, which carry no score.
    score_bp       integer     CHECK (score_bp BETWEEN 0 AND 10000),
    category       text        CHECK (category IN ('question', 'review', 'blocked', 'decision', 'chat', 'other')),
    -- NULL when no model ran (rules paths).
    model_id       text,
    prompt_version text        NOT NULL CHECK (length(prompt_version) BETWEEN 1 AND 64),
    created_at     timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (gc_id, seq) REFERENCES gc_event (gc_id, seq) ON DELETE RESTRICT,
    CONSTRAINT wake_decisions_idempotency_key UNIQUE (gc_id, seq, agent_id, prompt_version)
);

-- Owner and agent labels on a recorded decision (doc 13, "Measurement" and "Feedback"). Label values are not closed
-- by doc 13 yet; the label column is free text until the labelling vocabulary is decided.
-- Reasons are not stored here: doc 13 says flags keep reasons, but names no column for them.
CREATE TABLE gate_labels (
    id          uuid        PRIMARY KEY DEFAULT uuidv7(),
    decision_id uuid        NOT NULL REFERENCES wake_decisions (id) ON DELETE RESTRICT,
    label       text        NOT NULL CHECK (btrim(label) <> ''),
    source      text        NOT NULL CHECK (source IN ('agent_flag', 'owner_review', 'outcome')),
    labeller    uuid        NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX gate_labels_decision_idx ON gate_labels (decision_id);
