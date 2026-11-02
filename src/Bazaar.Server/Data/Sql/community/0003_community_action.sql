-- 0003: community_action (doc 36, "Data and interfaces" and "Rate limits, audit and takedown"). Owner: WP3.
--
-- One row per outward community action. The table is append-only: a withdrawal adds a row with action
-- WithdrawOwn, and no row is ever changed or removed. CommunityActionStore (Actions/) is the only writer. Each
-- append also writes one audit_log entry (kind "community.action", migration common/0004).
--
-- The runtime role (bazaar_app, created by common/0004) gets INSERT and SELECT only. The sequence grant is needed
-- because the identity column draws its values from it.
--
-- Columns:
--   taint_sources  jsonb holding the taint sources the action was made under (doc 15). Default is an empty array.
--   body_sha256    SHA-256 (32 bytes) of the UTF-8 body. The audit entry carries this hash, never the body.
--   body           per-person content. TODO(encryption): encrypt under the per-person key (doc 04) before release.

CREATE TABLE community_action (
    id            bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    agent_id      text        NOT NULL,
    sponsor_id    text        NOT NULL,
    platform      text        NOT NULL,
    action        text        NOT NULL,
    target_url    text,
    body          text,       -- TODO(encryption): per-person encryptable field (doc 04).
    body_sha256   bytea       CHECK (body_sha256 IS NULL OR octet_length(body_sha256) = 32),
    approval_id   text,
    taint_sources jsonb       NOT NULL DEFAULT '[]',
    external_id   text,
    status        text        NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX community_action_agent_platform_created
    ON community_action (agent_id, platform, created_at);

REVOKE ALL ON community_action FROM PUBLIC;
GRANT INSERT, SELECT ON community_action TO bazaar_app;
GRANT USAGE ON SEQUENCE community_action_id_seq TO bazaar_app;
