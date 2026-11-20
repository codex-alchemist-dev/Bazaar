-- Shared approvals queue (docs/design/51-web-admin-module.md, approvals queue). Migration namespace: approval_queue.
-- No foreign keys: principal ids are opaque keys owned by the identity and roles modules.
--
-- Stored statuses are pending, approved, denied and consumed. "Expired" is never stored: a pending row past expires_at reads as
-- expired. payload_ref names the thing to approve; the queue never stores its contents.

CREATE TABLE approval_queue_item (
    id                  uuid        PRIMARY KEY,
    kind                text        NOT NULL CHECK (btrim(kind) <> ''),
    tier                smallint    NOT NULL CHECK (tier IN (1, 2)),
    subject             text        NOT NULL CHECK (btrim(subject) <> ''),
    requester_id        uuid        NOT NULL,
    payload_ref         text        NULL,
    status              text        NOT NULL CHECK (status IN ('pending', 'approved', 'denied', 'consumed')),
    requested_at        timestamptz NOT NULL,
    expires_at          timestamptz NOT NULL,
    decided_by          uuid        NULL,
    decided_at          timestamptz NULL,
    reason              text        NULL,
    consumed_at         timestamptz NULL,
    CHECK (expires_at > requested_at),
    CHECK ((status = 'pending') = (decided_by IS NULL))
);
CREATE INDEX approval_queue_item_pending_idx ON approval_queue_item (requested_at) WHERE status = 'pending';

GRANT SELECT, INSERT, UPDATE ON approval_queue_item TO bazaar_app;
