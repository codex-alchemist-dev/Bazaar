# 61. Graph retrieval engine: RAG plus DAG on many cores and the GPU (GraphDRAG, server side)

Status: PROPOSED (2026-11-20). Design only. Nothing in this document is built. Every decision below is PROPOSED unless it is quoted from a DECIDED source, which is named. No open owner trade-off: the earlier cross-project question is answered by the owner (2026-11-20, section 0), and every other earlier question is settled in section 17.
Depends on: constellation-memory-graph.md (the retriever this doc accelerates), 19-constellation-and-git-solar-system.md ("Agent memory governance (GraphDRAG)", DECIDED 2026-11-13; renderer and GPU path), 08-memory-system.md (sections 8 and 9: retrieval, embeddings), 18-vault.md (access, search, block embeddings), 04-postgres-schema-and-migrations.md (embedding profile), 03-server-host-and-providers.md (provider seam, settings registry), 40-core-feature-modules-audit.md and 42-deps-as-modules.md (module shape, pgvector as a module), 46-kodo-health-module.md (module health contract), 15-security-taint-and-approvals.md and 48-gap-decisions-security-identity.md (audit), 35-spikes-plan.md (spike S28 is added by this doc), constellation-scale-and-lod.md (measured Arc B580 facts).
Used by: none yet.

## 0. Core principle (owner, 2026-11-20)

GraphDRAG is not a separate memory system. It runs over the Constellation's own nodes: the Vault notes, research, plans, internal docs and decisions that agents write, per project and across linked projects. Agents already write their research, plans and decisions into the Vault, so project knowledge and agent memory are one graph. There are no separate memory nodes. An agent's private memory note is a Vault document like any other, distinguished by its area and front matter, not by living in another node space.

What this means for this design:

1. **The GPU index is built over the Constellation node set**: Vault documents keyed by `vault_docs.doc_id`, edges from `vault_links` (resolved `target_doc`) and the front-matter relations doc 19 lists, and later the decision and GC references of doc 19. It is not built over the memory-only tables `constellation_memory_revision` and `constellation_memory_edge`.
2. **Scope still applies to every hop.** Per-agent and per-project scope and the `IVaultAccess` filter hold for every seed and every hop target. Project-to-project hops take part only where the existing design allows them: Hive copies (doc 08 section 6.1, where crossing projects is a copy note, never a link target of private text) and role grants.
3. **Cross-project reach is company knowledge (owner, 2026-11-20).** Owner, verbatim: "Cross-project is on the principle of company knowledge? Something knowledge. The knowledge the company builds up over time. Learnings, principles, what works for us, what doesn't. Stuff AI typically can't have since they never usually bring other 'unrelated' projects into context." So the cross-project channel is the **company knowledge tier** of doc 08 section 6: generalised principles, learnings, precedents, incidents and what works or fails, distilled from all projects with provenance, under `company/memory/`. It is always in scope for every project's agents (section 3A.10). It is Constellation nodes in the one graph, not a second store. Personal agent memory stays isolated; project-private notes cross only through a Hive grant, or by being distilled into a company note that names no project. No wider project-to-project hop is added.
4. **Flagged finding (PROPOSED convergence):** the memory graph store that exists today is a second node space. Section 1.1 describes it and the PROPOSED plan to converge it onto Constellation nodes.

### 0.1 Proactive injection first, tools as the fallback (owner design intent, 2026-11-20)

Relayed by the coordinator: agents always keep tools to search and read files, phrases and topics. But GraphDRAG should be good enough to place the relevant knowledge into the agent's soft long-horizon context **proactively**, so the agent rarely needs those tools. Proactive injection is the primary path; on-demand search and read tools are the fallback, never removed.

What this means here: the pipeline of section 3A runs without being asked, at the triggers in section 3A.11, and its quality is measured by how often the agent still has to search (section 13.5, "Injection quality"). The tools stay, and every tool call is also a measurement.

## 1. Where things stand today

There is **no server-side GPU retrieval** in the repo. Today's retrieval runs on the CPU and in Postgres:

| Piece | What it does today | Where |
|---|---|---|
| Vault vector search | Exact cosine over `vault_block_embedding.embedding` (`halfvec`), filtered by the access clause in SQL, `ORDER BY e.embedding <=> $5, b.doc_id, b.ordinal`. No HNSW index exists in the migrations, so this is a sequential scan. | `src/Bazaar.Server/Vault/Data/VaultSearchRepository.cs` (`VectorCandidatesAsync`), `src/Bazaar.Server/Data/Sql/common/0009_vault.sql` |
| Embedding storage | Upsert of block embeddings per profile as pgvector text literals; dimension checked by a trigger. | `src/Bazaar.Server/Vault/Data/VaultEmbeddingRepository.cs` |
| Memory graph retrieval | `MemoryGraphRetriever`: RRF-fused lexical and vector seeds, expansion up to 2 hops along `Link` (1.0), `DerivedFrom` (0.8) and `Supersedes` (0.5) with a 0.5 penalty per hop, `IVaultAccess.CanRead` on every candidate before it scores or expands, Active revisions only, `TokenBudgetPacker`. Each hop is one `IMemoryGraphStore.GetActiveAsync` call (a Postgres round trip). | `src/Bazaar.Server/Constellation/Memory/` |
| Vulkan | Client renderer only: loader and device probe, offscreen and swapchain node drawing, GLSL vertex and fragment shaders committed with their SPIR-V and embedded as resources. No compute pipeline. | `src/Bazaar.Constellation.Vulkan/` |

Facts found while reading the code, which this design has to respect:

- `constellation-memory-graph.md` says vector seeds are not built (the embedding adapter is doc 08 WP6). The vector search that exists works on Vault **blocks** keyed by `(doc_id, ordinal)`. The memory graph works on **note ids**. No code maps block hits to note seeds yet.
- On `main` at the time of writing, `MemoryGraphRetriever` is constructed only in `tests/Bazaar.Server.Tests/Constellation/Memory/MemoryGraphRetrieverTests.cs`. I found no DI registration or production caller, although `constellation-memory-graph.md` "Build status" says it is registered. That doc or the code needs a fix; this doc does not change either.
- The only Arc B580 measurements in the repo are for drawing (`constellation-scale-and-lod.md` section 10): discrete device, 11.9 GiB device-local heap, Vulkan 1.4.356, `pageableDeviceLocalMemory` true. There are no compute or retrieval measurements.
- The Vault side already has a one-hop graph read: `VaultSearchRepository.NeighbourCardsAsync` returns the first block of each document one wiki-link away from the seeds, in either direction, with targets the caller cannot read left out.
- Two access rules in the code are wider than the docs, and the segment map of section 5.1 must follow `IVaultAccess`, not them: (a) `VaultAccess.CanRead` in `src/Bazaar.Server/Vault/Search/VaultAccess.cs` treats every document with no `owner` as readable by every caller, so project areas are not checked by project membership in Vault search, while `VaultPathAccess` (`src/Bazaar.Server/Memory/Access/VaultPathAccess.cs`) does check it; (b) `VaultPathAccess` lets every agent read `hive/**`, while doc 25 and `constellation-memory-graph.md` limit a Hive copy to the projects its grant covers. Doc 08 work package 17 aligns both with doc 08 section 6.

### 1.1 Flagged finding: the memory graph is a second node space

