-- Gateway usage retention (design doc 48 section 9, doc 05): retention soft-deletes before it purges, so gw_usage gains a tombstone.
-- A soft-deleted row is hidden from normal reads (UsageRepository filters soft_deleted_at IS NULL) and removed for good only by the
-- retention sweeper once the soft-delete window has passed. Migration namespace: gateway/ (not common/, which is frozen).
-- Numbering is local to this namespace. The runtime role bazaar_app is created by common/0001, which always runs first.
--
-- Grants: the gateway writes usage rows and reads them back, retention soft-deletes (UPDATE of the tombstone column only) and purges
-- (DELETE). No other column may be updated. Rows are never purged unless they carry a tombstone: the purge statement requires it.

ALTER TABLE gw_usage ADD COLUMN soft_deleted_at timestamptz NULL;

-- The purge step lists soft-deleted rows oldest tombstone first.
CREATE INDEX gw_usage_soft_deleted ON gw_usage (soft_deleted_at) WHERE soft_deleted_at IS NOT NULL;

GRANT SELECT, INSERT, DELETE ON gw_usage TO bazaar_app;
GRANT UPDATE (soft_deleted_at) ON gw_usage TO bazaar_app;
