-- 0001 (gc namespace): Soul binding on GC events and message versions (docs/design/07 "Versioning": every message and
-- judgment records the soulVersion it was produced under; docs/design/01 server-assigned fields).
--
-- Typed, indexable columns, not a JSON payload. The Soul id and version are stored as plain values with no foreign key to
-- the Souls tables: GC and Souls are separate modules that depend on each other only through contracts (ISoulBindings and
-- SoulVersioningService). The write path validates the reference through that contract; a reconciliation check reports any
-- dangling value to Kodo.
--
-- Rules, enforced here:
--   * soul_id and soul_version are both set or both null.
--   * Only agent-authored events carry a Soul.
-- The server sets these columns; clients cannot. Existing rows stay null (pre-binding history).

ALTER TABLE gc_event
    ADD COLUMN soul_id      uuid,
    ADD COLUMN soul_version integer CHECK (soul_version >= 1),
    ADD CONSTRAINT gc_event_soul_pair CHECK ((soul_id IS NULL) = (soul_version IS NULL)),
    ADD CONSTRAINT gc_event_soul_agent_only CHECK (soul_id IS NULL OR author_kind = 'agent');

-- Events by Soul. Partial, so the common unbound case costs nothing. Note: this key does not lead with gc_id, so a
-- Soul-wide query scans every hash partition; per-GC reads still use the gc_id-leading keys.
CREATE INDEX gc_event_soul_idx ON gc_event (soul_id, soul_version) WHERE soul_id IS NOT NULL;

ALTER TABLE gc_message_version
    ADD COLUMN soul_id      uuid,
    ADD COLUMN soul_version integer CHECK (soul_version >= 1),
    ADD CONSTRAINT gc_message_version_soul_pair CHECK ((soul_id IS NULL) = (soul_version IS NULL));
