-- The GC event kind for sealed per-voter reasons (doc 10, Per-person sealed reasons). gc_event.kind references gc_event_kind and
-- the core seed does not list it, so without this row every council.reason append fails on the foreign key. Idempotent.
-- The reasoning itself is sealed in gc_event.payload_ct under the voter's key; this row adds no column and changes no core table.
INSERT INTO gc_event_kind (kind, description) VALUES
    ('council.reason', 'One voter''s sealed Council reasoning, written at reveal or decision (doc 10)')
ON CONFLICT (kind) DO NOTHING;
