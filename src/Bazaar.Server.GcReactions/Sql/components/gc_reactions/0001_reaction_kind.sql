-- GC reactions (docs/design/01-gc-data-model.md "Reactions", docs/design/02-wire-protocol-and-sync.md "Reactions on the wire").
-- Component migration 0001 in the gc_reactions namespace (not common, which is frozen).
-- A reaction is one gc_event row of kind 'reaction'; no new table. The kind registry takes the row, and a partial index serves the fold.

INSERT INTO gc_event_kind (kind, description) VALUES
    ('reaction', 'A member adds or removes an emoji reaction on a message (doc 01, Reactions); target_seq is the message seq')
ON CONFLICT (kind) DO NOTHING;

-- The fold reads every reaction of one message (or of one GC), oldest first. Partial, so message rows do not pay for it.
CREATE INDEX IF NOT EXISTS gc_event_reaction_target_idx ON gc_event (gc_id, target_seq, seq) WHERE kind = 'reaction';
