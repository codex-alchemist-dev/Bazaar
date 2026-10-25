-- Core GC schema (doc 01, doc 04; spike S3). Owner: G1. Do not edit after release; add a new numbered file.
-- The partition count comes from the session setting bazaar.partition_count (the runner sets it from gc.partition_count,
-- default 16) and is recorded in schema_setting; the runner refuses a later run with a different count.

CREATE TABLE schema_setting (
    key         text        PRIMARY KEY,
    value       text        NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT now()
);

-- GC type registry. A type is a row, not a behaviour fork (doc 01); other docs add rows.
-- kanban_binding is yes / optional / no (doc 01 sketches a bool but seeds an "optional" value).
CREATE TABLE gc_type (
    type_key           text PRIMARY KEY,
    label              text NOT NULL,
    default_visibility text NOT NULL CHECK (default_visibility IN ('members', 'two_members_owner', 'agent_owner')),
    default_wake       text NOT NULL,
    archive_policy     text NOT NULL DEFAULT 'manual',
    kanban_binding     text NOT NULL CHECK (kanban_binding IN ('yes', 'optional', 'no')),
    registered_by      text NOT NULL DEFAULT 'core'
);

INSERT INTO gc_type (type_key, label, default_visibility, default_wake, kanban_binding) VALUES
    ('general',      'General',      'members',           'mentions, DMs',                    'yes'),
    ('project',      'Project',      'members',           'mentions',                         'yes'),
    ('pr',           'Pull request', 'members',           'PR events',                        'yes'),
    ('research',     'Research',     'members',           'mentions',                         'optional'),
    ('architecture', 'Architecture', 'members',           'mentions',                         'optional'),
    ('emergency',    'Emergency',    'members',           'emergency interrupt',              'yes'),
    ('sidewalk',     'Sidewalk',     'members',           'mentions, owner messages',         'no'),
    ('dm',           'Direct message', 'two_members_owner', 'DM',                             'no'),
    ('thinking',     'Thinking',     'agent_owner',       'never',                            'no'),
    ('ward',         'Psych Ward',   'members',           'per doc 26',                       'no'),
    ('vent',         'Vent',         'agent_owner',       'per doc 26',                       'no'),
    ('inbox',        'Inbox',        'agent_owner',       'per docs 22 and 06',               'no'),
    ('sandbox',      'Sandbox',      'members',           'mentions',                         'no'),
    ('brainstorm',   'Brainstorm',   'members',           'mentions',                         'no');

CREATE TABLE gc_event_kind (
    kind        text PRIMARY KEY,
    description text NOT NULL
);

INSERT INTO gc_event_kind (kind, description) VALUES
    ('message',          'A new message (first version)'),
    ('message_edit',     'A new version of an existing message'),
    ('system',           'Server-generated line, such as archive or a tombstone'),
    ('structured',       'Task and PR state written by the task service (doc 12)'),
    ('external_mail',    'Inbound mail metadata (doc 22); source_trust = external'),
    ('quarantine',       'Marks an earlier seq as quarantined (doc 15)'),
    ('handover',         'Session handover (doc 15)'),
    ('council.open',     'Council vote opened (doc 10)'),
    ('council.reveal',   'Council votes revealed (doc 10)'),
    ('council.transition', 'Council state change (doc 10)'),
    ('council.decision', 'Council decision (doc 10)');

CREATE TABLE gc (
    gc_id         uuid        PRIMARY KEY DEFAULT uuidv7(),
    parent_gc_id  uuid        REFERENCES gc (gc_id),
    type_key      text        NOT NULL REFERENCES gc_type (type_key),
    title         text        NOT NULL,
    tags          text[]      NOT NULL DEFAULT '{}',
    metadata      jsonb       NOT NULL DEFAULT '{}',
    -- Last assigned seq. The append locks this row, which keeps seq gap-free per GC.
    head_seq      bigint      NOT NULL DEFAULT 0 CHECK (head_seq >= 0),
    created_by    uuid        NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),
    archived_at   timestamptz
);
CREATE INDEX gc_parent_idx ON gc (parent_gc_id) WHERE parent_gc_id IS NOT NULL;

CREATE TABLE gc_member (
    gc_id        uuid   NOT NULL REFERENCES gc (gc_id),
    principal_id uuid   NOT NULL,
    role         text   NOT NULL DEFAULT 'member',
    joined_seq   bigint NOT NULL DEFAULT 0,
    left_seq     bigint,
    PRIMARY KEY (gc_id, principal_id)
);
CREATE INDEX gc_member_principal_idx ON gc_member (principal_id, gc_id);

-- The log tables are hash-partitioned by gc_id; every key starts with gc_id.
CREATE TABLE gc_event (
    gc_id            uuid        NOT NULL REFERENCES gc (gc_id),
    seq              bigint      NOT NULL CHECK (seq >= 1),
    event_id         uuid        NOT NULL DEFAULT uuidv7(),
    kind             text        NOT NULL REFERENCES gc_event_kind (kind),
    author_kind      text        NOT NULL CHECK (author_kind IN ('owner', 'human', 'agent', 'system', 'external_bridge')),
    author_id        uuid        NOT NULL,
    session_id       uuid,
    -- Provenance lives on the event row for every kind (doc 01, doc 15). The server sets it; clients cannot.
    source_trust     text        NOT NULL CHECK (source_trust IN ('owner', 'system', 'member-human', 'agent', 'external', 'quarantined')),
    tainted_at_write boolean     NOT NULL DEFAULT false,
    taint_sources    jsonb,
    derived_from     jsonb,
    message_id       uuid,
    version          integer     CHECK (version >= 1),
    target_seq       bigint,
    reply_to_seq     bigint,
    payload          jsonb,
    -- Personal values in the payload are ciphertext under the person's key.
    payload_ct       bytea,
    key_id           uuid,
    -- Idempotency key supplied by the writer; a retry returns the same event.
    client_msg_id    text,
    created_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (gc_id, seq),
    UNIQUE (gc_id, event_id),
    UNIQUE (gc_id, author_id, client_msg_id)
) PARTITION BY HASH (gc_id);

