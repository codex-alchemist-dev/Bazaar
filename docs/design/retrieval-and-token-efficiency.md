# Retrieval and token efficiency: review and Graphify reference

Status: RESEARCHED (2026-10-30). A design review, not a decision. It changes no DECIDED item. Graphify (https://github.com/Graphify-Labs/graphify) is a reference only: nothing is ported, plugged in, or copied. The ideas below are candidates to fold into the existing PROPOSED docs when those are next revised.

## 1. How much RAG is Bazaar designed around?

A lot, but not as whole-corpus RAG. The design uses retrieval in four places, and each one is budgeted and scoped:

| Where | What is retrieved | How | Doc |
|---|---|---|---|
| Agent memory | Private, project, company and hive notes | Vector cosine (pgvector HNSW, `halfvec`) plus full-text, fused with reciprocal rank fusion (RRF); top-k within a token budget; scope, tier and decay applied | 08 section 8 |
| Vault search | Markdown blocks | Full-text (`ts_rank` on Windows, BM25 via `pg_search` on Linux) plus HNSW, RRF, then collapsed to documents | 18 "Search" |
| Context assembly | Memory, Vault docs for the task, GC backread | Per-section token budgets (`runtime.budget.*`) and a manifest of ids, not text | 06 "Context assembly" |
| Coding agents | Repo map and symbols | Repo map under a budget; no embeddings "until grep plus repo map is shown to be worse" | 17 "Context engine" |

Points that matter for the owner's question:

- **Embeddings are optional.** With no embedding profile, every search is lexical only, and the response says so (08, 18). Lexical retrieval is the floor, not a fallback of last resort.
- **Retrieval is scoped before ranking.** No unscoped query exists (08). Vault access is checked on every hit (18, "Access").
- **Links are a first-class retrieval path already.** The Vault stores `vault_links`, and context assembly fetches "wiki-linked docs for the task, by link and search, capped" (06). The Constellation's graph service is built on the same links (19).
- **The embedding model is unchosen.** Dimension and model were not evaluated (08 section 9). No retrieval quality number exists for Bazaar yet. The Mem0 figures cited in 08 are self-reported and not counted as evidence.

So: Bazaar is designed around scoped hybrid retrieval over its own Vault, with a link graph beside it. It is not designed to stuff a corpus into the prompt, and it has no RAG-specific shortcut to rely on.

## 2. Where tokens are spent today (by design)

The design sets budgets but does not yet measure spend. The sections below are what the docs commit to, and where the cost is likely to go.

| Section | Default budget (06 settings) | Notes |
|---|---|---|
| System (role, Constitution excerpt, tools) | 4000 | Fixed prefix, cache-friendly |
| Memory | 4000 | Top-k with provenance |
| Task state | 2000 | Kanban card, blockers, PR status |
| Vault | 4000 | Linked docs for the task |
| GC backread | 12000 | Trigger thread in full at its version; other GCs summarised. The largest section, and the one most worth measuring |
| Inbox | not capped | "Verbatim". What woke the agent |

Other known costs, all from the docs:

- **Multi-agent work.** Doc 06 cites Anthropic's "about 15x tokens" for multi-agent work and mixed debate results. Guards and metrics are required there.
- **Per-message scoring.** The Skimmer (13) scores messages before deciding to wake an agent. Shadow mode scores every pair in v1, so v1 pays full scoring cost.
- **Compaction.** The coding harness clears old tool results first, then summarises (17). That is good, but no measurement exists.

**Gap.** The manifest records ids, version ids and hashes (06), so per-section token counts can be measured per turn without storing text. That should be a v1 metric, before any retrieval tuning.

## 3. Graphify ideas that apply

Graphify is built around a code and document graph that an agent queries instead of reading whole files. Bazaar already has most of the parts. Mapping:

### 3.1 Graph-first retrieval (apply)

Expand from the task's known nodes along links before running vector search. Concretely: a `vault_neighbors(id, depth)` read that returns ids, titles and one-line gists, with the body fetched by id. This is the Graphify "ask the graph, read a few lines" pattern. It fits 06's "by link and search, capped" rule, and it reuses `vault_links` and the Constellation's graph service (19). No new store is needed.

### 3.2 EXTRACTED versus INFERRED edges (apply, cheaply)

Bazaar's edges come from different kinds of evidence, and the docs do not label them consistently:

- **EXTRACTED**: a wiki-link in a note (`vault_links`), a `gc_mention`, a decision's `decidedIn` reference, a PR-to-task `implements` edge.
- **INFERRED**: a vector-similarity hit (08 duplicate detection, RRF results), a `backlink` derived by the graph service (19), a `derived_from` link written by consolidation (08).

Tag every returned hit and every graph edge with its basis. Context assembly can then prefer EXTRACTED links when budgets are tight, and the owner can see why a note was pulled in. 08 already labels hits by scope, tier, kind and origin. Adding a basis label is a small change.

### 3.3 Incremental caching (already in the design)

Graphify's SHA256 cache matches 18's indexing: `git diff --name-status -M` per commit, and embeddings recomputed only for changed block hashes. Two checks to add: the block hash must be stored with the embedding row (18 already has `text_hash`), and the same rule must apply to the coding harness's repo map (17), so the map is rebuilt per changed file rather than per request.

### 3.4 One line per node in retrieval output (apply)

Progressive disclosure. Memory and Vault search return a compact card per hit (id, label, scope, tier, basis, one-line gist, approximate token count). The full body is one call away, by id. The context assembler then spends budget on the hits the agent actually opens, not on every hit's text. This is the single change most likely to cut tokens in `memory_retrieve` and `vault_search`, and it is a shape change, not a new system.

### 3.5 God-node summaries (apply, reuse Constellation)

Graphify reports the highest-degree nodes as a summary of what the codebase is about. The Constellation already computes Louvain communities server-side (19, "Clustering"). A per-project hub list (highest-degree notes and decisions, per cluster) is a small derived output. It can seed the Vault section of context assembly for a new agent, instead of the agent reading the index note.

### 3.6 Codebase RAG for agents working on repos (apply, candidate)

Agents that change code need to find the right symbols and files without reading whole repos. Doc 17 plans a repo map under a token budget, with Roslyn for C# and Tree-sitter for Luau and GDScript, and no embeddings until grep plus a repo map is shown to be worse. Graphify's approach is a concrete way to build that layer. Proposed shape, all PROPOSED:

- **Three indexed layers per repo.** File (path, language, hash), symbol (types, members, with line ranges and a signature line), and reference edges (calls, inherits, imports). Symbols come from Roslyn for C#, and from Tree-sitter for other languages once that binding is decided (the open question in PR #118's design doc).
- **Edge basis.** Every reference edge is EXTRACTED when the syntax shows it directly (a `using`, a base type, a named call). It is INFERRED when a name matches several candidates and resolution is a guess. Ambiguous names above a small cap are dropped rather than linked. The agent sees the basis on every edge, so it knows when to open the file and check.
- **Incremental cache per file.** The unit is the file's SHA256, the same rule 18 uses for Vault blocks. A commit that touches three files reparses three files. Deleted files drop out. The cache lives beside the repo and is rebuilt from git, so it is never a source of truth.
- **Entry points from hubs.** The highest-degree symbols per area (the "god nodes") make the first lines of the repo map, so an agent starts from the places the code depends on most.
- **Query, do not dump.** The agent asks for a symbol's neighbours at depth 1 to 3 and gets one line per edge, `source -relation-> target [basis]`. It opens a file only for the lines it will edit. Doc 17's per-request package (current file and diff, the asking GC thread, dependency state, the repo map under budget) stays as it is; this only changes how the map is built and queried.
- **Evaluation first.** Compare grep plus repo map against graph-first queries on a fixed set of tasks from real Bazaar PRs (tokens per task, files opened, and whether the change passed review). Only then decide on embeddings for code. Doc 17's rule stays in force.

A reference slice for the extraction and cache pieces was built in closed PR #118. It is kept as a reference and not as a port (see graph-index.md). The owner decides whether any of it is pursued.

### 3.7 Vault and general retrieval (apply)

The same graph applies to the Vault and to memory, which already share the Vault index (18, 08):

- **Neighbour expansion before search.** Context assembly reads a note's linked neighbours first (`vault_neighbors`, depth 1 by default), then runs hybrid search only for what the links miss. This is the cheapest source of relevant context, because the links were written by someone who judged them relevant.
- **Basis on every hit.** Vault and memory hits carry EXTRACTED (a link, a mention, a decision reference) or INFERRED (a similarity or consolidation link) labels. Context assembly prefers EXTRACTED when a budget is tight.
- **Card output.** Search returns one card per hit with an approximate token count; the body is one call away. The budget then pays only for what the agent opens.
- **Hub summaries.** Per project, the highest-degree notes and decisions, per Constellation cluster (19), seed a new agent's Vault section instead of the index note.

### 3.8 General retrieval rules (apply)

These apply to codebase, Vault and memory alike:

1. Graph or link expansion first, then search, then the budget. Never the reverse.
2. Every result states its basis and its scope, and carries an id that fetches the body.
3. Every cache is keyed by content hash and rebuilt from the source of truth (git, the Vault), never trusted on its own.
4. Retrieval quality is measured on a labelled set per domain, with tokens per outcome as the cost measure. No embedding model is chosen before that set exists.

### 3.9 Not applicable

- **Obsidian or wiki export.** The Vault already is Markdown in git.
- **A separate graph store.** Bazaar's graph is `vault_links` plus GC references (19). A second store would duplicate it.
- **Whole-graph JSON as an agent input.** The JSON is a cache and an index. Agents should only ever receive the card format in 3.4.

## 4. Recommended next steps (for the owner to choose)

1. Add per-section token counts from the manifest as a v1 metric (06, 34). Measure before tuning.
2. Adopt the card format (3.4) and the basis labels (3.2) in 08 and 18 when those docs are next revised.
3. Add `vault_neighbors` (3.1) to 18's tool list, reading `vault_links`.
4. Decide whether 17's repo map should use a symbol graph (3.6). If yes, use the closed PR #118 slice (see graph-index.md) as a reference, not as a port, and set up a retrieval evaluation (08 section "Quality evaluation") to compare it against grep plus map.
5. Choose the embedding model only after the labelled retrieval set exists (08 section 9).

## 5. Evidence gaps

- No measured token baseline exists for any Bazaar section.
- No retrieval quality number exists. The recall@k set in 08 is planned, not built.
- Whether embeddings beat grep plus a repo map for code is unknown (17 and `docs/research/notes/Bazaar remaining gaps research/coding_agent_harness.md`, Key Question 3).
- The Mem0 and Graphify efficiency claims are self-reported. They are not used as evidence here.

## Sources

- `docs/design/08-memory-system.md` sections 8 and 9, and the quality evaluation.
- `docs/design/18-vault.md` "Indexing", "Search", "Access", and the tables.
- `docs/design/06-agent-runtime.md` "Context assembly" and the settings table.
- `docs/design/04-postgres-schema-and-migrations.md` "Lexical and vector search".
- `docs/design/13-skimmer-bouncer.md` cost table and shadow mode.
- `docs/design/17-coding-agent-harness.md` "Context engine" and "Code intelligence".
- `docs/design/19-constellation-and-git-solar-system.md` "Graph service" and "Clustering".
- `docs/research/notes/Bazaar remaining gaps research/vault_constellation_tech.md` (hybrid query, RRF).
- `docs/research/notes/Bazaar remaining gaps research/coding_agent_harness.md` Key Question 3.
