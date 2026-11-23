-- 0002 (gc namespace): a sealed event payload is ciphertext plus its key id, always together (doc 01, doc 61).
-- The GC log never holds keys; the caller seals and passes the bytes and the key id. Existing rows are unsealed (both null).

ALTER TABLE gc_event
    ADD CONSTRAINT gc_event_sealed_pair CHECK ((payload_ct IS NULL) = (key_id IS NULL));
