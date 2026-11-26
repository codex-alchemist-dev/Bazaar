-- Memory graph convergence onto Constellation's own nodes (docs/design/constellation-memory-graph.md, "Convergence design").
-- Namespace constellation; 0001 is embedded in Bazaar.Server, this file in Bazaar.Server.Constellation (Sql/components/).
--
-- A memory revision becomes an attribute set of its Vault doc node: doc_id is vault_docs.doc_id. It is nullable because a
-- note can be stored before the Vault indexer has seen its file. No data is moved here; MemoryGraphDocIdMigrator backfills it
-- (with a dry run). No foreign key to vault_docs: the Vault index can be rebuilt, and revisions are never deleted.

ALTER TABLE constellation_memory_revision ADD COLUMN doc_id uuid;

CREATE INDEX constellation_memory_revision_doc_idx
    ON constellation_memory_revision (doc_id)
    WHERE doc_id IS NOT NULL;

-- One active memory revision per doc node, as there is one per note.
CREATE UNIQUE INDEX constellation_memory_revision_active_doc_key
    ON constellation_memory_revision (doc_id)
    WHERE is_active AND doc_id IS NOT NULL;

-- doc_id may go from NULL to a value once, never change or go back to NULL, so revision history stays keyed by one doc.
CREATE FUNCTION constellation_memory_revision_doc_id_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.doc_id IS NOT NULL AND NEW.doc_id IS DISTINCT FROM OLD.doc_id THEN
        RAISE EXCEPTION 'constellation_memory_revision.doc_id is set once and never changed (note %, version %)',
            OLD.note_id, OLD.version
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER constellation_memory_revision_doc_id_guard
    BEFORE UPDATE OF doc_id ON constellation_memory_revision
    FOR EACH ROW EXECUTE FUNCTION constellation_memory_revision_doc_id_guard();

GRANT UPDATE (doc_id) ON constellation_memory_revision TO bazaar_app;
