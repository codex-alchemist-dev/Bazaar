-- The GC event kind the GitHub sink writes for PR entries (doc 21, Event mapping). gc_event.kind references gc_event_kind, and the
-- core seed does not list it, so without this row every PR entry append fails on the foreign key. Idempotent.
INSERT INTO gc_event_kind (kind, description) VALUES
    ('pr_event', 'A GitHub pull request entry written by the GitHub bridge (doc 21)')
ON CONFLICT (kind) DO NOTHING;
