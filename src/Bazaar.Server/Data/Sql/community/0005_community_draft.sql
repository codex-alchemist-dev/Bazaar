-- 0005: community_draft (doc 36, "Data and interfaces" and work package 8). Owner: WP8.
--
-- One row per outbound community draft awaiting, or past, the sponsor's signed decision (doc 15 approval flow).
-- CommunityDraftStore (Drafts/) is the only writer. Every status change is a compare-and-set:
--   UPDATE ... WHERE draft_id = @id AND status = @expected
-- so two concurrent decisions cannot both win. An edit moves the original to Edited and inserts the replacement in one
-- transaction. Rows are never deleted; the runtime role (bazaar_app) gets SELECT, INSERT and UPDATE only.
--
-- Columns:
--   post columns       the exact outbound post. approval_hash covers all of them (DraftApprovalHash.Compute).
--   body               per-person content. TODO(encryption): per-person encryptable field (doc 04), not yet wired.
--   gate_input         jsonb of the draft as the quality gates read it; its body is the agent text WITHOUT the footer.
--   profile            jsonb of the bot profile used to append the footer to an edited body (holds the sponsor's contact:
--                      TODO(encryption) as for bot_profile.contact).
--   gate_failures      jsonb array of {gate, reason}; any entry blocks approval.
--   taint_sources      jsonb array of taint sources for the action log.
--   superseded_by      the replacement draft when status is Edited.

CREATE TABLE community_draft (
    draft_id            uuid        PRIMARY KEY,
    agent_id            text        NOT NULL,
    sponsor_id          text        NOT NULL,
    action              text        NOT NULL,
    platform            text        NOT NULL,
    kind                text        NOT NULL,
    target              text        NOT NULL,
    title               text,
    body                text        NOT NULL, -- TODO(encryption): per-person encryptable field (doc 04).
    body_sha256         text        NOT NULL CHECK (body_sha256 ~ '^[0-9a-f]{64}$'),
    head_branch         text,
    base_branch         text,
    gate_input          jsonb       NOT NULL,
    profile             jsonb       NOT NULL,
    approval_id         uuid        NOT NULL,
    approval_hash       bytea       NOT NULL CHECK (octet_length(approval_hash) = 32),
    approval_expires_at timestamptz NOT NULL,
    gate_failures       jsonb       NOT NULL DEFAULT '[]' CHECK (jsonb_typeof(gate_failures) = 'array'),
    status              text        NOT NULL CHECK (status IN ('Pending', 'Posting', 'Posted', 'Denied', 'Edited', 'Failed', 'Expired')),
    taint_sources       jsonb       NOT NULL DEFAULT '[]',
    external_id         text,
    url                 text,
    reused              boolean     NOT NULL DEFAULT false,
    superseded_by       uuid,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now()
);

-- The review list reads the open drafts of one sponsor's queue, newest first.
CREATE INDEX community_draft_status_created ON community_draft (status, created_at DESC);

REVOKE ALL ON community_draft FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON community_draft TO bazaar_app;
