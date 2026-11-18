-- Memory graph revisions and edges (docs/design/constellation-memory-graph.md, "Store").
-- Namespace constellation (the component's own folder; common/ is frozen). No foreign keys to other namespaces.
--
-- Revisions are append-only: a row is never updated except for is_active, and never deleted. Exactly one revision per note
-- is active; the partial unique index enforces it, so concurrent writers cannot leave two active rows. Edges belong to one
-- revision and are removed only with it (they are never removed on their own, because revisions are never deleted).

CREATE TABLE constellation_memory_revision (
    note_id          text        NOT NULL,
    version          bigint      NOT NULL,
    scope_agent_id   text        NOT NULL,
    vault_path       text        NOT NULL,
    title            text        NOT NULL,
    kind             text        NOT NULL,
    status           text        NOT NULL,
    trust            text        NOT NULL,
    is_active        boolean     NOT NULL DEFAULT false,
    stored_at        timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (note_id, version)
);

CREATE UNIQUE INDEX constellation_memory_revision_active_key
    ON constellation_memory_revision (note_id)
    WHERE is_active;

CREATE TABLE constellation_memory_edge (
    note_id    text             NOT NULL,
    version    bigint           NOT NULL,
    target_id  text             NOT NULL,
    kind       text             NOT NULL,
    weight     double precision NOT NULL,
    PRIMARY KEY (note_id, version, target_id, kind),
    FOREIGN KEY (note_id, version) REFERENCES constellation_memory_revision (note_id, version)
);

GRANT SELECT, INSERT ON constellation_memory_revision TO bazaar_app;
GRANT UPDATE (is_active) ON constellation_memory_revision TO bazaar_app;
GRANT SELECT, INSERT ON constellation_memory_edge TO bazaar_app;
