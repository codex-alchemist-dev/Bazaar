-- Delivery bodies for downstream consumers (docs/design/21-github-integration.md, "Data and interfaces").
-- payload is the raw JSON body (null when the body is not JSON); event_type is the X-GitHub-Event value.
-- Both are nullable so rows written before this migration stay valid. Retention follows the audit rule: keep forever by
-- default, crypto-shred on erasure, no sweeper. Idempotent.
ALTER TABLE github_delivery ADD COLUMN IF NOT EXISTS payload jsonb;
ALTER TABLE github_delivery ADD COLUMN IF NOT EXISTS event_type text;
