-- Claude Code CLI provider (docs/design/91-claude-code-cli-provider.md, section 14 item 6b).
-- Widens three CHECK constraints created by common/0002_gateway.sql, which is frozen: the adapter name gains 'claude_code_cli',
-- and the identity label gains 'subscription'. No data changes. Constraint names are the PostgreSQL defaults for column checks.

ALTER TABLE gw_provider DROP CONSTRAINT IF EXISTS gw_provider_adapter_check;
ALTER TABLE gw_provider ADD CONSTRAINT gw_provider_adapter_check
    CHECK (adapter IN ('openai_chat', 'anthropic_messages', 'gemini_generate_content', 'claude_code_cli'));

ALTER TABLE gw_model DROP CONSTRAINT IF EXISTS gw_model_identity_check;
ALTER TABLE gw_model ADD CONSTRAINT gw_model_identity_check
    CHECK (identity IN ('native', 'pooled', 'subscription'));

ALTER TABLE gw_usage DROP CONSTRAINT IF EXISTS gw_usage_identity_check;
ALTER TABLE gw_usage ADD CONSTRAINT gw_usage_identity_check
    CHECK (identity IN ('native', 'pooled', 'subscription'));
