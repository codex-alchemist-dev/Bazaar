-- Interrupt reasons are personal fields (doc 24 schema note, C01): they are sealed with AES-256-GCM before they reach the
-- database, and the plaintext column from 0015 is dropped. The additional data bound to each sealed value is the frame id
-- and agent id, so a sealed reason cannot be moved to another row. The key comes from the personal-data key service
-- (doc 01 WP6). No production code writes frames yet, so no existing row needs converting.
ALTER TABLE interrupt_frame
    ADD COLUMN reason_sealed bytea,
    ADD COLUMN reason_nonce bytea,
    ADD COLUMN reason_key_id uuid;

ALTER TABLE interrupt_frame DROP COLUMN reason;

ALTER TABLE interrupt_frame
    ALTER COLUMN reason_sealed SET NOT NULL,
    ALTER COLUMN reason_nonce SET NOT NULL,
    ALTER COLUMN reason_key_id SET NOT NULL,
    ADD CONSTRAINT interrupt_frame_reason_nonce_len CHECK (octet_length(reason_nonce) = 12),
    -- Ciphertext plus the 16-byte tag. A reason is at most 500 characters, which is at most 2000 UTF-8 bytes.
    ADD CONSTRAINT interrupt_frame_reason_sealed_len CHECK (octet_length(reason_sealed) BETWEEN 17 AND 2016);
