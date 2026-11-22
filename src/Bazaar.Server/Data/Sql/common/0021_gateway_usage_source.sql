-- Gateway usage: source label and denied rows (docs/design/05-model-gateway.md, Policy and Claude lane).
-- A call refused by policy writes a zero-token row with status 'denied'. It may name a provider that is not in gw_provider
-- (or the sentinel 'policy'), so the provider foreign key is dropped: usage history must outlive provider edits anyway.
ALTER TABLE gw_usage DROP CONSTRAINT IF EXISTS gw_usage_provider_id_fkey;
ALTER TABLE gw_usage DROP CONSTRAINT IF EXISTS gw_usage_status_check;
ALTER TABLE gw_usage ADD CONSTRAINT gw_usage_status_check CHECK (status IN ('success', 'error', 'aborted', 'denied'));
ALTER TABLE gw_usage ADD COLUMN IF NOT EXISTS source text NOT NULL DEFAULT '';
UPDATE gw_usage SET source = CASE provider_id
    WHEN 'anthropic-direct' THEN 'anthropic_key'
    ELSE provider_id END
WHERE source = '';
