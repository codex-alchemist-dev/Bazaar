-- Kanban gate decisions (doc 12, WP9) and the two event kinds the gate and PR-merge hooks write (WP12). Owner: KB-WP9.
-- Builds on 0012. Decisions are append-only: a later decision supersedes an earlier one, and the task service reads the latest.

CREATE TABLE task_gate_decisions (
    decision_id uuid        PRIMARY KEY DEFAULT uuidv7(),
    task_id     uuid        NOT NULL REFERENCES tasks (id),
    gate        text        NOT NULL CHECK (gate IN ('council', 'owner')),
    outcome     text        NOT NULL CHECK (outcome IN ('approved', 'rejected')),
    decided_by  uuid        NOT NULL,
    -- A council decision id (doc 10) or an owner approval id (doc 15). Empty references are refused.
    reference   text        NOT NULL CHECK (btrim(reference) <> ''),
    decided_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX task_gate_decisions_task_idx ON task_gate_decisions (task_id, decided_at DESC, decision_id DESC);

CREATE TRIGGER task_gate_decisions_append_only BEFORE UPDATE OR DELETE ON task_gate_decisions
    FOR EACH ROW EXECUTE FUNCTION gc_append_only();
CREATE TRIGGER task_gate_decisions_no_truncate BEFORE TRUNCATE ON task_gate_decisions
    FOR EACH STATEMENT EXECUTE FUNCTION gc_append_only();

ALTER TABLE task_events DROP CONSTRAINT task_events_kind_check;
ALTER TABLE task_events ADD CONSTRAINT task_events_kind_check CHECK (kind IN (
    'task.created', 'task.transitioned', 'task.blocked', 'task.unblocked', 'task.edge_changed',
    'task.guard_refused', 'task.gate_decided', 'task.due_passed', 'task.gate_recorded', 'task.review_approved'));

GRANT SELECT, INSERT ON task_gate_decisions TO bazaar_app;
