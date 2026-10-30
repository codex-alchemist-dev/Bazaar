-- Dev chat seed for scripts/dev/up.sh. Idempotent: re-running adds nothing new.
-- Variables (psql -v): owner_id, agent_id, gc_id. They are fixed local dev ids, not secrets.
-- gc_member is insert-only (joined_seq is part of its key), so membership is added only when no active row exists.
INSERT INTO gc (gc_id, type_key, title, created_by)
VALUES (:'gc_id', 'general', 'Dev chat', :'owner_id')
ON CONFLICT (gc_id) DO NOTHING;

INSERT INTO gc_member (gc_id, principal_id, role, joined_seq)
SELECT :'gc_id', m.principal_id, 'member', 0
FROM (VALUES (:'owner_id'::uuid), (:'agent_id'::uuid)) AS m(principal_id)
WHERE NOT EXISTS (
    SELECT 1 FROM gc_member existing
    WHERE existing.gc_id = :'gc_id' AND existing.principal_id = m.principal_id AND existing.left_seq IS NULL
);
