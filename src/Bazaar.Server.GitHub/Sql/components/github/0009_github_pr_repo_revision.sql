-- PROPOSED (doc 21, "Client PR wire" read model): what pr.list and pr.summary need beyond the PR service's columns.
-- repo_full_name is owner/name, written by the PR service from the event, so the read model can name the repo for the adapter
-- (the table is keyed by node ids and holds no repo name). revision rises by one on every change to a row, so a client can tell a
-- changed row from an unchanged one; a write that changes nothing does not raise it. Idempotent.
ALTER TABLE github_pr ADD COLUMN IF NOT EXISTS repo_full_name text
    CHECK (repo_full_name IS NULL OR repo_full_name ~ '^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$');
ALTER TABLE github_pr ADD COLUMN IF NOT EXISTS revision bigint NOT NULL DEFAULT 1;

CREATE OR REPLACE FUNCTION github_pr_bump_revision() RETURNS trigger AS $$
BEGIN
    NEW.revision := OLD.revision + 1;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS github_pr_revision ON github_pr;
CREATE TRIGGER github_pr_revision BEFORE UPDATE ON github_pr
    FOR EACH ROW WHEN (OLD.* IS DISTINCT FROM NEW.*) EXECUTE FUNCTION github_pr_bump_revision();

-- pr.list pages newest first, within one state.
CREATE INDEX IF NOT EXISTS github_pr_list_idx ON github_pr (state, updated_at DESC, pr_node_id);
