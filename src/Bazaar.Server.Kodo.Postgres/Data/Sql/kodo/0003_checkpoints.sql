-- Kodo checkpoints and anchor marks (docs/design/46-kodo-health-module.md sections 6.5 and 6.5.2).
-- A checkpoint covers one window of kodo_journal records: first and last sequence, time range, the hash the window links to, the head
-- hash, and a Merkle root over the window's record hashes. digest is SHA-256 over those fields; signature and signer_id are set only
-- when a signer is registered (no key provider exists yet). Rows are never updated or deleted by retention, so a root outlives the
-- pruning of the records it covers. kodo_checkpoint_anchor holds one row per sink that accepted a checkpoint's anchor.

CREATE TABLE kodo_checkpoint (
    sequence       bigint       PRIMARY KEY CHECK (sequence >= 0),
    first_sequence bigint       NOT NULL CHECK (first_sequence >= 0),
    first_at_ticks bigint       NOT NULL CHECK (first_at_ticks >= 0),
    last_at_ticks  bigint       NOT NULL CHECK (last_at_ticks >= 0),
    cut_at_ticks   bigint       NOT NULL CHECK (cut_at_ticks >= 0),
    previous_head  bytea        NOT NULL CHECK (octet_length(previous_head) = 32),
    head_hash      bytea        NOT NULL CHECK (octet_length(head_hash) = 32),
    root           bytea        NOT NULL CHECK (octet_length(root) = 32),
    digest         bytea        NOT NULL CHECK (octet_length(digest) = 32),
    signature      bytea,
    signer_id      text         CHECK (signer_id IS NULL OR char_length(signer_id) BETWEEN 1 AND 128),
    stored_at      timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT kodo_checkpoint_window_check CHECK (first_sequence <= sequence),
    CONSTRAINT kodo_checkpoint_signature_check CHECK ((signature IS NULL) = (signer_id IS NULL))
);

CREATE INDEX kodo_checkpoint_first_sequence_idx ON kodo_checkpoint (first_sequence);

CREATE TABLE kodo_checkpoint_anchor (
    checkpoint_sequence bigint       NOT NULL REFERENCES kodo_checkpoint (sequence),
    sink_id             text         NOT NULL CHECK (char_length(sink_id) BETWEEN 1 AND 128),
    anchored_at_ticks   bigint       NOT NULL CHECK (anchored_at_ticks >= 0),
    stored_at           timestamptz  NOT NULL DEFAULT now(),
    PRIMARY KEY (checkpoint_sequence, sink_id)
);

CREATE FUNCTION kodo_checkpoint_no_update() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% rows are append-only', TG_TABLE_NAME;
END;
$$;

CREATE TRIGGER kodo_checkpoint_append_only BEFORE UPDATE OR DELETE ON kodo_checkpoint
    FOR EACH ROW EXECUTE FUNCTION kodo_checkpoint_no_update();

CREATE TRIGGER kodo_checkpoint_anchor_append_only BEFORE UPDATE ON kodo_checkpoint_anchor
    FOR EACH ROW EXECUTE FUNCTION kodo_checkpoint_no_update();
