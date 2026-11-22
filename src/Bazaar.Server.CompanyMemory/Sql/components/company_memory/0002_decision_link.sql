-- Company Memory decision chain index (docs/design/25-company-systems-knowledge.md, Company Memory row).
-- Component migration 0002 in the company_memory namespace (0001 is the older decision_links file in Bazaar.Server). A derived index: every row is rebuilt from the Vault decision
-- files, never edited by hand, so a rebuild may drop and refill it. Paths are Vault paths and have no foreign keys.
-- from_doc is the newer decision and to_doc the one it replaces. A missing flag marks a path with no decision file.

CREATE TABLE decision_link (
    from_doc     text    NOT NULL CHECK (btrim(from_doc) <> ''),
    to_doc       text    NOT NULL CHECK (btrim(to_doc) <> ''),
    relation     text    NOT NULL CHECK (btrim(relation) <> ''),
    status       text    NULL,
    from_missing boolean NOT NULL,
    to_missing   boolean NOT NULL,
    PRIMARY KEY (from_doc, to_doc, relation)
);

CREATE INDEX decision_link_to_doc ON decision_link (to_doc);
