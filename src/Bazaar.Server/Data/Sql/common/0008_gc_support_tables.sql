-- GC support tables (doc 01 data sketch, doc 04): entry_ref, read_state, consumer_cursor, gc_access_request,
-- person_key (with the erasure function) and embedding_profile. Owner: G1. Builds on 0001.

-- Attachments and embeds. The snapshot is frozen at post, so the table is append-only. Hash-partitioned like the log;
-- the partition count is the value 0001 recorded in schema_setting.
CREATE TABLE entry_ref (
    gc_id    uuid   NOT NULL REFERENCES gc (gc_id),
    seq      bigint NOT NULL,
    ref_kind text   NOT NULL CHECK (ref_kind IN ('pr', 'task', 'vault_doc', 'gc', 'blob', 'decision')),
    ref_id   text   NOT NULL,
    snapshot jsonb  NOT NULL DEFAULT '{}',
    PRIMARY KEY (gc_id, seq, ref_kind, ref_id)
) PARTITION BY HASH (gc_id);

DO $$
DECLARE
    n int := (SELECT value::int FROM schema_setting WHERE key = 'gc.partition_count');
BEGIN
    FOR i IN 0..n - 1 LOOP
        EXECUTE format('CREATE TABLE %I PARTITION OF entry_ref FOR VALUES WITH (MODULUS %s, REMAINDER %s)',
                       'entry_ref_p' || i, n, i);
    END LOOP;
END
$$;

CREATE TRIGGER entry_ref_append_only BEFORE UPDATE OR DELETE ON entry_ref
    FOR EACH ROW EXECUTE FUNCTION gc_append_only();

-- Where each principal has read up to; unread count is head_seq - last_read_seq (doc 01).
CREATE TABLE read_state (
    principal_id  uuid   NOT NULL,
    gc_id         uuid   NOT NULL REFERENCES gc (gc_id),
    last_read_seq bigint NOT NULL DEFAULT 0 CHECK (last_read_seq >= 0),
    PRIMARY KEY (principal_id, gc_id)
);

-- Software consumers (notifier, unread counter, search indexer, Kanban view); idempotent on (consumer, gc_id, seq).
CREATE TABLE consumer_cursor (
    consumer text   NOT NULL,
    gc_id    uuid   NOT NULL REFERENCES gc (gc_id),
    last_seq bigint NOT NULL DEFAULT 0 CHECK (last_seq >= 0),
    PRIMARY KEY (consumer, gc_id)
);

-- DM access requests; the request and the decision are also `system` events in the log.
CREATE TABLE gc_access_request (
    id          uuid        PRIMARY KEY DEFAULT uuidv7(),
    gc_id       uuid        NOT NULL REFERENCES gc (gc_id),
    requester   uuid        NOT NULL,
    status      text        NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'approved', 'denied', 'withdrawn')),
    decided_by  uuid,
    decided_seq bigint,
    created_at  timestamptz NOT NULL DEFAULT now(),
    decided_at  timestamptz,
    CHECK ((status = 'pending') = (decided_by IS NULL))
);
CREATE INDEX gc_access_request_gc_idx ON gc_access_request (gc_id, status);

-- GC per-person keys only (Soul and Passport keys are doc 14's). wrapped_key is wrapped under the bootstrap key.
-- Destruction clears the wrapped key and sets destroyed_at; nothing else may change a row.
CREATE TABLE person_key (
    key_id       uuid        PRIMARY KEY DEFAULT uuidv7(),
    person_id    uuid        NOT NULL,
    wrapped_key  bytea,
    created_at   timestamptz NOT NULL DEFAULT now(),
    destroyed_at timestamptz,
    CHECK ((destroyed_at IS NULL) = (wrapped_key IS NOT NULL))
);
CREATE INDEX person_key_person_idx ON person_key (person_id);

CREATE FUNCTION person_key_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'person_key rows are never deleted' USING ERRCODE = 'restrict_violation';
    END IF;
    IF OLD.destroyed_at IS NOT NULL
       OR NEW.key_id IS DISTINCT FROM OLD.key_id
       OR NEW.person_id IS DISTINCT FROM OLD.person_id
       OR NEW.created_at IS DISTINCT FROM OLD.created_at
       OR NEW.destroyed_at IS NULL THEN
        RAISE EXCEPTION 'person_key may only be destroyed, once' USING ERRCODE = 'restrict_violation';
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER person_key_guard BEFORE UPDATE OR DELETE ON person_key
    FOR EACH ROW EXECUTE FUNCTION person_key_guard();

-- Erasure hook (doc 04): the only way to destroy a key. It runs as the schema owner; only bazaar_erasure may call it.
-- Appending the tombstone versions is the erasure flow's job (docs 32 and 33), in the same transaction.
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'bazaar_erasure') THEN
        CREATE ROLE bazaar_erasure NOLOGIN;
    END IF;
