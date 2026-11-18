-- Retention sweeper (design doc 48 section 9): the runtime role purges gw_usage rows older than gateway.usage.retention_days.
-- The purge is DELETE ... WHERE id IN (SELECT id ... ORDER BY ts LIMIT n), so it needs SELECT and DELETE and nothing else.
-- Migration namespace: retention/ (not common/, which is frozen). Numbering is local to this namespace.
-- The runtime role bazaar_app is created by common/0001, which always runs first.
GRANT SELECT, DELETE ON gw_usage TO bazaar_app;
