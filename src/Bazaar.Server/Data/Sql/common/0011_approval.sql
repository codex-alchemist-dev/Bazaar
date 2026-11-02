-- 0011: approval table (doc 15 "Approval flow", doc 04 conventions). Owner: APR1.
--
-- Rows are the pending approvals and their grants. PostgresApprovalStore (Security/Approvals/Db) is the only writer.
-- Column choices against doc 15's shape:
--   args_hash    bytea, SHA-256 dispatch hash (32 bytes). The only thing a decision is bound to.
--   payload      jsonb. The store casts the raw args text to jsonb; a row read back is the canonical jsonb text.
--   taint_sources jsonb array of {sourceId, trust, raised}, shown on the card as quoted lineage. Default '[]'.
--   status       text with a CHECK, not an enum type, so a new status is one migration and no type change.
--   signature    bytea, the owner's 64-byte P-256 signature (raw r||s) over the decision message. Null until decided.
--   consumed_at  set when an approved call runs. Consumed grants cannot be used again.
--
-- Decision rows (approved, denied, consumed) must carry decided_at and signature. Consumed rows must also carry
-- consumed_at. The table has no DELETE grant for bazaar_app: expired and consumed rows stay for the audit trail.
-- The runtime role bazaar_app is created by 0004, which runs first; this file does not create it.
-- Updates are compare-and-set on status (PostgresApprovalStore.TryReplaceAsync), so UPDATE is granted.

CREATE TABLE approval (
    id             uuid        PRIMARY KEY,
    agent_id       text        NOT NULL,
    session_id     uuid        NOT NULL,
    tool           text        NOT NULL,
    target         text        NULL,
    args_hash      bytea       NOT NULL CHECK (octet_length(args_hash) = 32),
    payload        jsonb       NOT NULL,
    taint_sources  jsonb       NOT NULL DEFAULT '[]'::jsonb,
    status         text        NOT NULL CHECK (status IN ('pending', 'approved', 'denied', 'expired', 'consumed')),
    requested_at   timestamptz NOT NULL,
    expires_at     timestamptz NOT NULL,
    decided_by     text        NULL,
    device_id      text        NULL,
    decided_at     timestamptz NULL,
    signature      bytea       NULL CHECK (signature IS NULL OR octet_length(signature) = 64),
    consumed_at    timestamptz NULL,
    CONSTRAINT approval_decided_has_signature CHECK (
        status NOT IN ('approved', 'denied', 'consumed') OR (decided_at IS NOT NULL AND signature IS NOT NULL)),
    CONSTRAINT approval_consumed_has_time CHECK (status <> 'consumed' OR consumed_at IS NOT NULL)
);

-- The request cap (security.approval.request_cap_per_agent_hour) counts an agent's recent rows.
CREATE INDEX approval_agent_requested_at_idx ON approval (agent_id, requested_at);

REVOKE ALL ON approval FROM PUBLIC;
REVOKE ALL ON approval FROM bazaar_app;
GRANT INSERT, SELECT, UPDATE ON approval TO bazaar_app;
