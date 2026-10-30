-- TODO(component): the Constellation is a Bazaar Component. Its migrations belong in components/constellation/,
-- which the runner does not read yet, so this file stays in common/ until the component model PR lands.
-- SECURITY (open, must close before the graph service reads these): the node and edge views are unfiltered and
-- bazaar_app has SELECT on them, so a direct query can read DM titles. Only constellation_visible_nodes applies
-- the visibility rule. Close this with a SECURITY DEFINER wrapper, or drop the direct SELECT, before shipping.
-- Constellation projection views (docs/design/19-constellation-and-git-solar-system.md, Data and interfaces, Visibility,
-- Testing; work package 2). Owner: CGR. Builds on 0001 (gc, gc_member_current), 0009 (vault_docs, vault_links) and 0012
-- (tasks, task_edges).
--
-- This doc owns no tables (DECISIONS-LOG C16): everything here is a view or a function. Nothing is stored twice.
--
-- Node ids are text. Vault documents, GCs and tasks use their uuid as text (uuids are unique across these tables).
-- Ghost nodes (unresolved wiki-links) use 'ghost:' followed by the target text as written.
-- Edges are directed: from_node_id to to_node_id. GC parent edges run from the child GC to its parent GC.
--
-- Visibility (constellation_visible_nodes(viewer, is_owner); docs 18 Access and 19 Visibility):
--   * The owner (is_owner = true) sees every node. The owner is a parameter, not a hardcoded principal.
--   * Vault documents of type memory or charter are one agent's private area. They are visible only to that agent, where
--     the stored owner equals the viewer's agent id (bare, or with the front-matter 'agent:' prefix from doc 18).
--     Every other vault document is visible to any viewer.
--   * GCs are visible to members only (current membership, gc_member_current with left_seq null). This covers DMs, which
--     are two-member GCs. Thinking GCs produce no nodes at all (type_key 'thinking', gc_type 0001).
--   * Tasks are visible to members of their origin GC.
--   * A ghost node is visible when a document that is itself visible links to it, so a private note cannot reveal a
--     link target to another viewer.
--   * The views carry titles and labels only. No gc_message body, payload or payload_ct is ever projected here.
--   * Edges are not filtered by this function. A consumer must drop any edge whose endpoint is not in the visible set.

CREATE VIEW constellation_doc_nodes AS
SELECT d.doc_id::text AS node_id,
       'vault_document'::text AS kind,
       d.type,
       d.title,
       d.area,
       d.owner
FROM vault_docs d;

-- Unresolved wiki-links: one node per distinct target text. The node key is derived from target_raw.
CREATE VIEW constellation_ghost_nodes AS
SELECT DISTINCT 'ghost:' || l.target_raw AS node_id,
                'ghost'::text AS kind,
                l.target_raw
FROM vault_links l
WHERE l.target_doc IS NULL;

-- Resolved wiki-links, one row per link occurrence.
CREATE VIEW constellation_link_edges AS
SELECT l.src_doc::text AS from_node_id,
       l.target_doc::text AS to_node_id,
       'links_to'::text AS kind
FROM vault_links l
WHERE l.target_doc IS NOT NULL;

-- Edges to ghost nodes, one row per unresolved link occurrence.
CREATE VIEW constellation_ghost_edges AS
SELECT l.src_doc::text AS from_node_id,
       'ghost:' || l.target_raw AS to_node_id,
       'links_to'::text AS kind
FROM vault_links l
WHERE l.target_doc IS NULL;

-- Thinking GCs produce no nodes (doc 19; C11 nests them under working GCs).
CREATE VIEW constellation_gc_nodes AS
SELECT g.gc_id::text AS node_id,
       'gc'::text AS kind,
       g.type_key,
       g.title,
       g.archived_at IS NOT NULL AS archived
FROM gc g
WHERE g.type_key <> 'thinking';

-- Parent edges. Both ends must be GC nodes, so a Thinking child never gets an edge.
CREATE VIEW constellation_gc_edges AS
SELECT c.gc_id::text AS from_node_id,
       c.parent_gc_id::text AS to_node_id,
       'parent'::text AS kind
FROM gc c
JOIN gc p ON p.gc_id = c.parent_gc_id
WHERE c.parent_gc_id IS NOT NULL
  AND c.type_key <> 'thinking'
  AND p.type_key <> 'thinking';

CREATE VIEW constellation_task_nodes AS
SELECT t.id::text AS node_id,
       'task'::text AS kind,
       t.title,
       t.state,
       t.project_id
FROM tasks t;

-- Blocker to blocked.
CREATE VIEW constellation_task_edges AS
SELECT e.blocker_id::text AS from_node_id,
       e.blocked_id::text AS to_node_id,
       'blocks'::text AS kind
FROM task_edges e;

-- The per-viewer node set. Returns node ids as text, matching the node views above.
-- Keep the rule in step with the header comment above and with docs 18 and 19.
CREATE FUNCTION constellation_visible_nodes(viewer uuid, is_owner boolean DEFAULT false)
RETURNS SETOF text
LANGUAGE sql STABLE
AS $$
    WITH visible_docs AS (
        SELECT d.doc_id
        FROM vault_docs d
        WHERE is_owner
           OR d.type NOT IN ('memory', 'charter')
           OR d.owner IN (viewer::text, 'agent:' || viewer::text)
    ),
    member_gcs AS (
        SELECT m.gc_id
        FROM gc_member_current m
        WHERE m.principal_id = viewer AND m.left_seq IS NULL
    )
    SELECT doc_id::text FROM visible_docs
    UNION
    SELECT g.gc_id::text
    FROM gc g
    WHERE g.type_key <> 'thinking'
      AND (is_owner OR g.gc_id IN (SELECT gc_id FROM member_gcs))
    UNION
    SELECT t.id::text
    FROM tasks t
    WHERE is_owner OR t.origin_gc_id IN (SELECT gc_id FROM member_gcs)
    UNION
    SELECT 'ghost:' || l.target_raw
    FROM vault_links l
    WHERE l.target_doc IS NULL
      AND (is_owner OR l.src_doc IN (SELECT doc_id FROM visible_docs))
$$;

-- Functions are executable by PUBLIC by default; the runtime role gets it explicitly, as erase_person_key does in 0008.
REVOKE ALL ON FUNCTION constellation_visible_nodes(uuid, boolean) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION constellation_visible_nodes(uuid, boolean) TO bazaar_app;

-- Read-only projections for the runtime role. No writes: a view has nothing to write to.
GRANT SELECT ON constellation_doc_nodes, constellation_ghost_nodes, constellation_link_edges, constellation_ghost_edges,
    constellation_gc_nodes, constellation_gc_edges, constellation_task_nodes, constellation_task_edges TO bazaar_app;
