-- Runtime grants for the model gateway tables created in common/0002_gateway.sql (docs/design/05-model-gateway.md).
-- Namespace "gateway" (docs/design/BAZAAR-COMPONENTS.md section 5). common/ is frozen, so the grants live here, in the module.
-- bazaar_app is created by a guarded CREATE ROLE in common/0001, and common always applies first, so the role exists here.
--
-- gw_usage is append-only: UsageRepository only inserts and reads it. The other tables take upserts
-- (INSERT ... ON CONFLICT DO UPDATE), so they need UPDATE as well. agent_model_policy is created in common/0002 and written by
-- AgentModelPolicyRepository the same way. No gateway statement deletes or truncates, so DELETE and TRUNCATE are not granted.
-- gw_usage's id is set by the caller (uuidv7 default), so it has no sequence to grant USAGE on.

GRANT SELECT, INSERT ON gw_usage TO bazaar_app;
GRANT SELECT, INSERT, UPDATE ON gw_provider, gw_model, gw_route, gw_quota_state, agent_model_policy TO bazaar_app;
