-- Board display names (doc 12, "Board names", PROPOSED). A board is a project_id; this table stores its name.
-- No foreign key from tasks to here, so tasks created before a name existed keep working: an unnamed board shows as
-- "Project <first 8 hex digits>" (BoardDirectoryRepository). Creating and renaming boards is a later Kanban change.

CREATE TABLE kanban_project (
    project_id uuid PRIMARY KEY,
    name       text NOT NULL CHECK (btrim(name) <> '')
);
