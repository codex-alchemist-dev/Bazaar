-- PR read model columns (doc 21, "Data and interfaces" and work package 6, PrService).
-- title and author_login are read-model fields; head_sha and base_ref become nullable because a PR can be first seen through
-- an event that does not carry them (a comment, for example). The row exists from that first event so the PR GC is created
-- once; the next pull_request event that applies fills them in. updated_at is the epoch for a row no pull_request event has
-- applied to yet, so the first real PR state is never rejected as older (GitHubEventOrdering).
ALTER TABLE github_pr ALTER COLUMN head_sha DROP NOT NULL;
ALTER TABLE github_pr ALTER COLUMN base_ref DROP NOT NULL;
ALTER TABLE github_pr ADD COLUMN IF NOT EXISTS title text CHECK (title IS NULL OR length(title) <= 1024);
ALTER TABLE github_pr ADD COLUMN IF NOT EXISTS author_login text CHECK (author_login IS NULL OR length(author_login) BETWEEN 1 AND 256);
