-- Agent role for the Org directory (docs/design/24-company-systems-core.md, "Org directory fields"): free text, for example
-- 'Engineer'. Nullable and empty by default; the Agents module owns it. The agent table itself is in common/0010_agent_runtime.sql.
ALTER TABLE agent ADD COLUMN IF NOT EXISTS role text NULL;
