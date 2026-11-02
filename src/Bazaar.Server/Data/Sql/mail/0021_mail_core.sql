-- Mail core tables (docs/design/22-email-subsystem.md, owner WP EMAIL1, work package 1). Only external mail uses these
-- tables: internal coordination stays in GCs. No domain is hardcoded: mail_domain holds the operator's domain.
-- Personal fields (message body) are encrypted per person, following the CAL1 title pattern (key id, ciphertext, nonce).
-- Sender credentials are never stored here: config_ref points into the Identity Service secret store (doc 14).

CREATE TABLE mail_domain (
    id          uuid        PRIMARY KEY,
    domain      text        NOT NULL CHECK (domain = lower(domain) AND domain <> ''),
    backend_kind text       NOT NULL CHECK (backend_kind IN ('bazaar_client', 'imap_smtp', 'cloudflare', 'builtin_server')),
    config_ref  text        NULL,
    state       text        NOT NULL DEFAULT 'configured' CHECK (state IN ('configured', 'active', 'paused', 'retired')),
    created_at  timestamptz NOT NULL DEFAULT now(),
    version     bigint      NOT NULL DEFAULT 1 CHECK (version >= 1),
    CONSTRAINT mail_domain_unique UNIQUE (domain)
);

-- One address per agent. soul_id is NULL for owner addresses (doc 22 account model). No foreign keys to agent tables:
-- the cross-table migration (0005, reserved) adds them.
CREATE TABLE mail_mailbox (
    id          uuid        PRIMARY KEY,
    soul_id     uuid        NULL,
    domain_id   uuid        NOT NULL REFERENCES mail_domain (id),
    local_part  text        NOT NULL CHECK (local_part = lower(local_part) AND local_part <> ''),
    state       text        NOT NULL DEFAULT 'active' CHECK (state IN ('active', 'paused', 'closed')),
    created_at  timestamptz NOT NULL DEFAULT now(),
    version     bigint      NOT NULL DEFAULT 1 CHECK (version >= 1),
    CONSTRAINT mail_mailbox_address_unique UNIQUE (domain_id, local_part)
);

CREATE TABLE mail_message (
    id              uuid        PRIMARY KEY,
    mailbox_id      uuid        NOT NULL REFERENCES mail_mailbox (id),
    direction       text        NOT NULL CHECK (direction IN ('inbound', 'outbound')),
    rfc_message_id  text        NULL,
    backend_id      text        NULL,
    from_addr       text        NULL,
    auth_json       jsonb       NOT NULL DEFAULT '{}'::jsonb,
    screen_codes    text[]      NOT NULL DEFAULT '{}',
    state           text        NOT NULL CHECK (state IN ('received', 'held', 'released', 'delivered', 'purged', 'reported', 'queued', 'sent', 'failed')),
    gc_message_id   uuid        NULL,
    blob_ref        uuid        NULL,
    -- Body is encrypted under the owner's or the agent's person key (doc 14, erasure keys); never stored in clear.
    body_key_id     uuid        NULL,
    body_ct         bytea       NULL,
    body_nonce      bytea       NULL,
    received_at     timestamptz NULL,
    retention_until timestamptz NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT mail_message_body_triplet CHECK ((body_ct IS NULL) = (body_key_id IS NULL) AND (body_ct IS NULL) = (body_nonce IS NULL))
);

-- Duplicate delivery is normal: dedupe on the RFC message id within a mailbox and direction (doc 22 behaviour).
CREATE UNIQUE INDEX mail_message_rfc_id_idx ON mail_message (mailbox_id, direction, rfc_message_id)
    WHERE rfc_message_id IS NOT NULL;

CREATE INDEX mail_message_state_idx ON mail_message (state, created_at);

-- Outbound sends are idempotent on the key: a repeated key sends once (doc 22 testing, key cases).
CREATE TABLE mail_outbound (
    idempotency_key text        PRIMARY KEY CHECK (idempotency_key <> ''),
    message_id      uuid        NOT NULL REFERENCES mail_message (id),
    approval_id     uuid        NULL,
    status          text        NOT NULL CHECK (status IN ('pending', 'sending', 'sent', 'failed', 'bounced')),
    attempts        integer     NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    next_attempt_at timestamptz NULL,
    updated_at      timestamptz NOT NULL DEFAULT now()
);

-- Raw blobs (full MIME, attachments). Served only over the authenticated HTTP surface (doc 02).
CREATE TABLE mail_blob (
    id          uuid        PRIMARY KEY,
    sha256      bytea       NOT NULL CHECK (octet_length(sha256) = 32),
    size_bytes  bigint      NOT NULL CHECK (size_bytes >= 0),
    scan_state  text        NOT NULL DEFAULT 'pending' CHECK (scan_state IN ('pending', 'clean', 'held')),
    created_at  timestamptz NOT NULL DEFAULT now()
);
