-- Exact token amounts (doc 48, DECIDED 11.3). Namespace agent_safety: common/ is frozen.
-- agent_token_spend.tokens becomes unconstrained numeric, so a spend is exact, may hold a fraction, and no sum can wrap.
-- The 0001 CHECK (tokens > 0) stays: ALTER COLUMN ... TYPE keeps the constraint and checks it again. Existing bigint rows
-- convert exactly. Plain PostgreSQL; no foreign keys.

ALTER TABLE agent_token_spend ALTER COLUMN tokens TYPE numeric;
