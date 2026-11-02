-- Vault index tables (docs/design/18-vault.md, "Data and interfaces"; work package 5). Owner: VLT4.
-- Markdown in git is the source of truth; every table here is a rebuildable index (doc 18, Indexing).
-- No foreign keys to GC or task tables; the cross-table keys arrive in 0005.
-- vault_id has no table of its own yet: one Vault per install is the default, and every row carries the value.
-- The embedding dimension is chosen at setup (C39), so the halfvec column is unconstrained here; the trigger on
-- vault_block_embedding checks each vector against embedding_profile.dimensions.

CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE vault_docs (
    doc_id      uuid        PRIMARY KEY DEFAULT uuidv7(),
    vault_id    uuid        NOT NULL,
    path        text        NOT NULL CHECK (path <> '' AND left(path, 1) <> '/'),
    title       text        NOT NULL CHECK (title <> ''),
    type        text        NOT NULL CHECK (type IN ('index', 'plan', 'deep-dive', 'research', 'constitution',
                                                     'decision', 'archive', 'memory', 'summary', 'twin', 'charter', 'draft')),
    project     text,
    area        text        NOT NULL CHECK (area <> ''),
    -- Memory and charter files only: the agent whose private area this is (doc 18, front matter).
    owner       text,
    frontmatter jsonb       NOT NULL DEFAULT '{}'::jsonb,
    head_commit char(40)    CHECK (head_commit ~ '^[0-9a-f]{40}$'),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT vault_docs_path_unique UNIQUE (vault_id, path),
    CONSTRAINT vault_docs_owner_scope CHECK (owner IS NULL OR type IN ('memory', 'charter'))
);
CREATE INDEX vault_docs_type_idx ON vault_docs (vault_id, type);

CREATE TABLE vault_blocks (
    doc_id       uuid        NOT NULL REFERENCES vault_docs (doc_id) ON DELETE RESTRICT,
    ordinal      integer     NOT NULL CHECK (ordinal >= 0),
    block_id     text,
    heading_path text[]      NOT NULL DEFAULT '{}',
    start_line   integer     NOT NULL CHECK (start_line >= 1),
    end_line     integer     NOT NULL CHECK (end_line >= start_line),
    text_hash    bytea       NOT NULL CHECK (octet_length(text_hash) = 32),
    body         text        NOT NULL,
    -- Language fixed to english here; doc 18 Q5 (english or simple) is open.
    tsv          tsvector    GENERATED ALWAYS AS (to_tsvector('english', body)) STORED,
    PRIMARY KEY (doc_id, ordinal),
    CONSTRAINT vault_blocks_block_id_unique UNIQUE (doc_id, block_id)
);
CREATE INDEX vault_blocks_tsv_idx ON vault_blocks USING gin (tsv);

-- Keyed by ordinal and hash: a row is valid only while its block still has the same text_hash.
CREATE TABLE vault_block_embedding (
    doc_id     uuid     NOT NULL,
    ordinal    integer  NOT NULL,
    profile_id uuid     NOT NULL REFERENCES embedding_profile (profile_id) ON DELETE RESTRICT,
    text_hash  bytea    NOT NULL CHECK (octet_length(text_hash) = 32),
    embedding  halfvec  NOT NULL,
    PRIMARY KEY (doc_id, ordinal, profile_id),
    FOREIGN KEY (doc_id, ordinal) REFERENCES vault_blocks (doc_id, ordinal) ON DELETE RESTRICT
);

CREATE FUNCTION vault_block_embedding_dims() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    expected integer;
BEGIN
    SELECT dimensions INTO expected FROM embedding_profile WHERE profile_id = NEW.profile_id;
    IF vector_dims(NEW.embedding) <> expected THEN
        RAISE EXCEPTION 'embedding has % dimensions; profile % expects %',
            vector_dims(NEW.embedding), NEW.profile_id, expected USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER vault_block_embedding_dims BEFORE INSERT OR UPDATE OF embedding, profile_id ON vault_block_embedding
    FOR EACH ROW EXECUTE FUNCTION vault_block_embedding_dims();

-- One row per link occurrence. target_doc is null while unresolved; target_raw keeps the text as written.
CREATE TABLE vault_links (
    src_doc       uuid     NOT NULL,
    src_ordinal   integer  NOT NULL,
    link_ord      integer  NOT NULL CHECK (link_ord >= 0),
    kind          text     NOT NULL CHECK (kind IN ('link', 'embed')),
    target_raw    text     NOT NULL CHECK (target_raw <> ''),
    target_doc    uuid     REFERENCES vault_docs (doc_id) ON DELETE RESTRICT,
    target_anchor text,
    alias         text,
    PRIMARY KEY (src_doc, src_ordinal, link_ord),
    FOREIGN KEY (src_doc, src_ordinal) REFERENCES vault_blocks (doc_id, ordinal) ON DELETE RESTRICT
);
CREATE INDEX vault_links_target_idx ON vault_links (target_doc);
CREATE INDEX vault_links_unresolved_idx ON vault_links (target_raw) WHERE target_doc IS NULL;

-- The Git DAG index for the Vault repo (doc 18, Indexing). Commits are facts: inserted once, never changed.
CREATE TABLE vault_commit (
    vault_id      uuid        NOT NULL,
    sha           char(40)    NOT NULL CHECK (sha ~ '^[0-9a-f]{40}$'),
    parents       char(40)[]  NOT NULL DEFAULT '{}',
    author        text        NOT NULL,
    committed_at  timestamptz NOT NULL,
    refs          text[]      NOT NULL DEFAULT '{}',
    touched_paths text[]      NOT NULL DEFAULT '{}',
    gc_id         uuid,
    task_id       uuid,
    is_merge      boolean     NOT NULL,
    PRIMARY KEY (vault_id, sha),
    CONSTRAINT vault_commit_merge_matches_parents CHECK (is_merge = (cardinality(parents) > 1))
);
CREATE INDEX vault_commit_time_idx ON vault_commit (vault_id, committed_at DESC);

-- The commit the index has caught up to. Startup catches up from here or rebuilds (doc 18).
CREATE TABLE vault_state (
    vault_id       uuid     PRIMARY KEY,
    indexed_commit char(40) NOT NULL CHECK (indexed_commit ~ '^[0-9a-f]{40}$')
);

-- Runtime role (created in 0001). The index is rebuildable, so it may be rewritten; commits are insert-only.
GRANT SELECT, INSERT, UPDATE, DELETE ON vault_docs, vault_blocks, vault_block_embedding, vault_links TO bazaar_app;
GRANT SELECT, INSERT ON vault_commit TO bazaar_app;
GRANT SELECT, INSERT, UPDATE ON vault_state TO bazaar_app;
