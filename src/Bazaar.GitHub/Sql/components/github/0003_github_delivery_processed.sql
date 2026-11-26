-- Webhook processor state on github_delivery (docs/design/21-github-integration.md, "Data and interfaces").
-- processed_at is null until the event sink accepted the delivery (it already exists from 0001; kept here so the intent is
-- explicit and the file is safe on any database). process_attempts and next_attempt_at drive the claim lease and the retry
-- backoff; process_error holds the reason a row that cannot be parsed (a poison row) was marked processed. Idempotent.
ALTER TABLE github_delivery ADD COLUMN IF NOT EXISTS processed_at timestamptz;
ALTER TABLE github_delivery ADD COLUMN IF NOT EXISTS process_attempts integer NOT NULL DEFAULT 0;
ALTER TABLE github_delivery ADD COLUMN IF NOT EXISTS next_attempt_at timestamptz;
ALTER TABLE github_delivery ADD COLUMN IF NOT EXISTS process_error text;

-- The processor scans only unprocessed rows, oldest first; processed rows (the vast majority) stay out of the index.
CREATE INDEX IF NOT EXISTS github_delivery_unprocessed_idx
    ON github_delivery (received_at, delivery_id)
    WHERE processed_at IS NULL;
