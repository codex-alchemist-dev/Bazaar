-- Gateway tables (docs/design/05-model-gateway.md, work package 3). Owner: GW3.
-- No foreign keys to GC or agent tables; the cross-table keys arrive in 0005.
-- Conventions (doc 04): UUIDv7 ids, timestamptz, text enums with CHECK, RESTRICT foreign keys, JSONB for opaque payloads.

CREATE TABLE gw_provider (
    id            text PRIMARY KEY,
    kind          text NOT NULL CHECK (kind IN ('first_party', 'aggregator', 'gateway', 'reverse_proxy', 'local')),
    adapter       text NOT NULL CHECK (adapter IN ('openai_chat', 'anthropic_messages', 'gemini_generate_content')),
    base_url      text NOT NULL,
    -- Only these three credential kinds exist. Subscription and OAuth logins cannot be stored (doc 05, Claude lane).
    auth_kind     text NOT NULL CHECK (auth_kind IN ('api_key', 'bearer', 'none')),
    -- A reference into the Identity Service, never a key. Required for api_key and bearer, absent for none.
    secret_ref    text,
    headers       jsonb NOT NULL DEFAULT '{}'::jsonb,
    proxy_ref     text,
    terms_ack_at  timestamptz,
    enabled       boolean NOT NULL DEFAULT true,
    CONSTRAINT gw_provider_secret_ref_matches_auth CHECK (
        (auth_kind = 'none' AND secret_ref IS NULL)
        OR (auth_kind <> 'none' AND COALESCE(secret_ref LIKE 'secret://bazaar/providers/_%', false))
    )
);

CREATE TABLE gw_model (
    provider_id text NOT NULL REFERENCES gw_provider (id) ON DELETE RESTRICT,
    model_id    text NOT NULL,
    family      text NOT NULL,
    identity    text NOT NULL CHECK (identity IN ('native', 'pooled')),
    caps        jsonb NOT NULL DEFAULT '{}'::jsonb,
    status      text NOT NULL DEFAULT 'available' CHECK (status IN ('available', 'missing')),
    first_seen  timestamptz NOT NULL,
    last_seen   timestamptz NOT NULL,
    source      text NOT NULL,
    PRIMARY KEY (provider_id, model_id)
);

CREATE TABLE gw_route (
    id           text PRIMARY KEY,
    strategy     text NOT NULL CHECK (strategy IN ('priority', 'round_robin', 'least_loaded')),
    -- Array of "provider:model" strings, in priority order.
    members      jsonb NOT NULL CHECK (jsonb_typeof(members) = 'array'),
    -- For delegated (custom/*) routes: the models the upstream is expected to serve.
    expected_set jsonb NOT NULL DEFAULT '[]'::jsonb CHECK (jsonb_typeof(expected_set) = 'array')
);

CREATE TABLE agent_model_policy (
    agent_id      text PRIMARY KEY,
    -- Array of provider-qualified models and route names. Deny by default: nothing listed, nothing allowed.
    allowed       jsonb NOT NULL CHECK (jsonb_typeof(allowed) = 'array'),
    default_route text,
    version       bigint NOT NULL CHECK (version >= 1),
    changed_by    text NOT NULL,
    changed_at    timestamptz NOT NULL
);

CREATE TABLE gw_quota_state (
    provider_id text NOT NULL REFERENCES gw_provider (id) ON DELETE RESTRICT,
    key_ref     text NOT NULL,
    model_class text NOT NULL,
    state       text NOT NULL CHECK (state IN ('available', 'rate_limited', 'spend_capped', 'daily_exhausted', 'down')),
    until       timestamptz,
    observed    jsonb NOT NULL DEFAULT '{}'::jsonb,
    PRIMARY KEY (provider_id, key_ref, model_class)
);

-- One row per model call. "input" counts uncached input only; "thinking" is a subset of "output".
-- No prompt or completion text is stored. Unpartitioned in v1; retention is the setting gateway.usage.retention_days.
CREATE TABLE gw_usage (
    id               uuid PRIMARY KEY DEFAULT uuidv7(),
    ts               timestamptz NOT NULL,
    agent_id         text NOT NULL,
    turn_id          text,
    provider_id      text NOT NULL REFERENCES gw_provider (id) ON DELETE RESTRICT,
    key_ref          text NOT NULL,
    model_id         text NOT NULL,
    served_model     text,
    identity         text NOT NULL CHECK (identity IN ('native', 'pooled')),
    input            bigint NOT NULL CHECK (input >= 0),
    cache_write_5m   bigint NOT NULL CHECK (cache_write_5m >= 0),
    cache_write_1h   bigint NOT NULL CHECK (cache_write_1h >= 0),
    cache_read       bigint NOT NULL CHECK (cache_read >= 0),
    output           bigint NOT NULL CHECK (output >= 0),
    thinking         bigint NOT NULL CHECK (thinking >= 0),
    latency_ms       bigint NOT NULL CHECK (latency_ms >= 0),
    status           text NOT NULL CHECK (status IN ('success', 'error', 'aborted')),
    error_code       text,
    retry_after_s    integer CHECK (retry_after_s >= 0),
    estimated        boolean NOT NULL,
    price_version    text,
    CONSTRAINT gw_usage_thinking_within_output CHECK (thinking <= output)
);

CREATE INDEX gw_usage_agent_ts ON gw_usage (agent_id, ts);
CREATE INDEX gw_usage_ts ON gw_usage (ts);
