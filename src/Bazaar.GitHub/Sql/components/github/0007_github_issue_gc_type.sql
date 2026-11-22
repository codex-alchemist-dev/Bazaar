-- The GC type for issue GCs. gc.type_key references gc_type and the core seed has no issue type, so without this row
-- creating an issue GC fails on the foreign key. Members-visible, woken by mentions and assignments (OPEN: doc 21 names no
-- issue type; this is the proposed one). Operators who use another key under Bazaar:GitHub:IssueGc:GcTypeKey register it themselves.
INSERT INTO gc_type (type_key, label, default_visibility, default_wake, kanban_binding, registered_by) VALUES
    ('issue', 'Issue', 'members', 'mentions, assignments', 'optional', 'github')
ON CONFLICT (type_key) DO NOTHING;
