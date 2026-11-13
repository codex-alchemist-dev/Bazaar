-- Principal handles: the name typed after "@" to mention a principal in a GC (WP-HANDLES).
-- Migration namespace: mentions/ (not a component namespace; the runner applies it after common and the component namespaces).
-- Reads common/0010 (agent) for the backfill. No foreign key: principal ids are agent ids (common/0010) or human ids (remote/0001),
-- and neither namespace owns a shared principal table.
--
-- A handle is optional (no row = not mentionable), unique, and stored lowercase. The shape is the one HandleSlug (C#) checks:
-- a lowercase ASCII letter or digit, then lowercase ASCII letters, digits, "_" or "-"; 2 to 32 characters. "here" and "all" are
-- the broadcast words and can never be handles. principal_kind uses the GC principal kinds (owner, human, agent); gc_member
-- itself stores no kind.

CREATE TABLE principal_handle (
    principal_id   uuid        PRIMARY KEY,
    principal_kind text        NOT NULL CHECK (principal_kind IN ('owner', 'human', 'agent')),
    handle         text        NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT principal_handle_handle_key UNIQUE (handle),
    CONSTRAINT principal_handle_handle_shape CHECK (handle ~ '^[a-z0-9][a-z0-9_-]{1,31}$'),
    CONSTRAINT principal_handle_handle_not_broadcast CHECK (handle NOT IN ('here', 'all'))
);

GRANT SELECT, INSERT ON principal_handle TO bazaar_app;
GRANT UPDATE (principal_kind, handle, updated_at) ON principal_handle TO bazaar_app;

-- Backfill: every existing agent gets the slug of its name, oldest agent first. The slug rule is HandleSlug.FromName: ASCII
-- upper case to lower case, every run of other characters to one "-", "-" and "_" trimmed from both ends, cut to 32 characters,
-- trimmed again. A slug shorter than 2 characters gives no handle. A taken (or broadcast) slug gets "-2", "-3", ... with the
-- base cut so the whole handle stays within 32 characters (HandleSlug.WithSuffix).
DO $$
DECLARE
    a         record;
    base      text;
    candidate text;
    suffix    text;
    n         integer;
BEGIN
    FOR a IN SELECT agent_id, name FROM agent ORDER BY created_at, agent_id LOOP
        base := translate(a.name, 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz');
        base := regexp_replace(base, '[^a-z0-9_]+', '-', 'g');
        base := btrim(base, '-_');
        base := rtrim(left(base, 32), '-_');
        IF length(base) < 2 THEN
            CONTINUE;
        END IF;

        n := 1;
        LOOP
            IF n = 1 THEN
                candidate := base;
            ELSE
                suffix := '-' || n::text;
                candidate := rtrim(left(base, 32 - length(suffix)), '-_') || suffix;
            END IF;

            IF candidate NOT IN ('here', 'all')
               AND NOT EXISTS (SELECT 1 FROM principal_handle WHERE handle = candidate) THEN
                INSERT INTO principal_handle (principal_id, principal_kind, handle) VALUES (a.agent_id, 'agent', candidate);
                EXIT;
            END IF;
            n := n + 1;
        END LOOP;
    END LOOP;
END $$;
