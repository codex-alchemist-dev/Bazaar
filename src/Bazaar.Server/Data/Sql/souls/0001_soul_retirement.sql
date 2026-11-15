-- Durable Soul retirement step outcomes (docs/design/07-soul-system.md, WP6).
-- The latest outcome is replaced on retry; successful outcomes let the coordinator skip completed work.

CREATE TABLE soul_retirement_step (
    soul_id     uuid        NOT NULL,
    agent_id    uuid        NOT NULL,
    step        text        NOT NULL CHECK (step IN (
        'passport_revocation',
        'token_revocation',
        'vm_stop',
        'vm_child_disk_delete',
        'vm_parent_disk_delete')),
    status      text        NOT NULL CHECK (status IN ('done', 'already_done', 'failed', 'deferred', 'refused')),
    reason      text        NULL,
    updated_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (soul_id, agent_id, step)
);

GRANT SELECT, INSERT ON soul_retirement_step TO bazaar_app;
GRANT UPDATE (status, reason, updated_at) ON soul_retirement_step TO bazaar_app;
