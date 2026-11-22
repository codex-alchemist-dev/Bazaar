-- Stored `withdrawn` task state (doc 12, "Withdrawn state", PROPOSED; doc 44 section 8.8). Owner: KB-WD1. Builds on 0012.
-- Terminal state set only by the task service's import-withdrawal port. Blocked is still derived and never stored.
-- Number 0002 is left for the import-job link on `tasks` (PR #1326); numbers are local to the namespace and may merge in any order.

ALTER TABLE tasks DROP CONSTRAINT tasks_state_check;
ALTER TABLE tasks ADD CONSTRAINT tasks_state_check
    CHECK (state IN ('backlog', 'ready', 'in_progress', 'review', 'done', 'withdrawn'));
