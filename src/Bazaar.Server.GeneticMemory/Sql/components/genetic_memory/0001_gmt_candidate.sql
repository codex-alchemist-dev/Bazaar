-- Genetic Memory Transfer review queue (docs/design/25-company-systems-knowledge.md, work package 9).
-- Component migration 0001 in the genetic_memory namespace. Staged text lives here, never in the Vault, until a reviewer accepts it.
-- Rejected rows are kept for audit and never retrievable. Agent values are slugs owned by other components, so no foreign keys.
-- Enum values are the names of GmtContentClass, GmtLessonKind, GmtReviewer and GmtStatus.
CREATE TABLE gmt_candidate (
    seq               bigint      GENERATED ALWAYS AS IDENTITY,
    id                uuid        PRIMARY KEY,
    child_agent       text        NOT NULL CHECK (btrim(child_agent) <> ''),
    target_agent      text        NOT NULL CHECK (btrim(target_agent) <> ''),
    content_class     text        NOT NULL CHECK (content_class IN ('Lesson', 'ToolGrant', 'CharterText')),
    kind              text        NOT NULL CHECK (kind IN ('Procedure', 'Failure')),
    title             text        NOT NULL CHECK (btrim(title) <> ''),
    body              text        NOT NULL CHECK (btrim(body) <> ''),
    trace_ref         text        NULL,
    tainted           boolean     NOT NULL,
    required_reviewer text        NOT NULL CHECK (required_reviewer IN ('Architect', 'Owner')),
    status            text        NOT NULL CHECK (status IN ('Staged', 'Accepted', 'Rejected')),
    staged_at         timestamptz NOT NULL,
    accepted_path     text        NULL
);
CREATE INDEX gmt_candidate_staged_idx ON gmt_candidate (seq) WHERE status = 'Staged';
CREATE UNIQUE INDEX gmt_candidate_accepted_path_idx ON gmt_candidate (accepted_path) WHERE accepted_path IS NOT NULL;