EXCEPTION WHEN duplicate_object OR unique_violation THEN
    NULL; -- another database on the cluster created it concurrently
END
$$;

DO $$
BEGIN
    EXECUTE format($f$
        CREATE FUNCTION erase_person_key(p_key_id uuid) RETURNS boolean
        LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog
        AS $body$
            WITH destroyed AS (
                UPDATE %1$I.person_key SET wrapped_key = NULL, destroyed_at = now()
                WHERE key_id = p_key_id AND destroyed_at IS NULL
                RETURNING 1)
            SELECT EXISTS (SELECT 1 FROM destroyed)
        $body$
    $f$, current_schema());
    EXECUTE format('REVOKE ALL ON FUNCTION %I.erase_person_key(uuid) FROM PUBLIC', current_schema());
    EXECUTE format('GRANT EXECUTE ON FUNCTION %I.erase_person_key(uuid) TO bazaar_erasure', current_schema());
    EXECUTE format('GRANT USAGE ON SCHEMA %I TO bazaar_erasure', current_schema());
END
$$;

-- One embedding profile is active per install; changing it means re-embedding (docs 08, 18).
CREATE TABLE embedding_profile (
    profile_id  uuid        PRIMARY KEY DEFAULT uuidv7(),
    provider_id text        NOT NULL,
    model_id    text        NOT NULL,
    dimensions  integer     NOT NULL CHECK (dimensions > 0),
    created_at  timestamptz NOT NULL DEFAULT now(),
    active      boolean     NOT NULL DEFAULT false
);
CREATE UNIQUE INDEX embedding_profile_one_active ON embedding_profile (active) WHERE active;

GRANT SELECT, INSERT ON entry_ref, gc_access_request, person_key, embedding_profile TO bazaar_app;
GRANT SELECT, INSERT ON read_state, consumer_cursor TO bazaar_app;
GRANT UPDATE (last_read_seq) ON read_state TO bazaar_app;
GRANT UPDATE (last_seq) ON consumer_cursor TO bazaar_app;
GRANT UPDATE (status, decided_by, decided_seq, decided_at) ON gc_access_request TO bazaar_app;
GRANT UPDATE (active) ON embedding_profile TO bazaar_app;

-- Membership is insert-only (edits, members and mentions thread): every join, role change or leave appends a row keyed
-- by the seq it took effect at. The current row per principal is the one with the highest joined_seq; left_seq set on
-- that row means the principal has left. Nothing updates or deletes a row, even for the owner.
ALTER TABLE gc_member DROP CONSTRAINT gc_member_pkey;
ALTER TABLE gc_member ADD PRIMARY KEY (gc_id, principal_id, joined_seq);
REVOKE UPDATE ON gc_member FROM bazaar_app;
CREATE TRIGGER gc_member_append_only BEFORE UPDATE OR DELETE ON gc_member
    FOR EACH ROW EXECUTE FUNCTION gc_append_only();
CREATE TRIGGER gc_member_no_truncate BEFORE TRUNCATE ON gc_member
    FOR EACH STATEMENT EXECUTE FUNCTION gc_append_only();

CREATE VIEW gc_member_current AS
    SELECT DISTINCT ON (gc_id, principal_id) gc_id, principal_id, role, joined_seq, left_seq
    FROM gc_member
    ORDER BY gc_id, principal_id, joined_seq DESC;
GRANT SELECT ON gc_member_current TO bazaar_app;
