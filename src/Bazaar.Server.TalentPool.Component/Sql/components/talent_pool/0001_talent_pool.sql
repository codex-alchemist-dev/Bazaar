-- Talent Pool tables (docs/design/25-company-systems-knowledge.md, Talent Pool and WP8). Owner: talent-pool component.
-- Component migration 0001 in the talent_pool namespace (not common, which is frozen).
-- Manifest only: prompt, tool allowlist, model whitelist. No memory column exists by design.

CREATE TABLE talent_template (
    template_id  text        NOT NULL CHECK (btrim(template_id) <> ''),
    version      integer     NOT NULL CHECK (version >= 1),
    manifest     jsonb       NOT NULL CHECK (jsonb_typeof(manifest) = 'object'),
    content_sha  text        NOT NULL CHECK (content_sha ~ '^[0-9a-f]{64}$'),
    submitted_by text        NOT NULL CHECK (btrim(submitted_by) <> ''),
    submitted_at timestamptz NOT NULL,
    PRIMARY KEY (template_id, version)
);

-- One row per template version: the state of its latest Council hire vote (IHireDecisionStore). The key is template_id@version.
CREATE TABLE talent_hire_state (
    hire_key   text        PRIMARY KEY CHECK (btrim(hire_key) <> ''),
    vote_id    uuid        NOT NULL,
    status     text        NOT NULL CHECK (status IN ('pending', 'released', 'blocked')),
    decision   text        NULL,
    updated_at timestamptz NOT NULL
);

-- Owner Tier 2 approval of a template version's tool grants. Keyed by the exact tool set, so a changed list needs a new approval.
-- A hire vote never writes here.
CREATE TABLE talent_tool_grant_approval (
    hire_key    text        NOT NULL CHECK (btrim(hire_key) <> ''),
    tools_sha   text        NOT NULL CHECK (tools_sha ~ '^[0-9a-f]{64}$'),
    approved_by text        NOT NULL CHECK (btrim(approved_by) <> ''),
    approved_at timestamptz NOT NULL,
    PRIMARY KEY (hire_key, tools_sha)
);
