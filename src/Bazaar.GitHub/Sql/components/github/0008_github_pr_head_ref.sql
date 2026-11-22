-- PROPOSED (doc 21, PR lookup): the head branch name of a PR whose head lives in the same repository, so a push to that branch
-- (refs/heads/<name>) finds its PR before the pull_request synchronize event has stored the new head_sha. Null for fork PRs (a fork's
-- branch name says nothing about the base repository's branches) and for rows no pull_request event has filled in yet.
ALTER TABLE github_pr ADD COLUMN IF NOT EXISTS head_ref text CHECK (head_ref IS NULL OR length(head_ref) BETWEEN 1 AND 1024);
CREATE INDEX IF NOT EXISTS github_pr_open_head_ref_idx ON github_pr (repo_node_id, head_ref) WHERE state = 'open' AND head_ref IS NOT NULL;
