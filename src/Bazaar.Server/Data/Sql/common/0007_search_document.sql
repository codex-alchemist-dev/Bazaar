-- Full-text index behind ISearchProvider on Postgres (doc 03 WP10, doc 04 "Search"). The provider indexes whatever text
-- its caller supplies. Scope keeps one owner's documents apart. Nothing here reads GC message bodies: those are
-- ciphertext until the encryption question is decided (doc 04).
CREATE TABLE search_document (
    scope      text        NOT NULL CHECK (length(scope) BETWEEN 1 AND 200),
    doc_id     text        NOT NULL CHECK (length(doc_id) BETWEEN 1 AND 200),
    body       text        NOT NULL,
    tsv        tsvector    GENERATED ALWAYS AS (to_tsvector('simple', body)) STORED,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (scope, doc_id)
);

CREATE INDEX search_document_tsv_idx ON search_document USING gin (tsv);
