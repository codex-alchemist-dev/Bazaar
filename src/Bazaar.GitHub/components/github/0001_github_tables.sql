-- GitHub component tables (docs/design/21-github-integration.md, "Data and interfaces"; work package 1).
-- Applies only through the GitHub component's migration source (components/github, embedded in Bazaar.Server.GitHub).
-- Never place this file in Data/Sql/common. Owner of the github_* tables: doc 21.
-- No cross-table foreign keys: agent_id, gc_id and task_id are plain uuids owned by other components' tables.
-- Enums are text with CHECK (doc 04). Timestamps are timestamptz in UTC. Secrets are never stored here: key_ref is a
-- reference held by the Identity Service (doc 14).

-- One row per agent that has a GitHub App installation. status is active, suspended or revoked (doc 21).
CREATE TABLE github_app (
    agent_id        uuid        PRIMARY KEY,
    app_id          bigint      NOT NULL,
    installation_id bigint      NOT NULL,
    account_login   text        NOT NULL CHECK (length(account_login) BETWEEN 1 AND 256),
    status          text        NOT NULL CHECK (status IN ('active', 'suspended', 'revoked')),
    -- Reference only, never the key. The key itself lives in the Identity Service (doc 14).
    key_ref         text        NOT NULL CHECK (length(key_ref) BETWEEN 1 AND 256)
);

-- One row per pull request. The PR GC is created once: gc_id is unique, so a replayed delivery cannot create a second
-- GC (doc 21, "Behaviour and edge cases"). A force-push changes head_sha; the state values are set by the PR service.
CREATE TABLE github_pr (
    pr_node_id   text        PRIMARY KEY CHECK (length(pr_node_id) BETWEEN 1 AND 256),
    repo_node_id text        NOT NULL CHECK (length(repo_node_id) BETWEEN 1 AND 256),
    number       integer     NOT NULL CHECK (number >= 1),
    gc_id        uuid        NOT NULL UNIQUE,
    task_id      uuid,
    head_sha     char(40)    NOT NULL CHECK (head_sha ~ '^[0-9a-f]{40}$'),
    base_ref     text        NOT NULL CHECK (length(base_ref) BETWEEN 1 AND 256),
    state        text        NOT NULL CHECK (length(state) BETWEEN 1 AND 32),
    draft        boolean     NOT NULL,
    updated_at   timestamptz NOT NULL
);

-- Webhook and poll deduplication (doc 21, "Webhook ingress"). The key is (source, delivery_id), so a delivery seen
-- twice (webhook plus poll) is applied once. received_at is set on insert; processed_at is set after processing.
CREATE TABLE github_delivery (
    source       text        NOT NULL CHECK (length(source) BETWEEN 1 AND 64),
    delivery_id  text        NOT NULL CHECK (length(delivery_id) BETWEEN 1 AND 256),
    event        text        NOT NULL CHECK (length(event) BETWEEN 1 AND 128),
    received_at  timestamptz NOT NULL,
    processed_at timestamptz,
    PRIMARY KEY (source, delivery_id)
);

-- Last known rate-limit state per installation and bucket, from GitHub response headers (doc 21, "Interfaces").
-- The doc names no key; (installation_id, bucket) is the natural key and is the upsert target.
CREATE TABLE github_rate_state (
    installation_id bigint      NOT NULL,
    bucket          text        NOT NULL CHECK (length(bucket) BETWEEN 1 AND 64),
    remaining       integer     NOT NULL CHECK (remaining >= 0),
    reset_at        timestamptz NOT NULL,
    PRIMARY KEY (installation_id, bucket)
);