CREATE TABLE gc_message (
    gc_id      uuid   NOT NULL REFERENCES gc (gc_id),
    message_id uuid   NOT NULL,
    first_seq  bigint NOT NULL,
    author_id  uuid   NOT NULL,
    PRIMARY KEY (gc_id, message_id)
) PARTITION BY HASH (gc_id);
CREATE INDEX gc_message_seq_idx ON gc_message (gc_id, first_seq);

-- Full copy per version; the highest version is current. No current-version column on gc_message (insert-only).
CREATE TABLE gc_message_version (
    gc_id      uuid        NOT NULL,
    message_id uuid        NOT NULL,
    version    integer     NOT NULL CHECK (version >= 1),
    seq        bigint      NOT NULL,
    body_ct    bytea,
    body_nonce bytea,
    key_id     uuid,
    editor_id  uuid        NOT NULL,
    tombstone  boolean     NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (gc_id, message_id, version),
    FOREIGN KEY (gc_id, message_id) REFERENCES gc_message (gc_id, message_id),
    CHECK (tombstone = (body_ct IS NULL))
) PARTITION BY HASH (gc_id);

CREATE TABLE gc_mention (
    gc_id               uuid   NOT NULL REFERENCES gc (gc_id),
    seq                 bigint NOT NULL,
    target_principal_id uuid   NOT NULL,
    PRIMARY KEY (gc_id, seq, target_principal_id)
) PARTITION BY HASH (gc_id);
CREATE INDEX gc_mention_target_idx ON gc_mention (target_principal_id, gc_id, seq);

DO $$
DECLARE
    n int := coalesce(nullif(current_setting('bazaar.partition_count', true), ''), '16')::int;
    parent text;
BEGIN
    IF n < 1 OR n > 256 THEN
        RAISE EXCEPTION 'gc.partition_count must be between 1 and 256, got %', n;
    END IF;
    INSERT INTO schema_setting (key, value) VALUES ('gc.partition_count', n::text);
    FOREACH parent IN ARRAY ARRAY['gc_event', 'gc_message', 'gc_message_version', 'gc_mention'] LOOP
        FOR i IN 0..n - 1 LOOP
            EXECUTE format('CREATE TABLE %I PARTITION OF %I FOR VALUES WITH (MODULUS %s, REMAINDER %s)',
                           parent || '_p' || i, parent, n, i);
        END LOOP;
    END LOOP;
END
$$;

-- Append-only: UPDATE, DELETE and TRUNCATE raise for every role, owner included.
CREATE FUNCTION gc_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% is append-only; % is not allowed', TG_TABLE_NAME, TG_OP USING ERRCODE = 'restrict_violation';
END
$$;

CREATE TRIGGER gc_event_append_only BEFORE UPDATE OR DELETE ON gc_event
    FOR EACH ROW EXECUTE FUNCTION gc_append_only();
CREATE TRIGGER gc_event_no_truncate BEFORE TRUNCATE ON gc_event
    FOR EACH STATEMENT EXECUTE FUNCTION gc_append_only();
CREATE TRIGGER gc_message_append_only BEFORE UPDATE OR DELETE ON gc_message
    FOR EACH ROW EXECUTE FUNCTION gc_append_only();
CREATE TRIGGER gc_message_version_append_only BEFORE UPDATE OR DELETE ON gc_message_version
    FOR EACH ROW EXECUTE FUNCTION gc_append_only();
CREATE TRIGGER gc_message_version_no_truncate BEFORE TRUNCATE ON gc_message_version
    FOR EACH STATEMENT EXECUTE FUNCTION gc_append_only();

-- Runtime role: DML only. NOLOGIN here; setup gives it a login and password. No DDL, and no UPDATE or DELETE on
-- the log tables (the triggers above also stop the owner). Roles are cluster-wide, so create only if missing.
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'bazaar_app') THEN
        CREATE ROLE bazaar_app NOLOGIN;
    END IF;
END
$$;

GRANT SELECT ON schema_setting, gc_type, gc_event_kind TO bazaar_app;
GRANT INSERT ON gc_type, gc_event_kind TO bazaar_app;
GRANT SELECT, INSERT ON gc, gc_member, gc_event, gc_message, gc_message_version, gc_mention TO bazaar_app;
GRANT UPDATE (head_seq, archived_at, title, tags, metadata) ON gc TO bazaar_app;
GRANT UPDATE (role, left_seq) ON gc_member TO bazaar_app;

-- The runtime role must see the schema but not create in it.
DO $$
BEGIN
    EXECUTE format('GRANT USAGE ON SCHEMA %I TO bazaar_app', current_schema());
END
$$;
