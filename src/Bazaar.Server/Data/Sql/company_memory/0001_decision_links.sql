-- Company Memory decision chain (doc 25, WP5). A derived index over the decision records in the Vault (doc 18: the
-- vault_docs rows with type = decision). Rebuilt from the Vault per vault_id; never authoritative. Owner: CM-WP5.
-- No foreign keys: this namespace is not common/, and the Vault tables are the source.

CREATE TABLE decision_links (
    vault_id     uuid    NOT NULL,
    from_doc     text    NOT NULL CHECK (btrim(from_doc) <> ''),
    to_doc       text    NOT NULL CHECK (btrim(to_doc) <> ''),
    relation     text    NOT NULL CHECK (relation IN ('supersedes')),
    -- Cached status of from_doc. Null when from_doc is not a known decision record (a missing source).
    status       text    NULL CHECK (status IS NULL OR status IN ('proposed', 'accepted', 'superseded')),
    from_missing boolean NOT NULL,
    to_missing   boolean NOT NULL,
    PRIMARY KEY (vault_id, from_doc, to_doc, relation)
);
CREATE INDEX decision_links_to_doc_idx ON decision_links (vault_id, to_doc);

GRANT SELECT, INSERT, UPDATE, DELETE ON decision_links TO bazaar_app;
