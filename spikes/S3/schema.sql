-- S3 schema: the doc 01 / doc 04 model, hash-partitioned by gc_id. __PARTS__ is substituted by Program.cs.
CREATE TABLE gc (
  gc_id uuid PRIMARY KEY, title text NOT NULL DEFAULT '', head_seq bigint NOT NULL DEFAULT 0,
  created_at timestamptz NOT NULL DEFAULT now());

CREATE TABLE gc_event (
  gc_id uuid NOT NULL REFERENCES gc(gc_id), seq bigint NOT NULL, event_id uuid NOT NULL,
  kind text NOT NULL, author_id uuid NOT NULL, message_id uuid, version int,
  payload jsonb NOT NULL DEFAULT '{}', client_msg_id uuid NOT NULL, created_at timestamptz NOT NULL,
  PRIMARY KEY (gc_id, seq),
  UNIQUE (gc_id, author_id, client_msg_id)) PARTITION BY HASH (gc_id);

CREATE TABLE gc_message (
  gc_id uuid NOT NULL REFERENCES gc(gc_id), message_id uuid NOT NULL, first_seq bigint NOT NULL, author_id uuid NOT NULL,
  PRIMARY KEY (gc_id, message_id)) PARTITION BY HASH (gc_id);

CREATE TABLE gc_message_version (
  gc_id uuid NOT NULL, message_id uuid NOT NULL, version int NOT NULL, body_ct bytea NOT NULL,
  editor_id uuid NOT NULL, created_at timestamptz NOT NULL,
  PRIMARY KEY (gc_id, message_id, version),
  FOREIGN KEY (gc_id, message_id) REFERENCES gc_message(gc_id, message_id)) PARTITION BY HASH (gc_id);

CREATE TABLE gc_mention (
  gc_id uuid NOT NULL, seq bigint NOT NULL, target_principal_id uuid NOT NULL,
  PRIMARY KEY (gc_id, seq, target_principal_id)) PARTITION BY HASH (gc_id);

DO $$ DECLARE t text; i int; BEGIN
  FOREACH t IN ARRAY ARRAY['gc_event','gc_message','gc_message_version','gc_mention'] LOOP
    FOR i IN 0..__PARTS__-1 LOOP
      EXECUTE format('CREATE TABLE %I PARTITION OF %I FOR VALUES WITH (MODULUS %s, REMAINDER %s)', t||'_p'||i, t, __PARTS__, i);
    END LOOP;
  END LOOP;
END $$;

-- Append-only guard as in doc 01: refuse UPDATE and DELETE on the log tables.
CREATE FUNCTION refuse_mutation() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'append-only'; END $$;
CREATE TRIGGER t_ev BEFORE UPDATE OR DELETE ON gc_event FOR EACH ROW EXECUTE FUNCTION refuse_mutation();
CREATE TRIGGER t_msg BEFORE UPDATE OR DELETE ON gc_message FOR EACH ROW EXECUTE FUNCTION refuse_mutation();
CREATE TRIGGER t_ver BEFORE UPDATE OR DELETE ON gc_message_version FOR EACH ROW EXECUTE FUNCTION refuse_mutation();
