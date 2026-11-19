-- audit/0002: external anchors for audit checkpoints (design doc 48 section 5.3, doc 15). Namespace audit: new migrations go in their
-- component's namespace, not common/ (frozen).
--
-- One row per (checkpoint, anchor target): the receipt a target returned for the checkpoint digest, and when it was anchored.
-- PostgresAuditAnchorStore (Security/Audit/Db) is the only writer. Anchors are outbound only; the receipts are checked by
-- AuditAnchorService and by the verifier.
--
-- Column choices:
--   checkpoint_id   references audit_checkpoint. The writer resolves it from the checkpoint content, so the caller never needs an id.
--   target_id       stable target identifier (for example rfc3161, offsite-git). Never a host name or a credential.
--   receipt         bytea. The target's proof (a DER time-stamp token, or a commit id). Non-empty.
--   anchored_at     timestamptz (UTC), truncated to microseconds.
--
-- One receipt per target per checkpoint: the primary key is (checkpoint_id, target_id). The runtime role bazaar_app gets INSERT and
-- SELECT only, like the other audit tables: no UPDATE or DELETE.

CREATE TABLE audit_checkpoint_anchor (
    checkpoint_id  bigint      NOT NULL REFERENCES audit_checkpoint (checkpoint_id),
    target_id      text        NOT NULL CHECK (target_id <> ''),
    receipt        bytea       NOT NULL CHECK (octet_length(receipt) > 0),
    anchored_at    timestamptz NOT NULL,
    PRIMARY KEY (checkpoint_id, target_id)
);

REVOKE ALL ON audit_checkpoint_anchor FROM PUBLIC;
REVOKE ALL ON audit_checkpoint_anchor FROM bazaar_app;
GRANT INSERT, SELECT ON audit_checkpoint_anchor TO bazaar_app;
