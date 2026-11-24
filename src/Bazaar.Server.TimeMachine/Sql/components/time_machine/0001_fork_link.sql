-- fork_link (docs/design/26-fun-systems.md, data and interfaces; section 4). Owner: Time Machine (component time-machine).
-- Component migration 0001 in the time_machine namespace (not common, which is frozen). Append-only: the runtime role needs
-- INSERT and SELECT only. No foreign keys: GC and project tables belong to other components; the caller guarantees the links.
-- A fork copies no messages, so this row is all a fork writes. Rows are metadata and survive erasure (doc 26, behaviour).

CREATE TABLE fork_link (
    new_gc_id    uuid        PRIMARY KEY,
    source_gc_id uuid        NOT NULL,
    source_seq   bigint      NOT NULL CHECK (source_seq >= 0),
    vault_commit text        NULL CHECK (vault_commit IS NULL OR length(vault_commit) BETWEEN 1 AND 128),
    request_id   uuid        NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT fork_link_request_key UNIQUE (source_gc_id, source_seq, request_id)
);

CREATE INDEX fork_link_source_idx ON fork_link (source_gc_id, created_at);
