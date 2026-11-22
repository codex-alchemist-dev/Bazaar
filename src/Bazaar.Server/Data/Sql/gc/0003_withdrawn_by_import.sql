-- 0003 (gc namespace): a message version can be a withdrawal by an import (doc 01 Edits, doc 44 8.8, PROPOSED).
-- The column holds the import job id on the new version that withdraws an import's message; null on every other version.
-- gc_message_version is append-only, so a withdrawal is a new version row and earlier versions are never touched.
-- Existing rows stay null.

ALTER TABLE gc_message_version
    ADD COLUMN withdrawn_by_import text CHECK (withdrawn_by_import IS NULL OR (length(withdrawn_by_import) BETWEEN 1 AND 200));
