-- Durable approval replay for credential leases (doc 14, "Replay, restart, quarantine"). Namespace identity: common/ is frozen.
-- An approval id may back at most one lease. The unique partial index keeps that across restarts, and it still holds after a
-- lease expires or is revoked, because the row stays. The runtime role needs UPDATE to set revoked_at.
ALTER TABLE credential_lease ADD COLUMN approval_id uuid NULL;

CREATE UNIQUE INDEX credential_lease_approval_idx ON credential_lease (approval_id) WHERE approval_id IS NOT NULL;

GRANT SELECT, INSERT, UPDATE ON credential_lease TO bazaar_app;
