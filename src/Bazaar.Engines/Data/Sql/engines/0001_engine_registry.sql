-- Engine registry (docs/design/20-engine-integration-api.md, Data and interfaces; WP2).
-- Namespace "engines" (docs/design/BAZAAR-COMPONENTS.md section 5). No foreign keys: agent and adapter ids are plain values.

CREATE TABLE engine_projects (
    id             uuid PRIMARY KEY,
    adapter_id     text NOT NULL,
    root_path      text NOT NULL,
    engine_version text NOT NULL,
    build_variant  text,
    gui_host_id    text
);

-- Per-agent tool grants. Default deny for write and exec: a missing row means denied (EngineGrantStore).
-- TODO(component): each grant is also an approval row in doc 15's approval table (one approval table, C16).
-- granted_by is free text until that table's schema is settled; do not invent an approval schema here.
CREATE TABLE engine_grants (
    agent_id   uuid NOT NULL,
    adapter_id text NOT NULL,
    tool_name  text NOT NULL,
    risk_class text NOT NULL CHECK (risk_class IN ('read', 'write', 'exec')),
    granted_by text NOT NULL,
    granted_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (agent_id, adapter_id, tool_name)
);
