-- Link from a task to the import job that created it (docs/design/44-data-portability.md section 8.8, PROPOSED).
-- Builds on 0012. Null for every task not created by an import. No foreign key: import_job is in the portability namespace,
-- which must apply on its own, so the link is a plain text id as with the other cross-namespace references.

ALTER TABLE tasks ADD COLUMN import_job_id text CHECK (import_job_id IS NULL OR btrim(import_job_id) <> '');

-- Withdrawal reads the tasks of one job.
CREATE INDEX tasks_import_job_idx ON tasks (import_job_id) WHERE import_job_id IS NOT NULL;