| | Constellation nodes (Vault) | Memory graph store (today) |
|---|---|---|
| Node key | `vault_docs.doc_id` (uuid v7, survives renames) | memory note ULID (`MemoryNote.Id`) as `text` |
| Coverage | every Vault document: research, plans, decisions, deep-dives, memory, drafts and the rest of the doc 18 type list | only notes under `agents/<agent>/memory/` (the sink's filter) |
| Edges | `vault_links` (`link`, `embed`, resolved `target_doc`), plus the doc 19 front-matter relations | `constellation_memory_edge`: `Link`, `DerivedFrom`, `Supersedes`, and provenance kinds to `agent:`, `gc:`, `message:` |
| Revisions | git history of the Vault file; one current row | every revision stored, one active, rollback by re-activation |
| Embeddings | `vault_block_embedding` keyed by `(doc_id, ordinal, profile_id)` | none |

The two spaces describe the same memory notes under two keys. A memory note's wiki-link to a research doc has no edge in the memory store today (the sink passes an empty title map and resolves only note-id links, `constellation-memory-graph.md` open item b), and research, plans and decisions are not in the memory store at all. GraphDRAG over the memory store alone therefore cannot reach project knowledge, which the core principle requires.

**PROPOSED convergence.** Memory notes become ordinary Constellation nodes keyed by `doc_id`. The memory-specific attributes move onto those nodes as a projection, not a second graph:

1. Trust (`MemoryTier`), memory kind and status come from the note's front matter on the Vault document (doc 19 already records `source_trust` and `tier` there, round 7 C20) and are indexed beside `vault_docs`.
2. `DerivedFrom` and `Supersedes` become the doc 19 edge kinds `derived_from` and `supersedes`, read from front matter. `Link` becomes `vault_links`.
3. Provenance (`AuthoredBy`, `FromGc`, `FromMessage`) stays as labels and audit payload, keyed by `doc_id`. It never expanded a retrieval and does not start to.
4. Revisions and rollback keep their DECIDED behaviour (doc 19: "a correction keeps the earlier version"). Either the revision table is re-keyed to `doc_id` (a migration) or it stays as the revision log and the graph reads only the active revision through a `doc_id` mapping. Which one is a schema choice and needs one answer (decision 10 in section 17).
5. `MemoryGraphRetriever`'s rules (RRF seeds, weighted hops, hop cap 2, 0.5 per hop, filter before scoring, Active only, packing) become the rules of one Constellation retriever over the converged node set.

The convergence is a change to `constellation-memory-graph.md` and lands as its own docs PR before any code (CLAUDE.md, docs first). This doc depends on it: the GPU index is specified over the converged node set, and the CPU retriever over that set is the reference.

## 2. Decided inputs (quoted, not changed here)

- **Placement (DECIDED, owner 2026-11-13, doc 19 and DECISIONS-LOG).** Agent memory and GraphDRAG belong to the Constellation's server side, `Server.Constellation`, "not a separate module". Server-held, owner-visible, per-agent isolation with role scoping, retention and crypto-erase, provenance, correction and rollback.
- **Vault access applies to the graph (doc 19, C38).** Every read, search hit and link resolution is checked against the caller's principal. The owner reads all; an agent reads its own private memory; other agents do not.
- **One embedding profile per install, stored as `halfvec` (C39).** The dimension is fixed at setup.
- **Server: one OS-agnostic .NET build (CLAUDE.md).** Windows first (the owner's PC), Linux must work. Anything OS-specific sits behind a provider interface.
- **Module rule (CLAUDE.md).** Every new feature is its own module pair on Bazaar.Core. Bazaar.Server and Bazaar.Core gain no feature code.
- **Licence (CLAUDE.md, DECISIONS-LOG round 4).** OSI-only public build, no paid components.
- **Deps are modules (DECISIONS-LOG, doc 42).** PostgreSQL and pgvector are modules tagged Deps.
- **Configurability rule (DECISIONS-LOG round 13).** Undecided values become operator settings with a safe default. Security invariants are not settings. Settings may tighten, never loosen.
- **Vulkan binding (doc 19).** Vortice.Vulkan (MIT) is the chosen binding, Silk.NET.Vulkan (MIT) the fallback. The repo pins Vortice.Vulkan 3.3.0.

### 2.1 How this doc fits the DECIDED placement

The placement decision keeps GraphDRAG, its rules and its retriever in `Server.Constellation`. This doc does not move them. It adds a **graph kernel** (`IGraphKernel`): an engine module that the Constellation retriever calls for the heavy inner loops (vector top-k, expansion, DAG closure, ranking), with a multi-core CPU engine as the default and GPU engines as optional provider modules, in the same way a provider module serves an OS-specific need (doc 03). The retriever, its scoring rules, its scope filter and its packing stay where they are. If the graph kernel is absent, disabled or failing, the retriever behaves exactly as today. Whether this reading is acceptable is decision 1 in section 17.

## 3. Scope and non-goals

In scope (PROPOSED):

1. The retrieval pipeline of section 3A: seeds, graph expansion and ranking, DAG closure, subgraph assembly, optional community context, optional model rerank, and packing under the token budget, all over Constellation nodes.
2. Two in-memory compute backends for the heavy steps: a **multi-core CPU backend** (first-class, not only a fallback) and a **GPU backend** (Vulkan compute). Today's Postgres path stays as the reference and the last fallback.
3. GPU and CPU vector top-k over the install's embedding profile, scoped to what the principal may read.
4. GPU and CPU graph kernels over the Constellation node graph (Vault documents, including memory notes): bounded breadth-first expansion with edge-kind weights, DAG ancestor and descendant closure, and personalised PageRank, all with a per-query visibility mask.
5. An in-memory index (on the GPU, and in host memory for the CPU backend), built from Postgres and kept current from the Vault indexer, the memory revision store and the embedding writer.
6. Fallback from GPU to CPU to Postgres so that no query fails because of a backend.
7. Health, metrics, settings and the benchmark spike S28 that decides which backend and which ranking method are the defaults.

Non-goals:

- **No silent change to retrieval semantics.** The stages today's retriever already has (RRF seeds, weighted hops with a cap of 2 and a 0.5 hop penalty, filter before scoring, Active only, packing) keep their rules, and backends must reproduce them exactly. The new stages in section 3A are PROPOSED additions to the retriever spec; they land through the docs PR for `constellation-memory-graph.md` (WP0) before code, and each is behind a setting until S28 and the retrieval evaluation choose defaults.
- **No lexical search on the GPU.** Full-text stays in Postgres (`ts_rank` on Windows, `pg_search` on Linux, per CLAUDE.md).
- **No embedding inference on the GPU.** Embeddings come through the gateway (doc 08 section 9). Running an embedding model on the server GPU is a separate question (decision 8 in section 17).
- **No widening of scope.** Cross-project reach is the company tier (section 0 item 3, doc 08 section 6), which `IVaultAccess` already lets every agent read; project-to-project reach stays with Hive copies and role grants. The graph kernel never decides what a principal may read.
- **No new source of truth.** The Vault is the source of truth and Postgres indexes it. GPU memory is a cache that can be thrown away at any time.
- **No client work.** The client Constellation renderer is unchanged.
- **No ANN index replacement decision for pgvector.** Whether pgvector gets an HNSW index is doc 08 and doc 18's call. This doc treats exact pgvector as the reference either way.
- **No per-query model dependency.** No step that decides what is retrieved, or whether it may be read, needs a model call. The optional rerank (3A.6) only reorders.

## 3A. Retrieval pipeline: RAG plus DAG (PROPOSED)

Owner direction (2026-11-20): context is not a list of semantically similar snippets pasted into the prompt. It is real graph reasoning over Constellation nodes: multi-hop traversal over typed, weighted, directed edges, DAG-aware where the structure is a DAG, and assembly of paths and subgraphs, not just top-k chunks. This section is the pipeline that meets that direction. Every algorithm choice here is PROPOSED; S28 and the retrieval evaluation (13.5) choose the defaults.

### 3A.0 The pipeline at a glance

```
query ──> 1 seeds (lexical + vector, RRF) ──> 2 bounded expansion (typed, weighted, masked)
      ──> 3 DAG closure (decisions, supersedes, derived_from, depends_on, plan and task ancestry)
      ──> 4 ranking (hop-decay, or personalised PageRank over the masked subgraph)
      ──> 5 subgraph assembly (paths, gists, provenance, why included)
      ──> 6 optional: community and hub context (Louvain, doc 19)
      ──> 7 optional: model rerank through the gateway
      ──> 8 pack under the token budget ──> context pack
```

Every stage runs over the principal's readable node set only. The range mask (section 10) applies to every seed, every hop, every closure step and every random-walk transition, and the CPU post-filter checks every node in the final pack.

### 3A.1 Seeds

- Lexical: Postgres full-text, unchanged. Vector: top-k on the selected backend (GPU, CPU or pgvector).
- Block hits collapse to their document node (`doc_id`) before fusion; the collapse rule (best block, or a sum) is decision 9 in section 17.
- Fusion: today's `RrfFusion.Fuse` with today's constants.
- Optional seed from context: the GC, task or plan the agent is working in can be added as a pinned seed (setting `graph_retrieval.seed.work_context`, default off), so retrieval starts from where the work is, not only from the query text.

### 3A.2 Bounded multi-hop expansion

- Level-synchronous breadth-first search from the seeds over typed, directed edges with per-kind weights (5.3), never entering a masked, non-Active or tombstoned node.
- Depth: today's cap of 2 stays the default and the parity anchor. A larger cap (setting `graph_retrieval.expand.max_hops`, up to 4) is a PROPOSED change, used only with a ranking method that controls fan-out (3A.4).
- Fan-out bound: a per-node and per-query cap on edges followed (`graph_retrieval.expand.max_frontier`), applied in a deterministic order (weight, then id), so hubs cannot flood the frontier and results stay reproducible.
- Each reached node records its best predecessor and edge kind, so stage 5 can rebuild the path.

### 3A.3 DAG closure

Some edge kinds form a DAG, or are meant to: `supersedes`, `derived_from`, `decides`, `depends_on` (doc 19 and doc 38, PROPOSED there), `follows_precedent`, `conditioned_on` and `resolved_by` (section 5.3), and plan, phase and task ancestry (parent edges). For these kinds the pipeline computes, from each selected node:

- **Supersession chain:** follow `supersedes` to the newest Active head. A superseded document is never returned as current; it can appear as labelled history if the budget allows. Deprecated documents stay excluded (today's rule).
- **Decision ancestry:** for a node that a decision `decides`, include that decision and, through `supersedes` and `derived_from`, the decisions it replaced or came from, so an agent sees why something is the way it is.
- **Dependency closure:** ancestors and descendants along `depends_on` and plan or task parent edges, up to `graph_retrieval.dag.max_depth` (default 8) and a node cap.
- **Precedent closure:** for a node that `follows_precedent`, include the precedent and the conditions it is `conditioned_on`; for an incident, include what `resolved_by` names. A precedent past its `valid_to` or review date is included only as labelled history with "expired" or "stale", never as the current rule.
- **Cycles:** the data is written by agents and people, so a "DAG" kind can contain a cycle. (Research input R1 argues `derived_from` and `supersedes` cannot cycle because they point back in time; this doc disagrees, see 3B.) Closure keeps a visited set and stops at a repeated node; the cycle is reported in the pack's diagnostics and as a metric, never as an error.
- Closure runs over the mask like everything else: an ancestor the principal cannot read is a gap, shown as nothing (not as "hidden"), and the walk does not pass through it.

Typed path queries the kernel offers (PROPOSED; from research input R1, section 3B), each over the mask:

| Query | Answers | Kernel |
|---|---|---|
| `ancestors(node, kinds, depth)` / `descendants(...)` | What is this built on? What rests on it? | closure |
| `supersede_head(node)` | What is the current answer, and how did it change? | chain walk |
| `paths(a, b, kinds, k, max_len)` | How is A connected to B? Up to k shortest typed paths (Yen-style over the bounded subgraph). | bounded BFS, CPU |
| `impact(node)` | Which documents depend on this one and may need a re-check when it is superseded? | reverse closure on a reverse CSR |
| `ppr(seeds)` | Which nodes the seeds jointly point at | 3A.4 |
| `subgraph(seeds)` | The scored neighbourhood (3A.5) | expansion plus ranking |

The pipeline uses these internally. They are also the agent's fallback tools (section 0.1), next to the plain search and read tools that agents always keep.

**Bounded traversal tools (PROPOSED; research PR #1037 section 3c).** A small, closed tool set, registered through doc 17's tool rules at Tier 0 (internal reads), with no free-form graph query language:

| Tool | Does | Bound |
|---|---|---|
| `graph_neighbours(node, kinds?)` | Typed neighbours of a node, with gists and edge kinds | `graph_retrieval.tools.max_results` (default 20) |
| `graph_path(a, b, kinds?)` | Up to k typed paths between two nodes | k at most 3, length at most 4 |
| `graph_ancestors(node, kinds)` / `graph_descendants(...)` / `graph_impact(node)` | Closure and reverse impact | `graph_retrieval.dag.max_depth` |
| `graph_current(node)` | Supersede head, with expired and stale labels | chain length cap |
| `zoom_in(cluster)` | The member gists of a cluster or scope summary (3A.6) | `graph_retrieval.tools.max_results` |

Rules: every call carries the caller's principal and the same mask, so a tool can never see more than injection could; each turn has a step cap (`graph_retrieval.tools.max_steps_per_turn`, default 8) and a cycle check over the nodes already visited in the turn, which ends the loop with a "cycle" result instead of repeating a node; results are gists and ids, and bodies are fetched by id with the normal read tool. This is a bounded ReAct-style traversal: the agent expands, inspects and chooses the next edge, inside fixed caps.

**Time validity (PROPOSED, research input R3).** Revisions carry a `stored_at` already. Adding `valid_from` and `valid_to` to revisions, and an effective time on `supersedes` edges, would let the pipeline answer "what was true on date D" and mark stale items in the pack. This is a schema change and is decision 14 in section 17; until then "current" means the head of the supersede chain.

### 3A.4 Ranking

Two methods, both PROPOSED, chosen by `graph_retrieval.rank.method`:

1. **`hop_decay` (default until S28):** today's rule, score = parent score x edge weight x 0.5 per hop, best path wins. It is the parity anchor with today's retriever.
2. **`ppr`: personalised PageRank (random walk with restart)** over the masked subgraph reached in stages 2 and 3. The restart vector is the normalised seed scores; transitions follow edges with probability proportional to the edge-kind weight; restart probability `graph_retrieval.ppr.alpha` (default 0.15, the textbook value, not tuned); a fixed iteration count (default 20) or an L1 change below `1e-6`, whichever comes first, so the cost is bounded. Masked nodes have no transitions in or out, so probability mass cannot flow through a hidden node. PPR rewards nodes reachable by many short paths from many seeds, which is the "graph reasoning" signal; hop-decay rewards only the single best path.

Closure nodes from 3A.3 get a fixed share of the budget (`graph_retrieval.dag.budget_share`, default 0.2) instead of competing on score, because a decision's ancestry matters even when its similarity score is low.

### 3A.5 Subgraph assembly

The output is a compact subgraph, not a bag of chunks:

1. Take the top-ranked nodes under the node cap.
2. Add the connecting paths: for each selected non-seed node, its best path back to a seed from the predecessors recorded in 3A.2 (a union of shortest weighted paths, an approximation of the Steiner tree that joins them). Path nodes the principal cannot read never exist in this subgraph, because the walk never entered them.
3. Order: DAG parts in topological order (the decision before what it decides, the plan before its tasks), other parts by score, grouped by the seed they hang off.
4. Each item carries: the node id and title; a **one-line gist**; **provenance** (author, source GC or message ids, trust tier, last change); and **why it was included** (for example "seed: lexical rank 3, vector rank 1", "2 hops via derived_from from <title>", "ancestor of decision <title>", "newest head of a supersedes chain").
5. Gists are deterministic by default: the front-matter `summary` if present, else the first sentence of the first block, truncated. Model-written gists are optional, written ahead of time as Vault content (doc 08 consolidation) and cached; a query never waits on a model to make a gist.

### 3A.6 Optional community and hub context

- Louvain communities are computed on the server for the Constellation (doc 19, "Clustering"). When `graph_retrieval.communities.enabled` is on, the pack may add a short note per community that the selected nodes fall in, and name the hub nodes (highest weighted degree inside the readable subgraph).
- **Security floor:** a community note is built only from nodes the principal may read. A precomputed community summary that was written over nodes of mixed scope is never shown to a principal who cannot read all of them. In practice this means summaries are computed per segment (one scope), or assembled at query time from the gists of readable members.
- Off by default; S28's evaluation decides whether it helps.
- **Hierarchical per-scope summaries (PROPOSED; research PR #1037 sections 3c and 4b).** Per scope (one project's shared area, the company tier) and per Louvain cluster inside a scope, a `summary` Vault note holds a short gist list of its members. Summaries are written ahead of time in a reader session (doc 08 section 7's pattern), never at query time, and never across scopes (the security floor above). When a member note changes, `impact(node)` (reverse closure) marks every summary that covers it `stale`; the maintenance job rewrites stale summaries in its next window, and a stale summary is shown with its label until then. A pack may carry a scope summary as one item instead of many member gists, and the agent opens it with `zoom_in(cluster)` (3A.3 tools).

### 3A.7 Optional model rerank

- When `graph_retrieval.rerank.enabled` is on, the top 25 to 50 candidates (`graph_retrieval.rerank.shortlist`, default 32; gists and titles only, already filtered) go through Bazaar.Server's gateway (doc 05) to a rerank model allowed by the requesting agent's whitelist. A small cross-encoder served by a local runtime on the server (an operator-added gateway upstream, possibly on the server GPU) is the expected first choice, per research input R2; a hosted model is equally allowed. Per-agent policy, quotas and usage labels apply as for any model call.
- The model can only reorder candidates. It cannot add a node, remove the scope filter or change what is readable.
- Never mandatory and never needed for correctness: a timeout (`graph_retrieval.rerank.deadline_ms`), an error, a quota stop or a gateway outage skips the step, and the pack says it was skipped.
- Taint: the rerank sees only content the requesting agent may already read, so it adds no new taint source; its output inherits the highest input taint as doc 15 requires.

### 3A.8 Packing

- Under the caller's token budget with `TokenBudgetPacker`. PROPOSED extension: two tiers, so many nodes appear as gist lines and only the top few carry full text. The current packer packs whole items; the tier change is part of WP0.
- Pack layout: the query; the assembled subgraph as ordered items with gist, provenance and reason; full text for the top tier; paths shown as compact chains of titles and edge kinds; then optional community notes; then diagnostics (backend used, ranking method, any skipped step, `lexical_only`, `approximate` if int8 was used).

### 3A.9 Which steps run where

| Stage | Multi-core CPU backend | GPU kernel | Notes |
|---|---|---|---|
| Lexical seeds | no (Postgres) | no | Postgres full-text on both OSes |
| Vector top-k | yes: `TensorPrimitives` over the in-memory matrix, one partition per core | yes (7.2 kernel 1 and 2) | The step most likely to gain from the GPU at large corpora |
| RRF fusion | yes, single thread | no | Tiny |
| Bounded expansion | yes: parallel frontier over the CSR | yes (kernel 3) | GPU likely pays only for large frontiers, deep caps or batches |
| DAG closure | yes | yes, the same BFS kernel restricted to DAG kinds | Chains are usually short; CPU is the expected default |
| Personalised PageRank | yes: parallel sparse matrix-vector product | yes (kernel 4) | Iterative and bandwidth-bound; a GPU candidate when subgraphs are large |
| Subgraph assembly, gists, ordering | yes | no | Branchy, small, CPU work |
| Louvain communities | offline on the server (doc 19), not per query | no (GPU Louvain out of scope) | |
| Model rerank | gateway | no | Optional |
| Packing | yes | no | |

The CPU backend is a first-class path: it is what runs on a server without a usable GPU (including Linux hosts with no Vulkan driver), and S28 may show it is the best default even where a GPU exists. Backend order when a step is declined: GPU, then CPU, then Postgres (today's path).

### 3A.10 The company knowledge tier in the pipeline

The tier is defined in doc 08 section 6; this is how each stage treats it.

- **Mask.** The company segment (`company/memory/`) is in the readable range set of every agent query unless the agent is quarantined or the call opts out (`IncludeCompany = false`). Candidates (`company/candidates/`) are never Active and never in the index.
- **Seeds (3A.1).** Besides the normal seed list, each query takes up to `graph_retrieval.company.max_seeds` (default 4, cap 10) seeds from the company segment alone, fused with the same RRF constants. So a strong company lesson reaches a project whose own notes dominate the main list, including a project that looks unrelated to where the lesson came from.
- **Expansion (3A.2).** Hops into the company tier follow ordinary `link` and `follows_precedent` edges from project notes. Company notes link only to company notes (doc 08 section 6.2), so a hop out stays inside the company segment. `company.evidence` is the provenance edge kind `evidence`, weight 0: like the provenance labels of 5.3 it is kept host-side for labels and is absent from the CSR, so it never expands and carries no PageRank mass. A backlink from a company note reaches a project note only when the mask lets the caller read it.
- **Closure (3A.3).** For a selected company note: its supersede head and history, its `contradicts` neighbours inside the company segment at depth 1, and its `conditioned_on` targets, inside `graph_retrieval.dag.budget_share`.
- **PPR (3A.4).** Company seeds join the restart vector at their fused scores. With no `evidence` edges in the CSR, mass from a company seed can reach a project node only through a backlink the caller can read, so PPR cannot route attention into another project's private notes.
- **Packing (3A.8).** Company items get a reserved share, `memory.context.company_share` (default 0.15, hard cap 0.3); an unused share returns to the general pool; reserved shares together stay at or below 0.5. Each company item carries the labels of doc 08 section 6.8: tier and trust (T2 provisional or T3 owner-promoted), company kind, confidence, evidence counts, conditions, review or expiry state, and the sources the caller can read, by title. Never raw project-private text.

### 3A.11 Proactive injection and refresh (section 0.1)

Triggers (each one runs the pipeline once, with the query built from the new context, not from the whole transcript):

| Trigger | Query and pinned seeds |
|---|---|
| Turn start | the triggering message plus the current task title (`constellation-memory-graph.md`, "Agent-turn wiring") |
| Task change (a Kanban card claimed, moved or finished) | the task title, its plan and dependency ancestry as pinned seeds |
| GC change (the agent's working GC switches, or a Thinking GC opens under it) | the GC topic and its recent decision references |
| New project (the agent joins a project or opens one for the first time) | the project's index and plan notes, plus a wider company-tier pass (`graph_retrieval.company.max_seeds` doubled, within its cap) so the agent starts with what the company already knows |
| Every trigger | company-tier seeds (3A.10) |

Refresh without bloating the window:

1. **Diff, not reload.** The runtime keeps the set of node ids and versions already in the window (`AlreadyInContext`). A refresh adds only new nodes and newer versions; a newer version replaces the older item in place and is marked as a replacement.
2. **Cards first, bodies by id.** Injected items are path-and-card bundles (gist, provenance, reason; 3A.5). Full bodies go in only for the top tier of the pack; the agent fetches any other body by id.
3. **Eviction.** Items that no later trigger re-selected for `graph_retrieval.inject.evict_after_triggers` (default 3) triggers drop out of the soft context first, oldest and lowest scored first, before anything else is cut. Before a stale tool result or injected item is cleared, anything the agent produced from it that should last is saved to the Vault first ("save to Vault before clear", research PR #1037 section 4b), so clearing never loses work.
4. **Budget.** Injection never exceeds the memory share of the turn budget (`memory.context.share_percent`); company and DAG shares sit inside it.

### 3A.12 Context efficiency and cache-aware packing (research PR #1037 section 4b)

- **Where prompt tokens are saved.** By retrieve-on-demand instead of preloading, by clearing stale tool results (with save to Vault before clear, 3A.11), by compact path-and-card bundles with bodies fetched by id (3A.5), and by per-scope summaries (3A.6). Index quantization does **not** remove prompt tokens; it only shrinks storage and speeds search (section 8.3).
- **Optional prompt compression.** An LLMLingua-2-style compressor may shorten bulky prose items, as an optional sidecar behind the gateway (`graph_retrieval.compress.enabled`, default off). Never on code, ids, paths, decision records or quoted text. Off until the evaluation (13.5) shows it keeps answer quality.
- **Cache-aware order.** The stable prefix comes first and in a fixed order: rules, Soul, Constitution, pinned cards. The provider's cache breakpoint sits after it. The retrieved bundle comes after the breakpoint, in a stable order (DAG parts topologically, the rest by score with id as the tie key), so two turns with the same bundle hit the same cache and a refresh changes only the tail.
- **CPU and GPU fit.** CPU for binary, int8 and RaBitQ-style codes and for the graph kernel (section 4); the GPU is the better place for embeddings, the reranker and a compressor, which are gateway upstreams, not this module (decision 8 in section 17). cuVS and cuGraph are NVIDIA-only and the owner's card is an Arc B580 (R5).

## 3B. Research inputs

Source: `docs/research/reports/graph-context-long-horizon-memory.md` (status RESEARCHED, 2026-11-20; on branch `claude/project-thread-m53emz`, draft PR #1037, not yet on `main`). Research, not decisions. Further inputs from that thread are added here with their source and folded into 3A only through a reviewed edit.

| # | Research recommendation | How this doc uses it | Agree? |
|---|---|---|---|
| R1 | Graph source is the Vault's own links (`Link`, `DerivedFrom`, `Supersedes`); no model-based entity extraction in v1. Typed path queries: ancestors, descendants, supersede head, k-shortest paths, reverse impact, PPR. Return a structured bundle of paths plus cards, not flat snippets. | 3A.3 path queries, 3A.5 assembly, 5.3 edge sources. | Yes, with one disagreement: the report says `DerivedFrom` and `Supersedes` cannot form a cycle because they point back in time. They are front-matter references to ids, written by agents and people, and nothing in the schema or the projector enforces time order; a rollback (`ActivateRevisionAsync`) can also bring back an older edge set. The kernel therefore keeps a visited set and reports cycles on every kind. Enforcing acyclicity at write time is possible later, but it is a Vault write rule, not a retrieval one. |
| R2 | Keep RRF seeds, 1 hop default, 2 max. Add a local cross-encoder reranker over the top 25 to 50. | 3A.1 and 3A.2 keep today's defaults; 3A.7 rerank over a 32-item shortlist. | Partly. Seeds and hop limits: yes. Rerank: useful, but this doc keeps it off by default and never mandatory until the labelled set (R6) shows a gain on Bazaar's own Vault, because the owner direction makes model steps optional and a rerank adds latency and a gateway dependency to every retrieval. It goes through the gateway so per-agent whitelists and usage labels apply, rather than a separate inference path. |
| R3 | Add `valid_from` and `valid_to` to revisions and `Supersedes` edges (Zep/Graphiti style). | 3A.3 "Time validity". | Yes in principle. It is a schema change, so it needs one answer (decision 14 in section 17) and lands with the convergence docs PR (WP0), not here. |
| R4 | Cross-project hops only via grants and Hive copies; access checked per hop at read time with the caller's identity; summaries per access scope, never across; dangling cross-scope links return nothing. | Section 0 items 2 and 3, 5.3 cross-project edges, 3A.6 community floor, 3A.10, section 10. | Yes for project-to-project hops. The owner's answer (2026-11-20) adds the company knowledge tier as the cross-project channel, readable by every agent; it adds no project-to-project hop. The range mask is the per-hop check on the engine; the CPU post-filter re-checks with the caller's identity. |
| R5 | Engine: `IGraphKernel` over a CSR snapshot, multi-core CPU reference first, optional GPU provider modules. cuVS, CAGRA and cuGraph are CUDA-only and the owner's card is an Arc B580, so GPU paths are optional and of the Vulkan or oneAPI/SYCL/OpenCL class. GPU is best spent on embeddings, the reranker and any graph scorer. | 4.1 (CPU engine in the main module, Vulkan as an optional provider module), 4.2 (`IGraphKernel`), 3A.9. | Mostly. Agree on the interface, the CPU default and optional providers. Two differences: (a) a cuGraph or cuVS provider cannot be first-party, because it needs the proprietary CUDA runtime and the public build is OSI-only; a third party could ship one under the module exception. (b) For Intel this doc picks Vulkan over SYCL or OpenCL, to keep one GPU API in the codebase (7.1). On where the GPU is best spent: plausible, and this doc does not dispute it, but embedding and rerank inference are out of this doc's scope (decision 8 in section 17). The GPU vector and graph kernels stay behind S28. |
| R6 | Build a labelled Vault question set (path, impact, current answer, isolation) before tuning weights. | 13.5 and WP16. Edge weights, PPR alpha, closure budget share and rerank stay at their starting values until it exists. | Yes. |
| R7 | Context efficiency (section 4b): prompt-token cuts come from retrieve-on-demand, clearing stale tool results with save to Vault before clear, compact path-and-card bundles with bodies by id, per-scope summaries, and optional prompt compression (LLMLingua-2, never on code, ids or decisions). Cache-aware packing: stable prefix first, retrieved bundle after the cache breakpoint in a stable order. | 3A.6 summaries, 3A.11 refresh, 3A.12. | Yes. Compression stays off by default until the evaluation shows no quality loss. |
| R8 | Index quantization (TurboQuant, RaBitQ, PQ, binary or int8 with rescore, Matryoshka) shrinks storage and speeds search but removes no prompt tokens. The Vault index is small (about 4 GB per million notes at 1024 f32 dimensions), so quantization is headroom, not v1: int8 or Matryoshka with full-vector rescore first, TurboQuant and RaBitQ benchmarked in S28. | 8.3, 13.2 arms. | Yes. int8 with rescore is already the opt-in mode; Matryoshka truncation needs a profile whose model supports it, so it is an S28 arm only for such a profile. |
| R9 | Wider project scan (section 3b): nothing is a drop-in (all Python). Borrow ideas and reimplement in C#: HippoRAG personalised PageRank (MIT), Graphiti temporal validity windows (Apache-2.0), LightRAG incremental updates (MIT), Cognee no-LLM build mode (Apache-2.0). Optional sidecar: LLMLingua (MIT) for bulky prose only. Skip FalkorDB (SSPL, not OSI), Mem0, Letta, MS GraphRAG, nano-graphrag. | 3A.4 PPR (HippoRAG idea), 3A.3 time validity (Graphiti idea), 9.1 delta apply (LightRAG idea), the deterministic build with no model call (Cognee idea), 3A.12 compressor. | Yes. Ideas only: no code is imported. MIT and Apache-2.0 are AGPL-compatible, but the licences were read from project About boxes, not LICENSE files, so each is **unverified** until a licence check reads the file. |
| R10 | Agentic GraphRAG patterns (section 3c; practitioner writing, its percentage claims are untested and not used as evidence): an institutional knowledge map of decision, precedent, incident and role notes; write-back of a link when acting under a precedent; failure memory with counts and time, a path marked bad only after repeats; hierarchical summaries with `zoom_in`; bounded ReAct-style traversal over typed-edge tools with step caps and cycle detection, no free-form query language; "who resolved a similar failure before" for Kodo and CI. | Doc 08 section 6 (the company tier is the map; write-back; failure repeats), 5.3 edge kinds, 3A.3 tools, 3A.6 summaries. | Yes, with one limit: "who resolved this before" is an owner-visible suggestion only, because people edges are personal data (C01); no person edge enters agent retrieval. |

## 4. Module shape and the seam

### 4.1 Projects (PROPOSED)

| Project | Holds | References |
|---|---|---|
| `Bazaar.Server.GraphRetrieval.Abstractions` | `IGraphKernel`, request and result records, capability and decline-reason enums, the index-feed contract. No implementation. | Bazaar.Core only |
| `Bazaar.Server.GraphRetrieval` | The pipeline and the default engine: CSR snapshot and index builder, delta feed, the multi-core CPU `IGraphKernel` (the default engine and the reference for GPU providers), engine selection and breaker, shadow parity checker, Kodo `IModuleHealth`, settings, component. | its Abstractions, `Bazaar.Kodo.Abstractions`, `Bazaar.Server.Settings.Abstractions`, `Bazaar.Vault.Abstractions`, Npgsql |
| `Bazaar.Server.GraphRetrieval.Vulkan` | Optional GPU provider module: an `IGraphKernel` on Vulkan compute (sections 5 to 7), its own residency and device-loss handling. Installed and enabled by the operator. | the Abstractions only, the shared Vulkan library (4.4) |
| `Bazaar.Server.GraphRetrieval.Tests` | Unit, property and parity tests, and the single-threaded oracle (section 14). | the modules |
| `Bazaar.Server.GraphRetrieval.Bench` | Spike S28 harness (section 13). | the modules |

Module ids: `graph-retrieval` (hard dependency on the `pgvector` Deps module, doc 42, because the index is built from pgvector tables) and `graph-retrieval-vulkan` (hard dependency on `graph-retrieval`, soft dependency on a Vulkan device). The pipeline itself (the order of stages, scoring rules, assembly, gists, reasons and packing in section 3A) stays in the Constellation retriever, as the DECIDED placement requires; these modules supply compute only. Other GPU provider modules can be added behind the same interface later; section 7.1 explains why only an OSI, cross-vendor provider is first-party.

### 4.2 The seam

`MemoryGraphRetriever` (and later the Vault vector search) takes an optional `IGraphKernel` from the Abstractions project. The default registration is a null kernel that declines every call. The retriever's code path is:

```csharp
// Bazaar.Server.GraphRetrieval.Abstractions (PROPOSED shape)
public interface IGraphKernel
{
    KernelCapabilities Capabilities { get; }

    // Vector top-k over the segments the caller has already resolved as readable.
    ValueTask<KernelResult<VectorTopK>> TryVectorTopKAsync(VectorTopKRequest request, CancellationToken ct);

    // Reachability by hop level (with best predecessor) from the seeds, never entering a node outside the mask.
    ValueTask<KernelResult<ExpansionLevels>> TryExpandAsync(ExpansionRequest request, CancellationToken ct);

    // Ancestor and descendant closure along DAG edge kinds, inside the mask.
    ValueTask<KernelResult<ExpansionLevels>> TryClosureAsync(ClosureRequest request, CancellationToken ct);

    // Personalised PageRank over a masked subgraph, fixed iteration budget.
    ValueTask<KernelResult<NodeScores>> TryRankAsync(RankRequest request, CancellationToken ct);
}

// Implementations: CpuGraphKernel (in Bazaar.Server.GraphRetrieval, the default) and
// VulkanGraphKernel (in the optional Bazaar.Server.GraphRetrieval.Vulkan provider module).
// A composite tries them in the configured order; when all decline, the retriever runs today's Postgres path.

public readonly record struct KernelResult<T>(T? Value, DeclineReason Reason, long IndexWatermark);
public enum DeclineReason { None, Disabled, NoDevice, NotResident, IndexBehind, Deadline, DeviceLost, BreakerOpen, Unsupported }
```

Rules (PROPOSED):

1. **The existing retriever is the specification.** A served result must equal the CPU and pgvector result within the tolerances in section 8. Any difference is a bug in the accelerator. "The existing retriever" means `MemoryGraphRetriever`'s rules applied over the converged Constellation node set (section 1.1). Until the convergence lands, the parity tests run the current `MemoryGraphRetriever` over a fake store that serves Vault documents as nodes.
2. **Decline, never throw.** The graph kernel returns a decline reason instead of throwing for every expected failure. The retriever then runs today's path. A thrown exception is also caught by the retriever and treated as `DeviceLost`.
3. **The retriever keeps the authority.** The graph kernel returns indices and hop levels only. The retriever maps them to ids, loads revisions, runs `IVaultAccess.CanRead` on every candidate, recomputes scores, applies the Active-only rule and packs. Section 10 explains why the check runs twice.
4. **No ids, text or paths cross into GPU buffers.** Section 10.1.

The seam itself is a few lines in `Server.Constellation`. Doc 40 records that Constellation is still a folder inside `Bazaar.Server` (finding 5), so this seam sits there until Constellation moves out. That is decision 2 in section 17.

### 4.2.1 Implementation notes: the CPU engine (built)

Status: implemented in `Bazaar.Server.GraphRetrieval` for expansion, closure, PPR and subgraph assembly. Vector top-k, the index builder from Postgres, the delta feed, engine selection, the breaker, the shadow checker and Kodo health are later work packages and are not part of this change. Where this doc is silent the choices below apply; each is a field of `CpuGraphKernelOptions` or of the request, with the safe default shown.

- **Dependencies.** Both projects depend on the BCL only (the Abstractions project does not reference `Bazaar.Core`, unlike the table in 4.1), so the engine is pure logic. The interface drops `TryVectorTopKAsync` for now (no embedding matrix yet) and adds `TryAssembleSubgraphAsync`; `DeclineReason` gains `InvalidRequest` for malformed input (bad seed index, negative score, decay outside (0, 1], mask length not equal to the node count). A kernel still never throws for these.
- **Node ids.** `GraphSnapshotBuilder` maps document ids to dense ints in ordinal order of the id string, and sorts and de-duplicates edges per (source, target, kind), keeping the largest weight, so one graph always gives one snapshot. Ties everywhere are broken by that dense id. Weights are stored as `float` (GPU friendly) and combined in `double`.
- **Per-edge weight.** The snapshot stores a weight per edge (default from the 5.3 table, `GraphEdgeWeights.Default`) rather than one shared lookup table, so a host can pass its own; a weight of 0 is never followed in expansion or PPR (this is how `contradicts`, `evidence` and zero-weight edges stay out). Closure ignores weights (it is structural).
- **Expansion.** Hop-limited best path: score = parent score x edge weight x `HopDecay` (default 0.5) per hop. A node's `Level` is the hop count of its best path, and a later, longer path replaces an earlier one only when strictly better. Seeds keep their own score and are never improved. Deterministic merge: candidates from fixed chunks of the frontier merge under the total order score descending, predecessor ascending, kind ascending. Fan-out caps: `MaxEdgesPerNode` (default 2,000, order weight descending then node id) and `MaxReached` (default 100,000; new nodes admitted by score then node id). `TracePath` returns the exact path behind a score from the kept improvement history.
- **Direction.** `Outgoing`, `Incoming` or `Both`. With `Both`, incoming edges are followed at `ReverseWeightFactor` (default 0.5, decision 12: backlinks at half weight).
- **Closure.** Breadth-first along `EdgeKindSet.Dag` by default (`derived_from`, `supersedes`, `conditioned_on`, `follows_precedent`, `resolved_by`), `Outgoing` for ancestors and `Incoming` for descendants, with `MaxDepth` (default 8) and `MaxNodes` (default 10,000). The visited set stops at repeats; cycles are reported as the sorted nodes of every non-trivial strongly connected component and every self loop, never as an error.
- **PPR.** Restart vector is the normalised seed scores (non-positive seeds dropped); transition probability is proportional to edge weight; `Alpha` 0.15, `Iterations` 20, `Tolerance` 1e-6 (L1) as in 3A.4. A node with no eligible out-edge returns its mass to the restart vector (the standard dangling rule; the doc did not specify it). The walk covers the nodes reachable from the seeds inside the mask and the optional `Subgraph` restriction. Each target sums its sources in a fixed order and chunk partials are added in chunk order, so scores are bit-identical for any thread count.
- **Subgraph assembly.** Pipeline: expansion, then closure from the seeds (optional), then ranking (`HopDecay` or `Ppr`, the latter over the nodes reached by both), then selection of at most `NodeCap` nodes. `ClosureShare` (default 0.2) of the cap is reserved for closure nodes, shallowest first; the rest goes to the best-scoring nodes. Closure-only nodes score 0 under `HopDecay`. Each selected non-seed node carries its justifying path (expansion path, else closure path), and the ordered DAG-topological layout of 3A.5 step 3 stays a pipeline concern in the retriever.
- **Threads.** `MaxDegreeOfParallelism` defaults to cores minus 2 (at least 1), as `graph_retrieval.cpu.threads`; `ChunkSize` (default 1,024) never changes results.
- **Tests.** `GraphKernelConformanceTests` is the abstract suite a GPU provider derives from; `Oracle` is the single-threaded reference of section 14. `Benchmark` runs expansion plus PPR on a 1M-edge synthetic graph when `BAZAAR_GRAPH_BENCH=1`.

### 4.3 What the graph kernel replaces, and what it does not

| Retriever step | Today | With the graph kernel |
|---|---|---|
| Vector seeds | pgvector exact scan in SQL with the access clause | GPU top-k over readable segments, plus a margin (8.2) |
| Lexical seeds | Postgres full-text | unchanged |
| RRF fusion | CPU `RrfFusion.Fuse` | unchanged |
| Expansion | one `GetActiveAsync` round trip per hop, CPU loop | GPU breadth-first levels with a mask; CPU replays the scoring |
| Visibility | `IVaultAccess.CanRead` per candidate | mask on the GPU **and** `CanRead` per candidate on the CPU |
| Revision load | per hop | once, for the final candidate set |
| Packing | `TokenBudgetPacker.Pack` | unchanged |

An honest note on expected gains: with `TopK` 20 and at most 2 hops of hop-decay, the expansion frontier is small. Most of today's expansion cost is likely the Postgres round trips, not arithmetic, and the multi-core CPU backend may capture most of that gain. The GPU becomes more plausible for the heavier stages of section 3A (personalised PageRank over large subgraphs, deeper caps, batches of queries) and for vector search, which is a bandwidth-bound scan over millions of rows. S28 measures the CPU backend next to the GPU backend for every stage (section 13), and a GPU kernel ships enabled only for stages where it beats the CPU backend.

For the new stages (DAG closure, PPR, assembly) there is no Postgres implementation to compare with. Their reference is a single-threaded, plainly written C# oracle in the test project; both backends must match it (section 8).

### 4.4 Reuse of the Vulkan code

`Bazaar.Constellation.Vulkan` is a client renderer library. It references `Bazaar.Constellation.Rendering`, which the server must not take. What is reusable is small: loader initialisation, instance creation at the highest supported version (`VulkanProbe.TargetApiVersion` 1.4), device enumeration, and the pattern of committed GLSL plus SPIR-V embedded as resources.

PROPOSED: extract a feature-free infrastructure library, `Bazaar.Gpu.Vulkan`, holding the probe, device selection, buffer and memory helpers, and a compute-pipeline helper. Both `Bazaar.Constellation.Vulkan` and `Bazaar.Server.GraphRetrieval` reference it. It holds no feature, so it does not break the module rule. The extraction is its own small PR with no behaviour change for the renderer. The probe's Intel driver-version decode bug noted in `constellation-scale-and-lod.md` section 10 is fixed in the same PR. Owner decision 3 asks whether this library is acceptable or the module should keep its own copy.

## 5. Data layout on the GPU (PROPOSED)

All GPU buffers are device-local storage buffers unless stated. Indices are `uint32`, which caps one index at about 4.29 billion rows and edges, far above the VRAM limits in section 6.

### 5.0 Node set

Nodes are Constellation nodes (section 0): one graph node per `vault_docs.doc_id`, of every Vault document type, in every project the install holds. Memory notes are included because they are Vault documents (`type = memory`), not because a memory table lists them. Phase 1 holds Vault document nodes and document-to-document edges only. GC, decision-reference, task and PR nodes from doc 19 are phase 2, because their visibility rules (DM membership, Thinking GCs) are not Vault path rules and need their own mask source (decision 11 in section 17).

### 5.1 Segments

The index is cut into **segments**. A segment is a contiguous range of rows that share one access scope, taken from the `vault_docs` columns `IVaultAccess` and the SQL access clause already use: `area`, `project` and, for private memory and charters, `owner`. Examples: one agent's private area, one project's shared area, the company area, the Hive. The segment table is small and lives in host memory, with a copy on the GPU as `(first_row, row_count, segment_id)` triples. A document whose path moves to another area or project is removed from the old segment and appended to the new one.

### 5.2 Embedding matrix

- Row-major `N x D`, with rows grouped by segment and `D` the profile dimension padded up to a multiple of 16 with zeros (zeros do not change a dot product or a norm).
- **fp16 mode (default):** the stored values are bit-identical to the `halfvec` values in Postgres, read as binary. A per-row fp32 norm is computed once on the GPU at load time.
- **int8 mode (opt-in):** per-row symmetric int8 with an fp32 scale, used only as a first pass. Section 8.3.
- Rows are blocks (`vault_block_embedding` is per block). A host-side map from row index to `(doc_id, ordinal, profile_id)` and from row to its graph node (the `doc_id`) stays in host memory and never goes to the GPU. Because the block's `doc_id` is the node key after convergence, no separate block-to-note mapping is needed.

### 5.3 Graph (CSR)

- `row_offsets: uint32[N_nodes + 1]`, `col_index: uint32[E]`, `edge_kind: uint8[E]` padded to 4 bytes per edge group. The weight is looked up from the kind through one weight table shared with the CPU retriever, so weights cannot drift.
- Edge sources: `vault_links` rows with a resolved `target_doc` (kind `link`, and `embed`), and the front-matter relations. Today's weights carry over: `link` 1.0 (today's `Link`), `derived_from` 0.8, `supersedes` 0.5. Today's memory retriever expands outgoing edges only, while `NeighbourCardsAsync` reads both directions; decision 12 in section 17 adds backlinks at half weight.
- **Closed set of typed edges (DECIDED by the coordinator, 2026-11-20, from the research thread; weights PROPOSED).** Beyond `link`, `derived_from` and `supersedes`, exactly five relation kinds are added, written as front-matter relations: `conditioned_on`, `approved_by`, `follows_precedent`, `resolved_by`, `affects`. The schema stays lean: detail lives in note bodies, and a new kind needs a docs change, not a setting (no ontology sprawl). The full table:

| Kind | Meaning | Direction expanded | Starting weight | DAG kind (3A.3) | Notes |
|---|---|---|---|---|---|
| `link` | a wiki-link | out; backlink at half | 1.0 | no | today's `Link` |
| `embed` | a transclusion | out | 1.0 (as `link`) | no | |
| `derived_from` | built from | out | 0.8 | yes | today's value |
| `supersedes` | replaces | out | 0.5 | yes | today's value; chain walk to the head |
| `contradicts` | disagrees with | closure only, depth 1 | none (not expanded) | no | shown beside its target, doc 08 section 2 |
| `conditioned_on` | holds only while the target holds | out | 0.8 | yes | a precedent's conditions |
| `follows_precedent` | was done under this precedent | out | 0.8 | yes | the write-back edge (doc 08 section 6.6) |
| `resolved_by` | an incident or failure resolved by a fix, decision or charter | out | 0.6 | yes | never targets a person |
| `affects` | changes or bears on the target | out; reverse for `impact` | 0.6 | no | feeds `impact(node)` |
| `approved_by` | approved by a decision record, motion or charter | out | 0.3 | no | targets a decision, a Council motion record or a role charter only. A person approver stays a person reference in the note (C01), visible to the owner, and is never a graph edge |
| `evidence` | company note to its source note (doc 08 section 6) | never | 0 | no | provenance only; host-side, absent from the CSR |
| provenance labels (`AuthoredBy`, `FromGc`, `FromMessage`) | who and where from | never | 0 | no | today's rule |

  The new weights are starting values, untuned; the labelled question set (13.5) replaces them. **Precedents carry review or expiry dates:** a precedent note has `review_by` and may have `valid_to`; retrieval flags a precedent past either date as `stale` or `expired` and uses an expired one only as labelled history (3A.3). Edges from a note to a person never exist in phase 1; GC and person nodes are phase 2 (5.0) with their own mask source.
- Unresolved links (`target_doc` null) produce no edge, matching the projector rule. Provenance labels never expand and are left out.
- A node bitset `node_flags: uint32[ceil(N_nodes / 32)]` per flag: `active` (for memory documents, the active revision's status is `Active`; for other documents, the document exists and is not deprecated or archived by its front matter, by the converged spec) and `tombstone` (removed, erased or moved).
- **Cross-project edges.** A link whose target is in another project is stored like any edge, and the range mask decides at query time whether the hop is allowed, so a hop across projects happens only when the principal may read the target segment under today's rules (Hive copies, role grants). The company segment is readable by every agent that is not quarantined, so hops into it are always allowed; hops out of it stay inside it (3A.10). This doc adds no project-to-project grant.
- Graph node order follows the same segment grouping, so the visibility mask in section 10 is a list of ranges, not a per-node upload.

### 5.4 Per-query buffers

Query vector (fp16 or fp32), the readable segment ranges, seed indices, and output buffers for top-k and hop levels. They come from a pool and are zeroed when returned to it.

## 6. Memory budget (calculated, not measured)

Bytes per million vectors, embedding matrix only:

| Dimension | fp16 | int8 plus scale |
|---|---|---|
| 768 | 1.43 GiB | 0.72 GiB |
| 1024 | 1.91 GiB | 0.96 GiB |
| 1536 | 2.86 GiB | 1.43 GiB |

Graph: about 8 bytes per edge (index plus kind with padding) and about 4.25 bytes per node (offset plus flags), so 10 million nodes with 50 million edges is roughly 0.4 GiB.

What fits on the owner's Arc B580 (11.9 GiB device-local heap, measured by the probe), at the default VRAM budget of 4 GiB (setting in section 12) and `D` = 1024:

| Mode | Vectors at 4 GiB budget | Vectors at 10 GiB (whole card, no renderer) |
|---|---|---|
| fp16 | about 2 million | about 5 million |
| int8 | about 4 million | about 10 million |

So "tens of millions of vectors" does not fit on this card in either mode. The design handles that explicitly instead of hoping:

1. **Residency by segment.** Segments are loaded whole, most-used first, until the budget is reached. A query is served on the GPU only when **every** segment the principal may read is resident. Otherwise the whole query declines with `NotResident` and runs on pgvector. Mixing a GPU part and a Postgres part of one query is rejected, because it doubles the parity surface.
2. **Eviction.** Least recently queried segments are evicted when a load would exceed the budget. Eviction only changes speed, never results.
3. **No silent oversubscription.** The module allocates against its own budget and checks `VK_EXT_memory_budget` when present. It never relies on the driver paging device memory to system RAM (the B580 reports `pageableDeviceLocalMemory`), because paging makes latency unpredictable.
4. **Shared card.** On the owner's PC the server and the desktop client share one GPU, and the client renderer has its own 1.5 GiB PC cap (`constellation-scale-and-lod.md`). The default budget leaves room for it.
5. **Larger corpora** (int8 resident plus fp16 rerank fetched from host memory, or a partitioned approximate index) are out of scope until S28 shows the need. They are listed in section 17.

## 7. Compute stack (PROPOSED: Vulkan compute with SPIR-V)

### 7.1 Options compared

| Option | Cross-vendor | Windows and Linux | .NET fit | Licence | Notes |
|---|---|---|---|---|---|
| **Vulkan compute, GLSL compiled to SPIR-V, Vortice.Vulkan** | Yes (Intel, AMD, NVIDIA, Mesa) | Yes | Same binding as the renderer; plain P/Invoke | Vortice.Vulkan MIT; glslang and shaderc OSI (BSD-3 and Apache-2.0) | Reuses the existing stack and the measured B580 driver. More boilerplate than a kernel language. Software devices (Mesa lavapipe, SwiftShader) can run the same SPIR-V in CI. |
| Vulkan compute, Slang compiled to SPIR-V | Yes | Yes | Same as above | Slang is OSI-licensed (to verify in the spike) | Nicer kernel language with generics; adds a compiler dependency. Could replace GLSL later without changing the runtime. |
| OpenCL through ILGPU or raw bindings | Mostly; Intel Arc supports OpenCL | Yes | ILGPU writes kernels in C# | ILGPU is NCSA-licensed (OSI) | Attractive for C# kernels. A second GPU API beside Vulkan; OpenCL driver quality varies by vendor and OS. Kept as the fallback if Vulkan compute proves too costly to write. |
| ComputeSharp (HLSL from C#) | Yes, on D3D12 | Windows only | Excellent | MIT | Fails the Linux requirement. Rejected. |
| CUDA, cuVS, Faiss GPU | NVIDIA only (Faiss GPU also ROCm) | Yes | Native interop | Open source | Not cross-vendor, and the owner's card is Intel. Rejected. |
| SYCL or oneAPI Level Zero | Intel first | Yes | C++ only, native shim needed | Open source | Ties the server to one vendor's stack. Rejected. |
| ONNX Runtime with DirectML or another execution provider | Varies | DirectML is Windows only | Good | MIT | A matrix product is easy, graph expansion and per-segment top-k are not. Rejected for this use. |
| WebGPU (wgpu or Dawn) | Yes | Yes | Native shim | Open source | Doc 19 rules out WebGPU for the renderer. Not reopened here. |

Choice (PROPOSED): **Vulkan compute with SPIR-V compiled from GLSL**, on Vortice.Vulkan, through the shared `Bazaar.Gpu.Vulkan` library. Reasons: one GPU API in the codebase, a binding already pinned and measured on the owner's card, cross-vendor and cross-OS, OSI-only, and CI can run the real shaders on a software device. The cost is more host code (descriptor sets, barriers, timestamp queries) than a kernel language would need. Slang is the first alternative if GLSL becomes the bottleneck for writing kernels.

### 7.2 Kernels

1. **Distance and partial top-k.** One workgroup per tile of rows inside the readable ranges. Dot product accumulated in fp32 from fp16 inputs, divided by the norms, giving cosine similarity `1 - distance`, the same quantity the SQL computes. Each workgroup keeps a local top-k' (k' = k plus the margin) using subgroup operations.
2. **Merge.** Reduces the partial lists to one top-k' list, ordered by score, then by the row's position in pgvector's tie order (`doc_id`, `ordinal`), which the host encodes into a sort key at load.
3. **Expansion.** Level-synchronous breadth-first search from the seed indices for at most `MaxHops` levels. A node is enqueued only when its `active` bit is set, its `tombstone` bit is clear and it falls inside a readable range. The output is a level per reached node, not a score. The same kernel with an edge-kind filter computes DAG closure (3A.3), with a visited bitset so cycles terminate.
4. **Personalised PageRank.** Power iteration as a sparse matrix-vector product over the CSR restricted to the reached, masked subgraph, fp32 accumulation, a fixed iteration count, and a reduction for the L1 change per iteration. Masked nodes have zero rows and columns.

Steps the GPU does not do: path reconstruction, ordering, gists, provenance and packing (small, branchy CPU work, section 3A.9).

Capability checks at device creation: Vulkan 1.1 subgroups, `shaderFloat16` and 16-bit storage, `shaderInt8` for int8 mode, `VK_EXT_memory_budget`. Optional extensions (integer dot product, cooperative matrix) are used only behind a check with a plain shader as the fallback, following doc 19's rule for mesh shading. Support on the B580 is to be confirmed by the probe in S28, not assumed.

### 7.3 Shader build

The repo today commits `.glsl` with the compiled `.spv` and embeds the `.spv` as resources. I found no build step that compiles them. This module follows the same pattern and adds one CI check: recompile each `.glsl` with a pinned compiler and run `spirv-val`, failing when the committed `.spv` is stale or invalid.

## 8. Precision and exactness (PROPOSED)

### 8.1 What "identical" means

The reference is exact pgvector over the same `halfvec` values, with the same access clause, plus today's `MemoryGraphRetriever` for expansion.

| Output | Required parity in fp16 mode |
|---|---|
| Vector top-k ids | The same set and order as pgvector, except where two scores differ by less than the tolerance; inside that band the order follows pgvector's tie key. |
| Vector scores | Absolute difference at most `1e-4` from pgvector's `float8` score. Dot products accumulated in a different order differ at about `1e-6` relative in fp32; the tolerance is set well above that and is checked by the parity tests, not assumed. |
| Expansion | Identical: the same reached set and hop levels. This is set arithmetic with no rounding. |
| Final scores and order (`hop_decay`) | Identical, because the CPU recomputes every score and best predecessor in `double` from the seeds and levels (8.4). |
| DAG closure | Identical sets and levels (set arithmetic). |
| PPR scores (`ppr`) | Both backends against the C# oracle: absolute difference at most `1e-5` per node after the fixed iteration count. Final order is decided on the CPU: when the reached subgraph is at most `graph_retrieval.ppr.cpu_replay_max_nodes` (default 50,000), the CPU reruns PPR in `double` from the GPU's subgraph and its result is used, so order is exact; above that, scores within `1e-5` are ordered by node id. |
| Assembled subgraph and pack | Identical, given identical ranked inputs. |

The multi-core CPU backend is held to the same table. Parallel reductions on the CPU use a fixed partition and a fixed combine order, so its results do not depend on the thread count.

### 8.2 The margin at the k boundary

Rounding can swap two rows whose scores straddle the k-th place. The GPU therefore returns `k + margin` candidates (default margin 16). The CPU re-sorts them by score with the tie key and cuts at k. A swap could still happen only if more than `margin` rows sit inside the tolerance band at the boundary, which the shadow checker (section 9.3) counts.

### 8.3 int8 mode

int8 is not exact. It is a first pass that selects `k x oversample` rows (default 4x), which are then rescored in fp16 on the GPU from the resident fp16 rows, or from host memory if only int8 is resident. Recall@k against exact pgvector is measured in S28. int8 mode is never the default, needs the operator to opt in, and carries a `approximate: true` label in the retrieval result so callers and logs can tell. If S28 shows recall below the threshold in section 13.4 the setting is removed rather than shipped.

Quantization is headroom, not a v1 need (R8): the Vault index is small, about 4 GB per million notes at 1024 f32 dimensions, and quantization saves storage and search time but no prompt tokens. Order of work: int8 with full-vector rescore (this section) first; Matryoshka truncation with full-vector rescore where the install's embedding model supports it; RaBitQ and TurboQuant only as S28 benchmark arms on the CPU engine, adopted only on measured gains.

### 8.4 Why the CPU replays the scoring

The retriever's expansion rules have details that are easy to get wrong in a kernel: seeds keep their fused score; a hop target's score is the best parent score times weight times 0.5 within one round; a later, higher-scoring path updates the score of an expansion hit but does not re-expand it and keeps the smaller hop distance (`constellation-memory-graph.md`, open item a); proposed targets are taken in ordinal id order. The GPU only answers "which nodes are reachable at which level, inside the mask". The CPU then runs the existing scoring loop over that small set with no Postgres round trips between hops. This keeps one implementation of the scoring rules and makes final scores bit-identical.

## 9. Index lifecycle and failure handling (PROPOSED)

### 9.1 Build and incremental updates

- **Cold build.** At start, and when enabled, the module streams the embedding rows for the install profile and the active graph revisions from Postgres in segment order, uploads them through a staging buffer, and records the **watermark** it built to (the highest change sequence seen, 9.2). Queries decline with `IndexBehind` until the first build finishes.
- **Change feed.** Producers emit changes after they commit: the Vault indexer when it writes `vault_docs`, `vault_blocks` and `vault_links` for a commit (doc 18), the memory revision store for activations and rollbacks (until convergence removes it as a separate path), and the embedding writer (`VaultEmbeddingRepository.UpsertAsync`, `DeleteForProfileAsync`). The feed is a small append-only table (`graph_retrieval_change`, owned by this module, with a sequence number) plus Postgres NOTIFY as the wake-up. A table, not only NOTIFY, because NOTIFY is lost when no listener is connected.
- **Apply.** Additions and edge changes go to a small delta area on the GPU (appended rows, a delta CSR) and are merged into the main buffers by a background compaction. Removals, tombstones, status changes away from `Active`, and moves between segments flip bits in `node_flags` at once.
- **Profile change.** `DeleteForProfileAsync` or a new profile drops the whole index and rebuilds. Profiles never mix (doc 08 section 9).

### 9.2 Freshness

Each query reads the current change sequence from Postgres (one cheap read, which the retriever already pays for with its store calls). If the GPU watermark is behind it, the graph kernel applies pending changes first when they are few (setting `graph_retrieval.max_pending_apply`), and otherwise declines with `IndexBehind`. This is how fresh notes appear in results at once, the same as on the CPU path.

**Security floor (not a setting):** a removal, erasure, scope move or status change away from `Active` is never served stale. If such a change is pending, the query declines until it is applied. When crypto-erase lands (doc 48, not built), erasure completion must wait for the graph kernel to acknowledge the tombstone, or for the graph kernel to be disabled.

### 9.3 Device loss, driver failure and the breaker

| Event | Detection | Response |
|---|---|---|
| Query exceeds its deadline | host timer around the fence wait | decline `Deadline`; the query runs on pgvector; count a failure |
| `VK_ERROR_DEVICE_LOST`, or a Windows TDR reset | Vulkan result codes | decline `DeviceLost`; tear down the device; open the breaker; rebuild in the background after the cool-down |
| Out of device memory | allocation result | shrink residency, evict, and decline `NotResident` for the affected queries |
| Driver missing or loader fails (Linux without a Vulkan driver, for example) | probe at start | module reports `NoDevice`, stays loaded and declines everything |
| Shadow parity mismatch | the shadow checker | disable the GPU path, raise a Kodo warning, keep the mismatch inputs (ids and scores only) for diagnosis |
| CPU post-filter rejects a GPU candidate | the retriever | discard the whole GPU result, rerun on the CPU path, write a security audit entry, open the breaker (section 10.3) |

**Breaker.** After `graph_retrieval.breaker.failures` failures inside `graph_retrieval.breaker.window_s`, the graph kernel declines with `BreakerOpen` for `graph_retrieval.breaker.cooldown_s`, then tries one probe query before closing.

**No query fails.** Every path above ends in today's CPU and pgvector path. The only way a query fails is if Postgres fails, which is true today as well.

**Shadow parity.** When the GPU path is on, a sample of served queries (`graph_retrieval.shadow.fraction`) is replayed on the CPU path in the background and compared under section 8's tolerances. The comparison never delays the caller.

## 10. Isolation and security (PROPOSED)

### 10.1 What may be in GPU memory

Only data the server process may already read: embedding values, row and node indices, edge kinds and flag bits. No note text, titles, vault paths, ids, Soul data, secrets or credentials go to the GPU. The host maps indices back to ids. Encrypted personal blocks are never embedded (doc 08 section 9), so they never reach the GPU either. GPU memory is never written to disk by the module. Staging and per-query buffers are zeroed when they go back to the pool. On Windows the driver may still page device memory; that is the same exposure as paging of server process memory and is listed as decision 7 in section 17.

### 10.2 Scope is enforced twice: by partitioning and by the CPU post-filter

| Layer | What it does | Why it is needed |
|---|---|---|
| **Partitioning (segments plus a range mask)** | The vector kernel only scans readable segments. The expansion kernel never enters a node outside readable ranges, nor a non-Active or tombstoned node. | Correctness and leak prevention. pgvector applies the access clause in the `WHERE`, before `LIMIT`. If the GPU searched everything and filtered afterwards, hidden rows would take top-k places, results would differ from pgvector, and the number of visible results would reveal how many hidden rows scored higher. For expansion, the CPU retriever never expands through a hidden node; traversing through one would reach nodes the CPU path cannot reach and reveal that the hidden node exists. |
| **CPU post-filter (`IVaultAccess.CanRead` on every candidate)** | The retriever checks each returned candidate exactly as it does today. | Defence in depth. `IVaultAccess` is the single authority for access. The segment map is derived from it and could drift, and kernel code is harder to review than C#. The post-filter is the security floor and is not a setting. |

The readable segment set is resolved on the CPU per query from the principal, by the same rules `IVaultAccess` uses. The graph kernel never receives a principal, only ranges.

### 10.3 A post-filter rejection is an incident, not a filter

On the CPU path a rejection is normal. On the GPU path, a candidate that fails `CanRead` means the mask was wrong. The retriever then discards the whole GPU result (since expansion may have passed through that node), reruns the query on the CPU path, writes an audit entry (`graph_retrieval.scope_mismatch`, ids only, through the shared payload guard), and opens the breaker. Repeated mismatches disable the GPU path until the operator re-enables it.

### 10.4 Other rules

- Deprecated and other non-Active revisions are excluded by the `active` bit and again by the retriever's status check.
- Cross-agent leakage, including as hop targets, is covered by the range mask and the post-filter, and by dedicated tests (section 14).
- Timing: the GPU path's latency depends on the size of the readable set, as pgvector's does. No new timing channel is added beyond that; this is not proven and is listed under tests.
- The graph kernel runs inside the server process with no new network listener, no new port and no remote control surface.
- Settings changes to this module are audited (doc 03 registry). No setting can disable the post-filter, the stale-removal floor or the shadow checker's mismatch shutdown.

## 11. Observability (PROPOSED)

### 11.1 Metrics

| Metric | Type | Labels |
|---|---|---|
| `graph_retrieval.query.duration_ms` | histogram (p50, p95, p99) | `path` (gpu, cpu, pgvector), `stage` (vector, expand, total) |
| `graph_retrieval.query.declined` | counter | `reason` (the `DeclineReason` values) |
| `graph_retrieval.shadow.compared` and `.mismatch` | counters | `kind` (ids, scores, levels) |
| `graph_retrieval.boundary.band_size` | histogram | rows within tolerance at the k boundary (8.2) |
| `graph_retrieval.scope_mismatch` | counter | none (each one also has an audit entry) |
| `graph_retrieval.index.resident_rows`, `.resident_segments`, `.vram_bytes` | gauges | `mode` (fp16, int8) |
| `graph_retrieval.index.lag_changes` | gauge | none |
| `graph_retrieval.index.rebuild_ms` | histogram | `cause` (start, profile, device_lost, compaction) |
| `graph_retrieval.device.lost` | counter | none |
| `graph_retrieval.breaker.state` | gauge | none |

Power is not exposed as a metric: no cross-vendor, cross-OS API for GPU power was found. S28 measures power with whatever the host offers (section 13).

### 11.2 Kodo hooks

The module implements `IModuleHealth` (doc 46 section 10):

- **Liveness:** the worker loop is alive. No I/O.
- **Readiness:** `Pass` when disabled (declining is correct behaviour), or when enabled with a device and an index within the lag limit. `Warn` when enabled but the breaker is open, the index is behind, or residency is partial. Never `Fail` for a GPU problem, because the fallback serves every query.
- **Dependencies:** `pgvector` hard; `vulkan-device` soft.
- **Self-test:** runs a fixed synthetic dataset through the GPU path and the CPU reference and compares under section 8. Side-effect free; uses its own buffers.
- **Facts:** device name, API version, driver version (vendor-aware decode), mode, resident rows, VRAM budget and use, breaker state.

## 12. Configuration (PROPOSED, doc 03 registry, DECISIONS-LOG round 13)

| Key | Type | Default | Scope | Who may change | Approval |
|---|---|---|---|---|---|
| `graph_retrieval.gpu.enabled` | bool | `false` until S28 passes (13.4) | install | operator | No (audited) |
| `graph_retrieval.gpu.device` | `auto` or a device UUID | `auto` (first discrete device, else none; integrated GPUs only by explicit choice) | install | operator | No (audited) |
| `graph_retrieval.gpu.vram_budget_mib` | int, 256 to 90% of the device-local heap | 4096 | install | operator | No (audited) |
| `graph_retrieval.gpu.precision` | enum `fp16`, `int8_rescore` | `fp16` | install | operator | No (audited) |
| `graph_retrieval.gpu.int8_oversample` | int 2 to 16 | 4 | install | operator | No (audited) |
| `graph_retrieval.engine` | enum `postgres`, `cpu`, `cpu_then_gpu`, `gpu_then_cpu` | `postgres` until the CPU engine passes the parity suite (WP6), then `cpu` | install | operator | No (audited) |
| `graph_retrieval.cpu.threads` | int 1 to core count | core count minus 2 | install | operator | No (audited) |
| `graph_retrieval.gpu.stages` | set of `vector`, `expand`, `closure`, `ppr` | empty: a stage joins only when S28 shows it beats the CPU engine | install | operator | No (audited) |
| `graph_retrieval.rank.method` | enum `hop_decay`, `ppr` | `hop_decay` | install | operator | No (audited) |
| `graph_retrieval.ppr.alpha` / `.iterations` / `.cpu_replay_max_nodes` | real 0.05 to 0.5 / int 5 to 100 / int | 0.15 / 20 / 50,000 | install | operator | No (audited) |
| `graph_retrieval.expand.max_hops` | int 0 to 4 | 1 (today's retriever default; today's cap of 2 stays unless WP0 changes it) | install | operator | No (audited) |
| `graph_retrieval.expand.max_frontier` | int | 2,000 | install | operator | No (audited) |
| `graph_retrieval.dag.max_depth` / `.budget_share` | int 1 to 32 / real 0 to 0.5 | 8 / 0.2 | install | operator | No (audited) |
| `graph_retrieval.seed.work_context` | bool | `false` | install | operator | No (audited) |
| `graph_retrieval.communities.enabled` | bool | `false` | install | operator | No (audited) |
| `graph_retrieval.rerank.enabled` / `.shortlist` / `.deadline_ms` | bool / int 8 to 100 / int | `false` / 32 / 300 | install, agent | operator | No (audited) |
| `graph_retrieval.topk_margin` | int 0 to 256 | 16 | install | operator | No (audited) |
| `graph_retrieval.query_deadline_ms` | int 5 to 5000 | 250 | install | operator | No (audited) |
| `graph_retrieval.max_pending_apply` | int 0 to 10000 | 256 | install | operator | No (audited) |
| `graph_retrieval.breaker.failures` / `.window_s` / `.cooldown_s` | ints | 3 / 60 / 600 | install | operator | No (audited) |
| `graph_retrieval.shadow.fraction` | real 0.001 to 1.0 | 0.01 | install | operator | No (audited) |
| `graph_retrieval.tools.max_results` / `.max_steps_per_turn` | int 1 to 50 / int 1 to 32 | 20 / 8 | install, agent | operator | No (audited) |
| `graph_retrieval.inject.evict_after_triggers` | int 1 to 20 | 3 | install | operator | No (audited) |
| `graph_retrieval.compress.enabled` | bool | `false` | install, agent | operator | No (audited) |

`graph_retrieval.company.max_seeds` and `memory.context.company_share` are doc 08's keys (section 6, Settings) and are not repeated here.

The defaults above are starting values, not measurements. S28 replaces them.

**Not settings (security floors):** the range mask on every hop, closure step and walk transition; `evidence` edges never expand; traversal tools see only what the mask allows; compression never touches code, ids, paths, decision records or quoted text; community notes only over readable nodes; the rerank can only reorder; the CPU post-filter; the stale-removal floor (9.2); shutdown on a shadow mismatch or a scope mismatch; the ban on ids, text and paths in GPU memory; fallback on every failure. The shadow fraction has a floor of 0.001 so it cannot be turned off while the GPU path is on.

## 13. Benchmark spike S28 (PROPOSED, added to doc 35 in a follow-up)

### 13.1 Question

Which engine should be the default for each stage (Postgres, multi-core CPU, GPU on the owner's Arc B580), with exact parity? Which ranking method (`hop_decay` or `ppr`) and which optional stages (communities, rerank) improve retrieval on Bazaar's own Vault? Is any GPU graph kernel worth building at all?

### 13.2 Arms

1. **pgvector exact** (today's SQL, sequential scan). The reference.
2. **pgvector HNSW**, if doc 08 or doc 18 adds the index by then. Recorded for comparison; not a reference.
3. **CPU engine**: `CpuGraphKernel` over the CSR snapshot, multithreaded with `System.Numerics.Tensors` and parallel frontiers, at 1, 4, 8 and all cores.
4. **GPU fp16.**
5. **GPU int8 with fp16 rescore.**
6. **Software Vulkan** (lavapipe on Linux), for correctness only, not speed.
7. **Quantized CPU codes** (R8): Matryoshka truncation with rescore (only for a profile whose model supports it), RaBitQ and TurboQuant, each with full-vector rescore, on the CPU engine. Recall@k against exact pgvector and the memory saved are the measures.

### 13.3 Datasets and sizes

- **Synthetic:** random unit vectors at `D` = 768, 1024 and 1536, with a power-law graph (degree distribution from the real memory graph once one exists). Sizes 10k, 100k, 1M, 5M, 10M and, where it fits (int8 or larger budget), 20M and 50M. Sizes that do not fit are reported as "not resident", which is itself a result.
- **Realistic:** the repo's own docs and the Vault fixtures embedded with the chosen profile; and, if the owner allows, a copy of the owner's Vault on the owner's PC that never leaves it (decision 6 in section 17).
- **Scope mixes:** 1, 10, 100 and 1,000 agents, with queries from an agent (one private segment plus shared ones) and from the owner (all segments).
- **Queries:** k = 10, 20, 50; MaxHops 0, 1, 2 (and 3, 4 for `ppr`); DAG closure depths 2, 8, 32; PPR over reached subgraphs of 1k, 10k, 100k and 1M nodes; single queries and batches of 8, 32 and 128.

### 13.4 Metrics and pass or fail

Measured: p50 and p99 latency per stage, throughput (queries per second at batch sizes), recall@k against exact pgvector, parity counters from section 8, VRAM use, index build and incremental-apply time, CPU use of the host during a run, and GPU power where available (Intel's tooling on Windows, hwmon on Linux if the driver exposes it, else an external meter, else "not measured").

PROPOSED gates for `graph_retrieval.gpu.enabled` to default to `true` (the owner may change the numbers; decision 4 in section 17):

| Gate | Threshold |
|---|---|
| Parity, fp16 | 100% id-set and level parity on every query in the suite, within section 8's tolerance; zero scope mismatches |
| Latency | GPU p99 at most half of pgvector exact p99 at the corpus size the owner's install has at the time, and better than the CPU engine for the same stage |
| Small corpora | GPU p50 not worse than pgvector at 10k vectors, or the module declines below a size threshold that S28 sets |
| Recall, int8 | recall@k at least 0.99 for int8 to ship at all (still opt-in) |
| Stability | 24-hour soak with the client renderer running on the same card: no device loss, no leaked VRAM, every forced fault (deadline, device loss, allocation failure) ends in a correct fallback |
| Freshness | a removal is never served after its commit returns, under a write load of the memory sink at its batch limits |

These gates apply per stage: a GPU stage joins `graph_retrieval.gpu.stages` by default only when it passes them against the CPU engine for that stage. If no stage passes, the Vulkan provider module stays opt-in and the CPU engine is the default. The GPU expansion, closure and PPR kernels (WP13) are built only if the CPU engine's numbers leave room for a gain.

### 13.5 Retrieval quality evaluation (research input R6)

Speed gates say nothing about whether the context is better. Before any weight, alpha, budget share or optional stage is tuned, build a labelled question set from Bazaar's own Vault (this repo's docs are a public start; the owner's Vault only with decision 6's consent):

| Question type | Example shape | Scored by |
|---|---|---|
| Path | "How is decision X connected to research Y?" | the expected path appears in the pack |
| Impact | "What depends on note Z?" | recall of the labelled dependants |
| Current answer | "What is the current rule for W?" | the supersede head is returned and stale versions are labelled |
| Multi-hop fact | needs two or three linked documents | answer support recall@budget |
| Look-up | single fact | must not get worse than seeds only (graph methods can hurt here, per the research report) |
| Isolation | asked by agent A about agent B's private notes | zero leaks, including paths and counts |

| Cross-project (company tier) | "What do we know about X?" asked from a project that did not produce the lesson | the company note is in the pack, labelled, with no source text from the other project |
| Company leakage | canary project, customer and person names planted in source notes | zero canaries in any pack, label or redacted evidence entry |
| Poisoning | a planted bad candidate and a planted T1 "principle" | neither appears in any pack (acceptance zero above T1) |

Arms: seeds only (today's vector plus lexical), `hop_decay`, `ppr`, each with and without DAG closure, communities, the company tier and rerank. Metrics: recall and nDCG at the token budget, tokens per useful item, and latency. Public long-horizon benchmarks (LongMemEval, LoCoMo) are a sanity check only, since none covers a vault of plans and decisions. Defaults change only on measured gains.

**Injection quality (section 0.1).** Proactive injection is the primary path, so it is measured by what the agent still has to look up. These are metrics and an evaluation plan, not targets; no number is set before measurement.

| Metric | Definition | Grouped by |
|---|---|---|
| Redundant search rate | Search or read tool calls whose useful result (the hit the agent then cites or opens) was already in the injected context at the time of the call, over all such calls | agent, task, trigger kind |
| Miss rate | Search or read tool calls that found a node the injection did not include and the agent then used, over all such calls | agent, task, trigger kind |
| Tokens per correct answer | Prompt tokens spent (injected plus tool results) per labelled question answered correctly, compared with a full-context baseline that puts every readable candidate note in the window up to the model's limit | agent, model, question type |
| Injection share used | Injected items cited or opened, over items injected | trigger kind |

How it is measured: the runtime already passes `AlreadyInContext` and the retrieval audit records returned ids (`constellation-memory-graph.md`); a tool result is matched against them by node id, never by text. Every miss writes a candidate question (the tool query and the node found, ids only) into the labelled set builder of WP16, where a person reviews it before it counts, so misses improve the set the weights are tuned on. The full-context baseline runs only in the evaluation harness, never in production.

### 13.6 Where and who

Owner's Windows PC with the B580 for speed numbers (like S14). A Linux run on any Vulkan-capable machine for the Linux requirement; the cloud container has no GPU, so Linux GPU numbers need hardware the owner chooses (decision 5 in section 17). Software Vulkan runs in CI. No benchmark number is written into this doc before S28 measures it.

## 14. Test strategy (PROPOSED)

GPU-less CI is the normal case. These implementations exist and must agree:

1. **pgvector plus today's retriever:** the reference, on the real Postgres fixture the server tests already use.
2. **The CPU engine (`CpuGraphKernel`)** and, for the new stages, the single-threaded oracle: the same tile layout, the same fp32 accumulation and the same tie key as the shaders, so a difference points at the shader, not the algorithm.
3. **The SPIR-V shaders on a software Vulkan device** (Mesa lavapipe on Linux CI; the Android job already uses SwiftShader through the emulator). If the CI image cannot provide one, these tests are skipped with a visible reason, never silently.

Tests:

- **Property tests (parity).** Random corpora, random graphs, random scopes and random queries: the graph kernel result equals the CPU retriever result under section 8. Generators include ties at the k boundary, duplicate vectors, zero-degree and very high-degree nodes, cycles, self-links, and links that point into hidden segments. A property-test library is not in `Directory.Packages.props` today; adding one (for example CsCheck, Apache-2.0, or FsCheck, BSD-3) needs the usual licence check, or the generators are hand-written with a fixed seed.
- **Scope tests.** An agent never sees another agent's note as a seed, a hop target, a count or a path through a hidden node. The owner sees all. A note moved out of a readable area disappears on the next query.
- **Deprecated and rollback tests.** A deprecated revision is never returned; a rollback through `ActivateRevisionAsync` changes the GPU result on the next query.
- **Fault injection.** A fake device that times out, loses the device, fails allocation or returns a wrong mask: each case returns the CPU result, opens the breaker where section 9.3 says so, and writes the audit entry for a scope mismatch.
- **Freshness.** Concurrent writes through the sink while querying: removals are never stale; additions appear once applied.
- **Settings.** Each default and bound; a change writes an audit entry; the floors cannot be set.
- **Kodo contract.** The module passes the `Bazaar.Kodo.Testing` health contract base.
- **Architecture.** `Bazaar.Server` references only the Abstractions project; the module does not reference `Bazaar.Constellation.Rendering` or any client project.
- **Nightly on the owner's PC:** the parity suite on the real B580, plus the S28 harness at small sizes as a regression check. Not a PR gate.

## 15. Work packages (PROPOSED)

Sizes: S is up to a day, M is two to three days, L is about a week.

| # | Package | Size | Depends on |
|---|---|---|---|
| 0 | Docs PR: converge the memory graph onto Constellation nodes in `constellation-memory-graph.md` (section 1.1), with the edge-weight table for the Vault edge kinds | M | decisions 10 and 12 in section 17 |
| 1 | Abstractions project: interface, records, decline reasons, null kernel, architecture test | S | none |
| 2 | Seam in `MemoryGraphRetriever`: optional kernel, decline handling, CPU replay of scoring from hop levels, post-filter mismatch handling; tests proving unchanged behaviour with the null kernel | M | 1 |
| 3 | Extract `Bazaar.Gpu.Vulkan` from the renderer (probe, device selection, buffers, compute pipeline helper); fix the Intel driver-version decode; renderer tests unchanged | M | decision 3 in section 17 |
| 4 | Change feed table, migration and producers in the Vault indexer, memory revision store and embedding writer | M | 1 |
| 5 | Host-side index over Constellation nodes (`vault_docs`, `vault_links`, front-matter relations): segment table, row maps, CSR builder, cold build from Postgres, delta apply, compaction | L | 0, 4 |
| 6 | `CpuGraphKernel` (the default engine): vector top-k, bounded expansion, DAG closure, PPR, typed path queries; the single-threaded oracle; parity property tests against pgvector, the retriever and the oracle | L | 5 |
| 6a | Pipeline stages 3A.5 and 3A.8 in the Constellation retriever: subgraph assembly, gists, provenance, reasons, two-tier packing | M | 0, 6 |
| 6b | Optional stages: community notes (3A.6) and gateway rerank (3A.7), both off by default | M | 6a, doc 19 WP5 (Louvain) |
| 7 | GLSL compute shaders and SPIR-V, CI recompile and `spirv-val` check, software-Vulkan test job | L | 3, 6 |
| 8 | `Bazaar.Server.GraphRetrieval.Vulkan` provider module: residency, budgets, per-query pools, deadline and device-loss handling, breaker | L | 5, 7 |
| 9 | Shadow parity checker and the scope-mismatch audit entry | M | 2, 6 (CPU engine), 8 (GPU) |
| 10 | Kodo `IModuleHealth`, metrics, settings rows and component wiring for both modules | M | 6, 8 |
| 11 | S28 harness and run on the owner's PC; write results into this doc and doc 35 | L | 6, 8, 16 |
| 12 | int8 mode with fp16 rescore | M | 8, only if S28 keeps it |
| 13 | GPU expansion, closure and PPR kernels | M | 7, only if S28's CPU numbers leave room for a gain |
| 14 | Vault vector search (`VaultSearchService`) through the same kernel | M | 8, and the block-to-document seed rule (decision 9 in section 17) |
| 15 | Phase 2 nodes: GC, decision-reference, task and PR nodes with their own mask source | L | 6, decision 11 in section 17 |
| 16 | Labelled Vault question set and evaluation harness (13.5), including the company-tier, leakage and poisoning questions | M | none (public docs first; decision 6 in section 17 for the real Vault) |
| 17 | Company tier in the index and pipeline (3A.10): company segment always in the mask, company seeds, `evidence` edges host-side only, reserved share; tests that no `evidence` edge is in the CSR and that a project B query never reaches project A through the tier. Doc 08 work packages 21 and 22 | M | 5, 6a; doc 08 work package 18 |
| 18 | Typed edge kinds of 5.3 in the projector and the converged retriever spec (WP0), with the precedent closure and expiry labels | M | 0 |
| 19 | Bounded traversal tools (3A.3) through doc 17's tool rules: step cap, cycle check, mask | M | 6, doc 17 |
| 20 | Proactive injection triggers and refresh (3A.11) in the turn assembler side of `constellation-memory-graph.md`, with save to Vault before clear | M | 6a, doc 06 |
| 21 | Per-scope summaries and `zoom_in` (3A.6), refreshed by reverse impact | M | 6, doc 19 WP5 |
| 22 | Cache-aware pack order and the optional compressor sidecar (3A.12), off by default | S | 6a |
| 23 | Injection-quality metrics (13.5): redundant search rate, miss rate, tokens per correct answer against the full-context baseline, misses fed to WP16 | M | 16, 20 |

Work packages 0, 1, 3, 4 and 16 can start in parallel. Nothing beyond 0, 1 and 2 should merge before the decisions 1, 2 and 10 in section 17 are accepted.

## 16. Risks

- The gain may be small at the owner's current corpus size; pgvector exact over a few hundred thousand rows may already be fast enough. S28 decides; the module is cheap to leave off.
- Intel Arc compute behaviour under Vulkan is not measured. The draw benchmark found an unexplained cliff above about 4M instances per frame; compute may have its own surprises.
- Sharing the card with the desktop client renderer on the owner's PC can cause device-loss or latency interference. The soak gate tests this.
- Kernel bugs that pass tests but differ on one vendor's driver. Shadow parity in production is the guard.
- Keeping the index in step with the Vault adds a new consistency path. The change-feed table and the stale-removal floor bound the damage to "slower", not "wrong".

## 17. Decisions taken

The earlier draft listed 14 owner questions. Most are answered by the design docs, the research, or the owner's standing direction (build everything, no shortcuts, everything is a module, nothing duplicated). They are settled here as PROPOSED decisions, and the owner can overturn any of them.

| # | Question | Decision (PROPOSED) | Why |
|---|---|---|---|
| 1 | Placement | A compute-only module pair, `Bazaar.Server.GraphRetrieval` plus `.Abstractions`, that Constellation calls. All GraphDRAG rules (seeds, scope, expansion policy, packing) stay in `Bazaar.Server.Constellation`. | Everything is a module and core holds no features; GraphDRAG stays "Constellation's server side" because the rules never leave it. |
| 2 | Seam location | The seam lives in the extracted Constellation module, never in `Bazaar.Server` or Core. | The architecture ratchet forbids new files there. |
| 3 | Shared Vulkan library | Extract a feature-free `Bazaar.Gpu.Vulkan` (probe, device, buffers) used by the renderer and the server module. | One GPU API in the codebase, and no duplicate code. |
| 4 | Default-on gates | The thresholds in section 13.4 stand. The engine stays off until S28 passes them on the owner's PC. | The benchmark decides, not opinion. |
| 5 | Linux GPU testing | CI runs the CPU reference and parity tests. The GPU benchmark runs on the owner's PC (Intel Arc B580) through Remote Control, from `V:`. A Linux GPU run waits for a Linux GPU host. | No other GPU machine exists. |
| 6 | Realistic dataset | S28 and the evaluation may use a copy of the owner's real Vault on the owner's PC only. Nothing leaves the machine. | It is their machine and their data. |
| 7 | Driver paging on Windows | Accepted and documented. Embeddings are no more sensitive than the database the server already reads. | The same exposure already exists for server memory. |
| 8 | Embedding inference on the server GPU | Out of scope here. A separate design follows if the benchmark shows VRAM headroom. | Keeps this design bounded. |
| 9 | Block versus document seeds | A document's vector score is its best block's score. Tunable later from the labelled set. | Simple and standard. |
| 10 | Convergence schema | Re-key the memory revision tables to `doc_id` by migration, with a dry run, keeping all history. | Coordinator approved; follows from "no separate memory nodes". |
| 11 | Phase 2 nodes | GC, decision-reference, task and PR nodes join retrieval in phase 2. Their visibility follows GC membership and the DM and Thinking-GC rules, not Vault paths. | The goal is project-wide context; the access rules already exist. |
| 12 | Edge kinds and direction | Defaults stay: `link` 1.0, `derived_from` 0.8, `supersedes` 0.5, outgoing, plus backlinks at half weight. The closed set of five added kinds (`conditioned_on`, `approved_by`, `follows_precedent`, `resolved_by`, `affects`) is DECIDED by the coordinator (2026-11-20); their weights in 5.3 are starting values, replaced from the labelled question set. `evidence` and provenance labels never expand. | No weight is tuned without measurement; a lean, closed schema. |
| 14 | Time validity | Add nullable `valid_from` and `valid_to` to revisions and an effective time to `supersedes` edges. "Current" stays the head of the supersede chain. Precedents carry `review_by` and may carry `valid_to`; expired ones are labelled history. | Needed for long-horizon "what was true on date D"; additive. |
| 15 | How wide a cross-project hop may go (the earlier open trade-off) | **Answered by the owner (2026-11-20, section 0 item 3).** Cross-project reach is the company knowledge tier of doc 08 section 6, always in scope for every agent; project-to-project hops stay with Hive copies and role grants; no owner-granted project link is added. | The owner's statement names company knowledge as the principle; distillation with provenance gives the cross-project value without opening one project's private notes to another. |
| 16 | Primary context path | Proactive injection (3A.11) is primary; search, read and traversal tools stay as the fallback, and their use is the measure of injection quality (13.5). | Owner design intent (section 0.1). |
| 17 | Quantization in v1 | int8 with rescore stays opt-in; Matryoshka, RaBitQ and TurboQuant are S28 arms only. | Research input R8: headroom, not a v1 need, and no prompt-token gain. |

No owner trade-off is left open in this doc.

Also to fix outside this doc (found while reading, not changed here): `constellation-memory-graph.md` "Build status" says `MemoryGraphRetriever` is registered in `ConstellationComponent`, but on `main` it is constructed only in tests.
