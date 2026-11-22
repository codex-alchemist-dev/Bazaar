-- Exact token counts (doc 48, DECIDED 11.3). Namespace gateway: common/ is frozen.
-- The six gw_usage token-count columns become unconstrained numeric, so a count is exact, may hold a fraction, and no sum can
-- wrap. The 0002_gateway CHECKs (each column >= 0, and thinking <= output) stay: ALTER COLUMN ... TYPE keeps them and checks
-- them again. Existing bigint rows convert exactly. latency_ms and retry_after_s are not token counts and keep their types.
-- Plain PostgreSQL; no foreign keys, and no index covers these columns.

ALTER TABLE gw_usage
    ALTER COLUMN input TYPE numeric,
    ALTER COLUMN cache_write_5m TYPE numeric,
    ALTER COLUMN cache_write_1h TYPE numeric,
    ALTER COLUMN cache_read TYPE numeric,
    ALTER COLUMN output TYPE numeric,
    ALTER COLUMN thinking TYPE numeric;
