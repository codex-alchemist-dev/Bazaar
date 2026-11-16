-- Token budget pruning (doc 15 quota table). The service prunes every agent's spends older than the widest window on each
-- call (DELETE ... WHERE at <= $1); the (agent_id, at) index from 0001 cannot serve that, so prune needs its own index
-- on at alone. Plain PostgreSQL; no foreign keys.

CREATE INDEX agent_token_spend_at_idx ON agent_token_spend (at);
