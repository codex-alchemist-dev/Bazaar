-- Objective roadmap views (docs/design/38-ryusei-fuda.md, "Layer 3: objective roadmap", "Derived views"; WP-R12).
-- Owner: RyuseiFuda layer 3. Builds on 0009 (vault_docs, vault_links are read as documented there) and 0012 (tasks, task_links).
--
-- This namespace owns no tables. Objectives and phases are Vault documents: front matter `kind: objective` or `kind: phase`
-- (the vault type is 'plan'; the vault_docs type check is not changed here). Progress is derived, never stored.
-- The front-matter contract is enforced in C# (Bazaar.Server.Roadmap.RoadmapFrontMatter) when the Vault indexer reads it;
-- these views read the stored jsonb as written.
--
-- Phase slug: a phase document's slug is its file name without the .md extension (roadmap/phases/<slug>.md). An objective's
-- front-matter `phase` must equal that slug (a plain slug, not a wiki-link).
--
-- Views only: no triggers, no functions, no extensions. Plain PostgreSQL.

-- One row per objective document.
CREATE OR REPLACE VIEW roadmap_objectives AS
SELECT d.doc_id,
       d.vault_id,
       d.path,
       d.title,
       d.frontmatter->>'phase'  AS phase,
       d.frontmatter->>'status' AS status,
       d.frontmatter->>'owner'  AS owner,
       CASE WHEN jsonb_typeof(d.frontmatter->'depends_on') = 'array'
            THEN ARRAY(SELECT jsonb_array_elements_text(d.frontmatter->'depends_on'))
            ELSE '{}'::text[]
       END AS depends_on
FROM vault_docs d
WHERE d.frontmatter->>'kind' = 'objective';

-- One row per phase document. The slug is the file name without its .md extension.
CREATE OR REPLACE VIEW roadmap_phases AS
SELECT d.doc_id,
       d.vault_id,
       d.path,
       d.title,
       substring(d.path from '([^/]+)\.md$') AS slug
FROM vault_docs d
WHERE d.frontmatter->>'kind' = 'phase';

-- Per objective: linked tasks (task_links kind 'vault_doc' whose ref is the objective path), counted by tasks.state.
-- done_share is done / linked, a numeric between 0 and 1, and NULL when no task is linked. Objectives with no links
-- still appear, with zero counts.
CREATE OR REPLACE VIEW roadmap_progress AS
SELECT o.doc_id,
       o.vault_id,
       o.path,
       COUNT(t.state)                                       AS linked_tasks,
       COUNT(t.state) FILTER (WHERE t.state = 'backlog')     AS backlog,
       COUNT(t.state) FILTER (WHERE t.state = 'ready')       AS ready,
       COUNT(t.state) FILTER (WHERE t.state = 'in_progress') AS in_progress,
       COUNT(t.state) FILTER (WHERE t.state = 'review')      AS review,
       COUNT(t.state) FILTER (WHERE t.state = 'done')        AS done,
       CASE WHEN COUNT(t.state) = 0 THEN NULL
            ELSE (COUNT(t.state) FILTER (WHERE t.state = 'done'))::numeric / COUNT(t.state)
       END                                                  AS done_share
FROM roadmap_objectives o
LEFT JOIN task_links l ON l.kind = 'vault_doc' AND l.ref = o.path
LEFT JOIN tasks t ON t.id = l.task_id
GROUP BY o.doc_id, o.vault_id, o.path;

-- Per phase: the number of objectives in it, and whether every one of them is done or dropped.
-- A phase with no objectives does not pass the gate (gate_passed is false), so an empty phase cannot clear by accident.
CREATE OR REPLACE VIEW roadmap_phase_gate AS
SELECT p.doc_id,
       p.vault_id,
       p.path,
       p.slug,
       COUNT(o.doc_id)                                                       AS objective_count,
       COUNT(o.doc_id) FILTER (WHERE o.status IS NULL OR o.status NOT IN ('done', 'dropped')) AS open_objectives,
       COUNT(o.doc_id) > 0
         AND COUNT(o.doc_id) FILTER (WHERE o.status IS NULL OR o.status NOT IN ('done', 'dropped')) = 0 AS gate_passed
FROM roadmap_phases p
LEFT JOIN roadmap_objectives o ON o.phase = p.slug AND o.vault_id = p.vault_id
GROUP BY p.doc_id, p.vault_id, p.path, p.slug;

-- Read by the server's runtime role. The views run with their owner's rights over the tables they read, as the
-- Constellation views do, but the runtime role reads these directly because nothing here is private.
GRANT SELECT ON roadmap_objectives, roadmap_phases, roadmap_progress, roadmap_phase_gate TO bazaar_app;
