-- Registers the pull request event kind emitted by the GitHub module (doc 21, doc 52).
-- Without this row an insert of a pr_event violates the gc_event_kind foreign key.
INSERT INTO gc_event_kind (kind, description) VALUES
    ('pr_event', 'Pull request state change in a pr GC (doc 21, doc 52)')
ON CONFLICT (kind) DO NOTHING;
