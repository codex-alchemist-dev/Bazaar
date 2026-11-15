-- 0021: audit checkpoints (design doc 48 section 5.2, doc 15). Owner: audit checkpoint persistence.
--
-- One row per signed checkpoint over the audit_log chain. PostgresAuditCheckpointStore (Security/Audit/Db) is the only
-- writer. The signature is made outside this table, by the Identity Service (doc 14); this table stores the signed bytes'
-- inputs and the signature, never the key.
--
-- Column choices:
--   first_seq, last_seq   bigint. The covered range is contiguous: first_seq is the previous checkpoint's last_seq + 1,
--                         and the first checkpoint starts at 1. Gaps and overlaps are rejected by the writer under an
--                         advisory lock, and the UNIQUE constraints are the last line of defence.
--   row_count             must equal last_seq - first_seq + 1 (CHECK).
--   chain_head_hash       the audit_log hash at last_seq. The writer inserts only when that row exists and matches, so a
--                         checkpoint can never point at a head the chain does not contain.
--   merkle_root           bytea, SHA-256 (32 bytes), over the rows first_seq..last_seq.
--   signature             the signer's bytes over AuditCheckpoint.SignedBytes(). Non-empty; the signer verifies it.
--   key_id                the signing key identifier, so a verifier knows which key to check against.
--   created_at            timestamptz (UTC), truncated to microseconds before signing.
--
-- The runtime role bazaar_app gets INSERT and SELECT only. Like audit_log, checkpoints are append-only: no UPDATE or DELETE.

CREATE TABLE audit_checkpoint (
    checkpoint_id  bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    first_seq      bigint      NOT NULL CHECK (first_seq >= 1),
    last_seq       bigint      NOT NULL,
    row_count      bigint      NOT NULL CHECK (row_count >= 1),
    chain_head_hash bytea      NOT NULL CHECK (octet_length(chain_head_hash) = 32),
    merkle_root    bytea       NOT NULL CHECK (octet_length(merkle_root) = 32),
    signature      bytea       NOT NULL CHECK (octet_length(signature) > 0),
    key_id         text        NOT NULL CHECK (key_id <> ''),
    created_at     timestamptz NOT NULL,
    CONSTRAINT audit_checkpoint_range_ordered CHECK (last_seq >= first_seq),
    CONSTRAINT audit_checkpoint_row_count CHECK (row_count = last_seq - first_seq + 1),
    CONSTRAINT audit_checkpoint_last_seq_unique UNIQUE (last_seq),
    CONSTRAINT audit_checkpoint_first_seq_unique UNIQUE (first_seq)
);

REVOKE ALL ON audit_checkpoint FROM PUBLIC;
REVOKE ALL ON audit_checkpoint FROM bazaar_app;
GRANT INSERT, SELECT ON audit_checkpoint TO bazaar_app;
GRANT USAGE ON SEQUENCE audit_checkpoint_checkpoint_id_seq TO bazaar_app;
