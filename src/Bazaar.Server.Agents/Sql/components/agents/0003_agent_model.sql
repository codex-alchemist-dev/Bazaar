-- Agent model badge for the profile card (docs/design/24-company-systems-core.md, "Org directory fields"): the short label of the
-- model the agent runs on, for example 'Haiku 5.5'. Nullable and empty by default; the Agents module owns it. A label for display only:
-- routing and the model whitelist stay in agent_model_policy (doc 05).
ALTER TABLE agent ADD COLUMN IF NOT EXISTS model text NULL;
