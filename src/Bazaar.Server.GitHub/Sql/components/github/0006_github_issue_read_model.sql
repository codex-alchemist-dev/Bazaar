-- Issue GC read model (doc 21, "Issue GCs"). One row per issue, keyed by node id, with the GC it is mirrored into.
-- Same shape as github_pr minus the head and base: the issue's number, title, state and author, and updated_at, which is the
-- epoch for a row no `issues` event has applied to yet (so a row first made by a comment never makes a later real issue
-- event "older", see GitHubEventOrdering). Idempotent.
CREATE TABLE IF NOT EXISTS github_issue (
    issue_node_id text        PRIMARY KEY CHECK (length(issue_node_id) BETWEEN 1 AND 256),
    repo_node_id  text        NOT NULL CHECK (length(repo_node_id) BETWEEN 1 AND 256),
    number        integer     NOT NULL CHECK (number >= 1),
    gc_id         uuid        NOT NULL UNIQUE,
    state         text        NOT NULL DEFAULT 'open' CHECK (length(state) BETWEEN 1 AND 32),
    title         text        CHECK (title IS NULL OR length(title) <= 1024),
    author_login  text        CHECK (author_login IS NULL OR length(author_login) BETWEEN 1 AND 256),
    updated_at    timestamptz NOT NULL
);
