-- Crash reports, groups and the filing outbox (docs/design/43-telemetry-and-crash-reporting.md sections 8, 11 and 12.1).
-- Migration namespace: diagnostics/ (not common/, which is frozen). Numbering is local to this namespace.
--
-- Personal data: none outside crash_report.body_* and crash_attachment.data_*, which are AEAD ciphertext under the person's
-- data key or the company key (section 11.3). Destroying the key makes them unreadable. crash_group holds counts, versions,
-- aggregated device classes and OS families, and the issue facts, which carry no personal data (section 8.5).
-- No foreign key to the install or person tables: the install belongs to the identity component and the caller guarantees it.

CREATE TABLE crash_group (
    fingerprint        text        PRIMARY KEY CHECK (fingerprint ~ '^fp1:[0-9a-f]{32}$'),
    version            bigint      NOT NULL CHECK (version >= 1),
    status             text        NOT NULL CHECK (status IN ('Open', 'FilingDeferred', 'ResolvedIn')),
    occurrence_count   bigint      NOT NULL CHECK (occurrence_count >= 1),
    first_seen_version text        NOT NULL CHECK (btrim(first_seen_version) <> ''),
    last_seen_version  text        NOT NULL CHECK (btrim(last_seen_version) <> ''),
    first_seen_at      timestamptz NOT NULL,
    last_seen_at       timestamptz NOT NULL,
    device_classes     text[]      NOT NULL DEFAULT '{}',
    os_families        text[]      NOT NULL DEFAULT '{}',
    issue_number       integer     NULL CHECK (issue_number IS NULL OR issue_number > 0),
    issue_closed       boolean     NOT NULL DEFAULT false,
    fixed_in_version   text        NULL,
    last_comment_date  date        NULL,
    facts              jsonb       NOT NULL
);
CREATE INDEX crash_group_deferred_idx ON crash_group (first_seen_at, fingerprint) WHERE status = 'FilingDeferred';
CREATE INDEX crash_group_last_seen_idx ON crash_group (last_seen_at);

CREATE TABLE crash_report (
    report_id      uuid        PRIMARY KEY,
    install_id     uuid        NOT NULL,
    fingerprint    text        NOT NULL REFERENCES crash_group (fingerprint) ON DELETE RESTRICT,
    created_at     timestamptz NOT NULL,
    received_at    timestamptz NOT NULL,
    client_version text        NOT NULL,
    symbols        text        NOT NULL CHECK (symbols IN ('Resolved', 'Missing')),
    body_sha256    text        NOT NULL CHECK (body_sha256 ~ '^[0-9a-f]{64}$'),
    key_id         text        NOT NULL CHECK (btrim(key_id) <> ''),
    nonce          bytea       NOT NULL,
    ciphertext     bytea       NOT NULL,
    tag            bytea       NOT NULL
);
CREATE INDEX crash_report_group_idx ON crash_report (fingerprint, received_at);
CREATE INDEX crash_report_install_idx ON crash_report (install_id, received_at);
CREATE INDEX crash_report_received_idx ON crash_report (received_at);

CREATE TABLE crash_attachment (
    attachment_id uuid        PRIMARY KEY,
    report_id     uuid        NOT NULL REFERENCES crash_report (report_id) ON DELETE CASCADE,
    content_type  text        NOT NULL CHECK (content_type IN ('image/png', 'image/jpeg')),
    sha256        text        NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    key_id        text        NOT NULL CHECK (btrim(key_id) <> ''),
    nonce         bytea       NOT NULL,
    ciphertext    bytea       NOT NULL,
    tag           bytea       NOT NULL
);
CREATE INDEX crash_attachment_report_idx ON crash_attachment (report_id);

-- One row per FileIssue decision, kept after the outbox row is gone, so the daily budget can be counted (section 8.4).
CREATE TABLE crash_issue_filing (
    id          uuid        PRIMARY KEY,
    fingerprint text        NOT NULL REFERENCES crash_group (fingerprint) ON DELETE CASCADE,
    utc_day     date        NOT NULL,
    decided_at  timestamptz NOT NULL
);
CREATE INDEX crash_issue_filing_day_idx ON crash_issue_filing (utc_day);

CREATE TABLE crash_action_outbox (
    id          uuid        PRIMARY KEY,
    fingerprint text        NOT NULL REFERENCES crash_group (fingerprint) ON DELETE CASCADE,
    kind        text        NOT NULL CHECK (kind IN ('file_issue', 'add_comment', 'reopen')),
    payload     jsonb       NOT NULL,
    created_at  timestamptz NOT NULL,
    attempts    integer     NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    not_before  timestamptz NOT NULL,
    dead        boolean     NOT NULL DEFAULT false,
    last_error  text        NULL
);
CREATE INDEX crash_action_outbox_due_idx ON crash_action_outbox (not_before, created_at) WHERE NOT dead;
CREATE INDEX crash_action_outbox_group_idx ON crash_action_outbox (fingerprint, kind) WHERE NOT dead;

-- Runtime role: the intake path inserts and updates; retention deletes; nothing here is append-only because reports expire.
GRANT SELECT, INSERT, UPDATE, DELETE ON crash_group TO bazaar_app;
GRANT SELECT, INSERT, DELETE ON crash_report TO bazaar_app;
GRANT SELECT, INSERT, DELETE ON crash_attachment TO bazaar_app;
GRANT SELECT, INSERT, DELETE ON crash_issue_filing TO bazaar_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON crash_action_outbox TO bazaar_app;
