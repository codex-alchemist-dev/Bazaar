-- Offline sync store and merge state (docs/design/56-offline-sync-module.md sections 4, 5 and 8; step 2 of section 9).
-- Component migration 0001 in the offline_sync namespace (not common: common is frozen). No foreign keys to any other
-- component's tables; the host guarantees every link (gc ids, task ids and so on are opaque keys here).
-- A write's claim, its state change, its outcome record and its merge audit commit in one transaction (section 8, item 4).

-- The store: one row per (device, write id) with the stored outcome. Kept for the life of the device registration (section 8, item 1).
CREATE TABLE offline_write_seen (
    device_id       uuid        NOT NULL,
    write_id        uuid        NOT NULL,
    device_sequence bigint      NOT NULL CHECK (device_sequence >= 1),
    outcome_kind    smallint    NOT NULL CHECK (outcome_kind BETWEEN 1 AND 7),
    server_version  text,
    reason          text,
    recorded_at     timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (device_id, write_id)
);

-- The highest device sequence applied per device.
CREATE TABLE offline_device_seq (
    device_id       uuid        PRIMARY KEY,
    acked_sequence  bigint      NOT NULL CHECK (acked_sequence >= 0)
);

-- Claims of write ids by the merge rules' state (the TryClaimWriteAsync of every state interface).
CREATE TABLE offline_write_claim (
    write_id    uuid        PRIMARY KEY,
    claimed_at  timestamptz NOT NULL DEFAULT now()
);

-- One audit record per non-duplicate outcome (doc 49: device, write, base version, server version, outcome).
CREATE TABLE offline_merge_audit (
    id              bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    device_id       uuid        NOT NULL,
    write_id        uuid        NOT NULL,
    data_class      smallint    NOT NULL,
    target          text        NOT NULL,
    base_version    text,
    server_version  text,
    outcome_kind    smallint    NOT NULL CHECK (outcome_kind BETWEEN 1 AND 7),
    reason          text,
    recorded_at     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX offline_merge_audit_write_idx ON offline_merge_audit (device_id, write_id);

-- Review items opened by merge rules (conflicts and held writes). The held value lives in the write itself (section 8, item 5).
CREATE TABLE offline_review_item (
    id              bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    kind            text        NOT NULL CHECK (length(kind) BETWEEN 1 AND 128),
    target          text        NOT NULL,
    write_id        uuid        NOT NULL,
    base_version    text,
    server_version  text,
    reason          text        NOT NULL,
    status          text        NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'resolved')),
    created_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT offline_review_item_write_kind_key UNIQUE (write_id, kind)
);
CREATE INDEX offline_review_item_open_idx ON offline_review_item (created_at, id) WHERE status = 'open';

-- GC message append: a gapless per-GC sequence (counter row, rolled back with the transaction) and the log.
CREATE TABLE offline_gc_head (
    gc_id       text    PRIMARY KEY,
    last_seq    bigint  NOT NULL CHECK (last_seq >= 1)
);
CREATE TABLE offline_gc_log (
    gc_id       text    NOT NULL,
    seq         bigint  NOT NULL CHECK (seq >= 1),
    payload     bytea   NOT NULL,
    PRIMARY KEY (gc_id, seq)
);

-- GC message edits: every version is kept; the highest seq of a message is current.
CREATE TABLE offline_gc_edit (
    seq             bigint  GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    message_id      text    NOT NULL,
    write_id        uuid    NOT NULL,
    base_version    text,
    payload         bytea   NOT NULL
);
CREATE INDEX offline_gc_edit_message_idx ON offline_gc_edit (message_id, seq DESC);

-- Kanban fields: compare-and-set on version; a never-set field has no row (version 0).
CREATE TABLE offline_kanban_field (
    task_id     text    NOT NULL,
    field       text    NOT NULL,
    value       text    NOT NULL,
    version     bigint  NOT NULL CHECK (version >= 1),
    PRIMARY KEY (task_id, field)
);

-- Kanban edges: a row per edge that was ever added or removed, with its version; present says whether it is in the graph.
CREATE TABLE offline_kanban_edge (
    graph_id    text    NOT NULL,
    from_task   text    NOT NULL,
    to_task     text    NOT NULL,
    present     boolean NOT NULL,
    version     bigint  NOT NULL CHECK (version >= 1),
    PRIMARY KEY (graph_id, from_task, to_task)
);
CREATE INDEX offline_kanban_edge_present_idx ON offline_kanban_edge (graph_id) WHERE present;

-- Last-writer-wins values (Constellation positions and preferences): the current value and every non-current value.
CREATE TABLE offline_lww_current (
    key         text    PRIMARY KEY,
    write_id    uuid    NOT NULL,
    hlc_wall    bigint  NOT NULL,
    hlc_counter integer NOT NULL,
    hlc_device  uuid    NOT NULL,
    version     text    NOT NULL,
    payload     bytea   NOT NULL
);
CREATE TABLE offline_lww_history (
    id          bigint  GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    key         text    NOT NULL,
    write_id    uuid    NOT NULL,
    hlc_wall    bigint  NOT NULL,
    hlc_counter integer NOT NULL,
    hlc_device  uuid    NOT NULL,
    version     text    NOT NULL,
    payload     bytea   NOT NULL
);
CREATE INDEX offline_lww_history_key_idx ON offline_lww_history (key, id);

-- Content-addressed attachments.
CREATE TABLE offline_attachment (
    content_hash    text    PRIMARY KEY CHECK (content_hash ~ '^[0-9a-f]{64}$'),
    payload         bytea   NOT NULL
);

-- Client audit actions in arrival order (the audit module hashes them into the chain). One head row gives a gapless position.
CREATE TABLE offline_audit_head (
    id          smallint PRIMARY KEY CHECK (id = 1),
    last_pos    bigint   NOT NULL CHECK (last_pos >= 0)
);
INSERT INTO offline_audit_head (id, last_pos) VALUES (1, 0);
CREATE TABLE offline_audit_action (
    position    bigint  PRIMARY KEY,
    write_id    uuid    NOT NULL UNIQUE,
    payload     bytea   NOT NULL
);
