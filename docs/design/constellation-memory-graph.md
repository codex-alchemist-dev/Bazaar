# Constellation memory graph (GraphDRAG, server side)

Status: IN PROGRESS (2026-11-16): projector, store, retriever and sink are built; see Build status. The remaining GraphDRAG pieces are specified as PROPOSED in "Server-side GraphDRAG completion". Placement and governance are DECIDED (owner, 2026-11-13; doc 19 "Agent memory governance", DECISIONS-LOG round 11). The rules here are the implementation spec for that placement. They do not change any DECIDED item.

## Core principle: one graph, Constellation's own nodes

Owner statement (2026-11-20, relayed by the coordinator): GraphDRAG is not a separate memory system. It retrieves over Constellation's own nodes. Agents already write research, plans, internal docs and decisions into the Vault, so each project's knowledge and each agent's memory are the same graph. It is built per project, and it is interconnected across linked projects. Retrieval does not use separate memory nodes. The placement in doc 19 ("Agent memory governance") already says GraphDRAG belongs to Constellation's server side; this states what that means for the data model.
Owner statement (2026-11-20, relayed by the coordinator): GraphDRAG is not a separate memory system. It retrieves over Constellation's own nodes. Agents already write research, plans, internal docs and decisions into the Vault, so each project's knowledge and each agent's memory are the same graph. It is built per project, and it is interconnected across projects. Retrieval does not use separate memory nodes. The placement in doc 19 ("Agent memory governance") already says GraphDRAG belongs to Constellation's server side; this states what that means for the data model.

What "interconnected across projects" means (owner, 2026-11-20, verbatim): "Cross-project is on the principle of company knowledge? Something knowledge. The knowledge the company builds up over time. Learnings, principles, what works for us, what doesn't. Stuff AI typically can't have since they never usually bring other 'unrelated' projects into context." The cross-project link is therefore the **company knowledge tier** (doc 08 section 6): generalised principles, learnings, precedents, incidents and what works or fails, distilled from every project with provenance, stored as ordinary Vault notes under `company/memory/` and so ordinary nodes of this one graph. Retrieval reaches it from every project by default. Personal agent memory stays isolated; project-private notes cross projects only through an explicit Hive grant, or by being distilled into a company note that names no project.

Proactive first (owner design intent, 2026-11-20): agents keep their search and read tools, but GraphDRAG places the relevant knowledge, including company knowledge, into the agent's soft long-horizon context without being asked, so the tools are the fallback. Triggers, refresh and the measurement are in doc 64 sections 0.1, 3A.11 and 13.5; the memory side is "Agent-turn wiring" below.

Check of what is built against this principle (2026-11-20). The built slice does NOT yet meet it, and the gap is a duplication risk:

- Constellation's graph (`VaultGraphSource`, `VaultGraphProjection`) has nodes keyed by Vault `doc_id` (all Vault documents) and edges from resolved `vault_links`.
- The built memory graph has its own node space. `constellation_memory_revision` and `constellation_memory_edge` key nodes by memory note id (a ULID) and are filled only from `agents/<agent>/memory/`. A memory note and the Vault document of the same Markdown file are therefore two nodes in two stores. Project research, plans, decisions and internal docs are not nodes in the memory graph at all, so retrieval cannot hop from a memory note to the decision or doc it came from, and cross-project links in the Vault are invisible to it.
- `MemoryGraphProjector` re-parses wiki-links itself instead of reading the resolved links the Vault index already holds (known open item b is a symptom: it cannot resolve `[[Title]]` links).

**[PROPOSED] Convergence.** Retrieval runs over the Vault index nodes (`doc_id`) and `vault_links`, plus the other Constellation node kinds in doc 19 where they are in scope. Memory-specific attributes (kind, trust tier, status, revision and rollback history, agent scope) become attributes and history on those same nodes, not a second node set. The memory store keeps only what the Vault index does not hold (append-only revisions and the active pointer), keyed by `doc_id`. Per-agent and per-project scope, `IVaultAccess` filtering on every hop, deprecated-note exclusion and the token budget are unchanged. A hop across projects is allowed only where the existing design allows it (role grants, Hive copies); anything wider is an owner question below. Migration is a data move with a dry run and no loss of revision history. No new code should add nodes to the memory-only tables until this is settled.
**[PROPOSED] Convergence.** Retrieval runs over the Vault index nodes (`doc_id`) and `vault_links`, plus the other Constellation node kinds in doc 19 where they are in scope. Memory-specific attributes (kind, trust tier, status, revision and rollback history, agent scope) become attributes and history on those same nodes, not a second node set. The memory store keeps only what the Vault index does not hold (append-only revisions and the active pointer), keyed by `doc_id`. Per-agent and per-project scope, `IVaultAccess` filtering on every hop, deprecated-note exclusion and the token budget are unchanged. A hop from one project's notes into another project's notes is allowed only where the existing design allows it (role grants, Hive copies). The company tier is readable by every agent that is not quarantined, so hops into it are always allowed, and hops out of it stay inside it (doc 08 section 6.8). Migration is a data move with a dry run and no loss of revision history. The details are pinned in "Convergence design" below and built (see Build status).

### Convergence design [PROPOSED; coordinator-approved 2026-11-20]

Pins the details of the convergence above. Nothing here changes a DECIDED item; per-agent isolation is unchanged.

1. **Node identity.** A graph node is a Vault index document, `vault_docs.doc_id`. A memory note is the doc node of its Markdown file; it gets no second node. The note id (`MemoryNote.Id`, a ULID) maps to `doc_id` by joining `constellation_memory_revision.vault_path` to `vault_docs.path` (optionally restricted to the configured `Constellation:VaultId`). The match must be unique. The doc's front matter `id` (`vault_docs.frontmatter->>'id'`) is a cross-check: when present and different from the note id, the pair is a conflict, not a mapping.
2. **Attributes, not nodes.** Memory kind, trust tier, status and `scope_agent_id` are attributes of the doc node, read from its active memory revision. `constellation_memory_revision` gains a nullable `doc_id`. Revisions stay append-only and keep their `(note_id, version)` key; every revision of a note carries the same `doc_id` once mapped, so revision and rollback history is listed per `doc_id` (`ListRevisionsByDocAsync`). At most one active memory revision per `doc_id` (partial unique index). A `doc_id` may change only from NULL to a value, never afterwards (trigger). The store resolves `doc_id` from the Vault index on each write; a write made before the Vault indexer has seen the file stores NULL, and the next write of that note or a migrator run fills it.
3. **Edges.** `Link` edges are the resolved `vault_links` (`target_doc` not null; kinds `link` and `embed`), read live from the Vault index for every node, so `[[Title]]`, alias and path links resolve by the doc 18 rules. The projector, when the doc is indexed, also records the doc's resolved links in the revision (targets are `doc_id`s) as that revision's snapshot. `DerivedFrom` and `Supersedes` stay revision edges from front matter; their note-id targets, and note-id `Link` targets of revisions written before convergence, are mapped to `doc_id` through the revision table, then through front matter `id`; an id that maps to more than one doc is dropped. Weights are unchanged: Link 1.0, DerivedFrom 0.8, Supersedes 0.5, provenance 0.0 (labels only, never expand). A target reached by two edges keeps the higher weight.
4. **Retriever.** Seeds and hits are `doc_id`s (`Guid` "D" format); token counts are keyed by the same string. Expansion follows the expanding edges of item 3 from each kept node, up to `MaxHops` (default 1, cap 2), score = parent x weight x 0.5. `IVaultAccess.CanRead` is applied to every node before it scores or expands, hop targets included, on the doc path and, for a memory node, on the revision path too; a hidden node is never returned, never expanded and never counted. Excluded: memory nodes whose active revision is not `Active`, and docs whose front matter `status` is `deprecated`. Docs of other projects are reachable only when the caller's scope allows the path (project membership, doc 19); nothing widens scope. Token budget packing is unchanged (`TokenBudgetPacker.Pack`).
5. **Migration.** `constellation/0002_memory_doc_id` (embedded in `Bazaar.Server.Constellation` under `Sql/components/constellation/`, read by the host's `ComponentMigrationSource`; `0001` stays embedded in `Bazaar.Server`) adds `doc_id`, its indexes, the trigger and the column grant. It moves no data. The backfill is `MemoryGraphDocIdMigrator`: `DryRunAsync` computes, in a read-only transaction, the mappings (note, doc, revision rows), unmapped notes (no Vault doc at that path) and conflicts (ambiguous path, front matter id mismatch, a doc claimed by two notes, an existing different `doc_id`), and changes nothing. `RunAsync` recomputes the same plan inside one transaction under a table lock, sets `doc_id` only where it is NULL for the mapped notes, skips unmapped and conflicting notes, and reports what it did. It never deletes or rewrites a revision row and is idempotent: a second run maps nothing new.

Open for the owner: whether the Vault link resolver should also resolve `[[note id]]` links by front matter `id` (doc 18 resolves by path, title and alias only; note-id links of older revisions are kept by item 3), and what happens to history when a re-index gives an existing path a new `doc_id` (the store refuses the write and the dry run reports it as a conflict).

## Scope of this slice

Builds the server-side graph over agent memory notes, the path from a note write to the graph, and graph-expanded retrieval with per-agent scope. In:

1. Graph model and wire-independent records (`Bazaar.Server.Constellation.Memory`).
2. A pure projector: one `MemoryNote` revision becomes one `MemoryGraphRevision` with edges.
3. A Postgres store (`constellation` namespace, migration `0001`): every revision is kept, one revision per note is active, and rollback re-activates an earlier revision.
4. A retriever: RRF-fused seeds, expanded along active edges, filtered by `IVaultAccess`, packed under the token budget.
5. A sink decorator: after `IMemoryBatchSink.CommitAsync` succeeds, the batch's notes are projected and stored.
6. Component wiring in `ConstellationComponent`, and tests for each piece.

Out of this slice (named so nobody assumes they exist): crypto-erase and retention (blocked on the per-person key table, doc 04 and doc 48), legal hold, the Hive and Company areas (doc 08 section 6), vector seeds (the embedding adapter is doc 08 WP6; retriever use is specified in "Vector seeds" below), the owner-audit trail of reads (doc 48; specified in "Owner-read access log" below, not built), HTTP and wire endpoints for the graph (specified in "Retrieval operation" below, not built), and the client view.
Out of this slice (named so nobody assumes they exist): crypto-erase and retention (blocked on the per-person key table, doc 04 and doc 48), legal hold, the Hive and Company areas (doc 08 section 6), vector seeds (the embedding adapter is doc 08 WP6; retriever use is specified in "Vector seeds" below), the owner-audit trail of reads beyond the retrieval operation (doc 48; the durable log is built for retrieval, see "Owner-read access log" below; `get`, `list_revisions` and `export` are not built), HTTP and wire endpoints for the graph (specified in "Retrieval operation" below, not built), and the client view.

## Records

Namespace `Bazaar.Server.Constellation.Memory`. Revisions are keyed by note id (`MemoryNote.Id`) and, since the convergence, carry the Vault `doc_id` of their file; graph nodes are doc ids (see "Convergence design"). Edge targets use prefixes for non-note nodes: `agent:<id>`, `gc:<id>`, `message:<id>`; Link targets written by the indexed projection are doc ids.

- `MemoryEdgeKind`: `Link` (a `[[wiki-link]]` in the body that resolves to a note id), `DerivedFrom` (`MemoryNote.DerivedFrom`), `Supersedes` (`MemoryNote.Supersedes`), `AuthoredBy` (target `agent:`), `FromGc` (target `gc:`), `FromMessage` (target `message:`).
- `MemoryGraphEdge(TargetId, Kind, Weight)`. Weights: Link 1.0, DerivedFrom 0.8, Supersedes 0.5, provenance edges 0.0 (they never expand a retrieval; they only label).
- `MemoryGraphRevision(NoteId, Version, ScopeAgentId, VaultPath, Title, Kind, Status, Trust, Edges)`. `ScopeAgentId` is the agent whose private area holds the note. `VaultPath` is the normalized `VaultPath.Value`.

## Store

`IMemoryGraphStore` (interface in the same namespace):

- `StoreRevisionAsync(revision)`: in one transaction, insert the revision and make it the active one for its note. The previous active revision stays stored with `is_active = false`. Storing a version that already exists for that note is an error, not an upsert, because revisions are append-only.
- `ActivateRevisionAsync(noteId, version)`: makes a stored earlier revision the active one. Returns false when the version does not exist. This is the rollback. Rollback never deletes.
- `GetActiveAsync(noteIds)`: active revisions for the ids given. Missing ids are left out.
- `ListRevisionsAsync(noteId)`: all stored revisions of one note, newest first.

Postgres (`src/Bazaar.Server/Data/Sql/constellation/0001_memory_graph.sql`):

- `constellation_memory_revision(note_id text, version bigint, scope_agent_id text, vault_path text, title text, kind text, status text, trust text, is_active boolean, stored_at timestamptz, primary key (note_id, version))`, with a partial unique index on `(note_id) where is_active`.
- `constellation_memory_edge(note_id text, version bigint, target_id text, kind text, weight double precision, primary key (note_id, version, target_id, kind))`, foreign key to the revision row.

Writes use raw Npgsql through the shared `NpgsqlDataSource` (CLAUDE.md data-access rule). No ORM.

## Projector

`MemoryGraphProjector.Project(MemoryNote note, string body, string vaultPath, string scopeAgentId, IReadOnlyDictionary<string,string> noteIdsByTitle)` returns a `MemoryGraphRevision`.

- Wiki-links: `[[target]]` tokens in the body. A target resolves when it equals a note id or a key of `noteIdsByTitle`. Unresolved targets produce no edge (the rule from `graph-index.md`: a dangling name is dropped, not invented).
- Provenance: `AuthoredBy` for the agent (`scopeAgentId`); `FromGc` when `Origin.Gc` is set; `FromMessage` for each `Origin.Messages` entry.
- Output is deterministic: edges sorted by kind, then target, with duplicates removed.

## Retrieval

`MemoryGraphRetriever.RetrieveAsync(VaultPrincipal who, IReadOnlyList<RankedHit> lexical, IReadOnlyList<RankedHit> vector, MemoryGraphOptions options, ct)`:

1. Fuse the seeds with `RrfFusion.Fuse` (ids are Vault doc ids since the convergence, not note ids or vault paths).
2. Expand up to `options.MaxHops` (default 1, cap 2) along `Link`, `DerivedFrom` and `Supersedes` edges of active revisions. A neighbour's score is the parent's score times the edge weight times 0.5 per hop.
3. Visibility: every candidate's `VaultPath` must pass `IVaultAccess.CanRead(who, path)`. Candidates that fail are dropped before scoring, so a hidden note never shows up as a hop target or a count. Scope is never widened by expansion. An optional path scope (`MemoryGraphOptions.ScopePrefixes`) narrows seeds and hop targets further to docs under the given path prefixes; it only narrows what `IVaultAccess` allows.
4. Status: only `Active` revisions are returned. Deprecated revisions are kept for history and never returned.
5. Pack with `TokenBudgetPacker.Pack`, using the token counts supplied by the caller.

### Supersede-head ranking [PROPOSED; coordinator decision 2026-11-20; built]

A `Supersedes` chain (C supersedes B, B supersedes A) has one current note, its head. The rules:

1. **Head first.** The node reader reports, for each doc, the docs whose active memory revision has a `Supersedes` edge to it (`ConstellationGraphNode.SupersededBy`; the edge target may be the doc id, a note id of the doc, or its front matter `id`; a key that names more than one doc is dropped). When a seed or hop target is superseded, the retriever follows the chain to its head: the first node that no readable, `Active`, non-deprecated, in-scope node supersedes. The head is returned in its place with at least the superseded node's score (so a query that best matches an old version still ranks the current version where the old one would have ranked) and `ViaDocId` set to the superseded doc. Among several readable superseders the lowest doc id wins, so the result is deterministic; the walk is cycle-safe and bounded (16 steps).
2. **History is labelled and opt-in.** A superseded node is never current advice. It never expands and is not returned unless the caller sets `MemoryGraphOptions.IncludeHistory`; then it is returned with `IsHistory = true` and `SupersededBy` = the head's doc id, its score capped at the head's score, and it sorts after the head on a tie. It is never ranked above its head.
3. **Isolation.** A superseder the caller cannot read (another agent's private note, a doc outside the caller's project or path scope) does not count: it is never a head, never appears, and does not turn the caller's own note into history.


## Sink

`MemoryGraphSink : IMemoryBatchSink` wraps the real sink. On a successful commit it parses each `MemoryWriteItem.Content` with `NoteDocument.Parse`, projects the note, and calls `StoreRevisionAsync`. A projection failure is logged and does not fail the commit: the Vault is the source of truth and the graph can be rebuilt from it.

## Rebuild

`MemoryGraphRebuilder.RebuildAgentAsync(agentId, ct)` rebuilds one agent's graph from the Vault (the source of truth), reading `agents/<agent>/memory/` through `IMemoryVault`. Two passes: parse every file and build a title map from titles that are unique within the agent's area (an ambiguous title is dropped, so it makes no edge); then project each note. A note whose active revision is already at or past the Vault version is skipped, so a rebuild is idempotent and never writes an older revision over a newer one. Files that do not parse, or stores that are refused, are counted as failed and logged; they never stop the rest. The result is `(Scanned, Stored, UpToDate, Failed)`.

## Audit

Doc 19 ("Agent memory governance", DECIDED) requires that each memory item's source and author are written to the audit log (doc 48). `AuditingMemoryGraphStore` decorates `IMemoryGraphStore` and appends one `AuditEntry` after each change that succeeds:

- `memory.revision.stored` after `StoreRevisionAsync`. Actor `agent:<scope agent id>`. Payload: `note_id`, `version`, `scope_agent_id`, `memory_kind`, `trust`, and the provenance edges as `written_by`, `from_gc` and `from_messages` (ids only). Never the title, the body or the vault path.
- `memory.revision.activated` after `ActivateRevisionAsync` returns true (a rollback). Actor `system:memory`, because the store interface carries no acting principal; the endpoint slice adds one. Payload: `note_id`, `version` and the same provenance as the activated revision.

The payload goes through the shared payload guard, so no secret-like key can enter. The audit append happens after the store call and its exceptions propagate: a revision that could not be audited is reported, not hidden. Failing the audit never rolls the stored revision back (revisions are append-only). The memory component registers the decorator as its own service (`AuditingMemoryGraphStore`) and uses it for the write-path sink when an `IAuditLog` is registered; without one the graph store is used as is. The Constellation registration of `IMemoryGraphStore` is left alone, because component start order is not guaranteed. Callers that roll a note back (the endpoint slice) take the decorator so the rollback is audited too.

## Server-side GraphDRAG completion [PROPOSED]

Status: PROPOSED (2026-11-20). Everything in the sections below this line up to "Tests" is a specification for work that is not built. It changes no DECIDED item and needs owner confirmation of the open questions at the end before code starts. GPU acceleration of the retrieval work is specified separately in `docs/design/64-gpu-graph-retrieval.md`; nothing here depends on it, and the CPU path stays the reference.

### Vector seeds (retriever side only) [PROPOSED]

Scope split (coordinator, 2026-11-20): the embedding adapter is not specified here. Another work package owns it as module `Bazaar.Server.Embeddings` plus `Bazaar.Server.Embeddings.Abstractions`, with an `IEmbeddingProvider` contract (name to be published by the Embeddings module), gateway-routed per doc 05, one profile per install per doc 08 section 9 and doc 08 work package 6. This section states only how the retriever uses it.
Scope split (coordinator, 2026-11-20): the embedding adapter is not specified here. Another work package owns it as module `Bazaar.Server.Embeddings` plus `Bazaar.Server.Embeddings.Abstractions`, with an embedding provider contract, gateway-routed per doc 05 (`IModelGateway.EmbedAsync`), one profile per install per doc 08 section 9 and doc 08 work package 6. This section states only how the retriever uses it. On main today the contract the retriever binds to is `IVaultQueryEmbedder` (`Bazaar.Vault.Abstractions`, namespace `Bazaar.Server.Vault.Search`): a `ProfileId` and `EmbedAsync(text)`, the same one Vault search takes. The Embeddings module that will implement it is not on main yet; until an implementation is registered every call is lexical only.

What exists today, from the code: `VaultEmbeddingRepository` (`src/Bazaar.Server/Vault/Data/VaultEmbeddingRepository.cs`) stores one `halfvec` per Vault block in `vault_block_embedding` (`src/Bazaar.Server/Data/Sql/common/0009_vault.sql`), keyed by `(doc_id, ordinal, profile_id)`, with a `text_hash` so a row is valid only while its block text is unchanged, and a database trigger that checks the dimension against `embedding_profile.dimensions`. Vectors travel as pgvector text literals. `DeleteForProfileAsync` drops one profile's rows. The pgvector build is pinned and packaged under `packaging/postgres` (`versions.txt`, `build-pgvector-windows.ps1`, `Dockerfile`). The memory graph store has no vectors of its own.

Rules:

1. **One store.** Note embeddings are the Vault block embeddings above. The graph does not get a second vector table. The block index indexes a note's blocks when the Vault indexer catches up (doc 08 section 8, doc 18), so embeddings are produced by the indexer path that already runs after a memory write commits, not inside `MemoryGraphSink`. The sink still only projects edges. A note written but not yet embedded has no vector seed until the indexer catches up (doc 08: notes without embeddings are skipped by vector search).
2. **Query embedding.** At retrieval time the retriever calls the `IEmbeddingProvider` once for the query text, under the active `embedding_profile`. The call goes through the gateway, so per-agent model policy and quota apply to the calling agent.
3. **Vector seed query.** The retriever asks for the top `memory.query_candidates`-bounded set of blocks by cosine distance (`halfvec`, pgvector HNSW) where `profile_id` equals the active profile, collapses blocks to note ids (best block wins), and passes the result as the `vector` argument of `MemoryGraphRetriever.RetrieveAsync`. The note id to `doc_id` mapping comes from the Vault index. Profiles never mix in one query (doc 08 section 9).
4. **Per-agent scoping.** The vector query is restricted to the paths the caller may read before ranking: the agent's own `agents/<agent>/memory/` and the project area its role allows, using the same `IVaultAccess.CanRead` rule as the lexical seeds. The filter is applied in the query where possible and again on the result. Another agent's private notes never enter the candidate set, so they cannot rank, expand or count.
5. **Personal data.** Encrypted personal blocks are never embedded (doc 08 section 9), so no vector seed can surface them.
6. **Model or dimension change.** Changing `memory.embedding_profile` is a planned re-embed (doc 08 settings table, doc 04 `embedding_profile`). The new profile gets its own `profile_id`; the old profile's rows stay until the re-embed finishes and the operator confirms, then `DeleteForProfileAsync` removes them. Until a note has rows under the new active profile it has no vector seed. Retrieval during the transition uses only the active profile and reports `LexicalOnly = false` only when the vector query ran against a profile that has rows for at least one candidate path; otherwise it reports lexical only for that call.
7. **Failure behaviour.** If the profile is `none`, the provider call fails or times out, the gateway refuses it by policy or quota, or the vector query fails, the retriever runs on lexical seeds alone and sets `LexicalOnly = true` in the result. It never fails the whole retrieval for an embedding failure, and it never retries inside the call beyond one attempt. The audit entry (below) records the reason code (`profile_none`, `provider_error`, `provider_timeout`, `policy_denied`, `vector_query_error`, `no_vectors_yet`), never the query text.
2. **Query embedding.** At retrieval time the retriever calls the `IVaultQueryEmbedder` once for the query text, under the active `embedding_profile`. The call goes through the gateway, so per-agent model policy and quota apply to the calling agent.
3. **Vector seed query.** The retriever asks for the top `memory.query_candidates`-bounded set of blocks by cosine distance (`halfvec`, pgvector HNSW) where `profile_id` equals the active profile and the block's `text_hash` still equals the stored one (a stale vector never seeds), collapses blocks to `doc_id` (a document's vector score is its best block's score), and RRF-fuses the result with the lexical seeds. Since the convergence seeds are doc ids, so no note id mapping is needed. Profiles never mix in one query (doc 08 section 9).
4. **Per-agent scoping.** The vector query is restricted to the paths the caller may read before ranking: the agent's own `agents/<agent>/memory/`, the project area its role allows, and the company tier `company/memory/` (doc 08 section 6.5), using the same `IVaultAccess.CanRead` rule as the lexical seeds. A second, company-only vector query supplies the company seeds of doc 64 section 3A.10. The filter is applied in the query where possible and again on the result. Another agent's private notes never enter the candidate set, so they cannot rank, expand or count.
5. **Personal data.** Encrypted personal blocks are never embedded (doc 08 section 9), so no vector seed can surface them.
6. **Model or dimension change.** Changing `memory.embedding_profile` is a planned re-embed (doc 08 settings table, doc 04 `embedding_profile`). The new profile gets its own `profile_id`; the old profile's rows stay until the re-embed finishes and the operator confirms, then `DeleteForProfileAsync` removes them. Until a note has rows under the new active profile it has no vector seed. Retrieval during the transition uses only the active profile and reports `LexicalOnly = false` only when the vector query ran against a profile that has rows for at least one candidate path; otherwise it reports lexical only for that call.
7. **Failure behaviour.** If the profile is `none`, the embedder returns no vector (`profile_mismatch`), or its vector does not fit the stored profile (the vector query fails: `vector_query_error`), the provider call fails or times out, the gateway refuses it by policy or quota, or the vector query fails, the retriever runs on lexical seeds alone and sets `LexicalOnly = true` in the result. It never fails the whole retrieval for an embedding failure, and it never retries inside the call beyond one attempt. The audit entry (below) records the reason code (`profile_none`, `provider_error`, `provider_timeout`, `policy_denied`, `vector_query_error`, `no_vectors_yet`), never the query text.

**Built (2026-11-20; ported onto the converged main 2026-11-23).** `MemoryGraphRetriever.RetrieveForQueryAsync` takes the query text and lexical seeds, gets vector seeds from `IMemoryVectorSeedSource`, and fuses them with `RrfFusion`. `EmbeddingMemoryVectorSeedSource` takes an optional `IVaultQueryEmbedder` (null gives `profile_none`), embeds the query once under a timeout (default 5 s), and runs `PgMemoryVectorQuery`. The SQL path filter (`MemoryPathFilter`) excludes, for an agent or cover, every `agents/` area but its own and, for a human member, every agent area, plus the optional scope prefixes, before the nearest-block limit; `IVaultAccess.CanRead` and the scope are applied again to each result before ranking. Token counts are asked once for the final candidate set (`MemoryTokenCounter`), so vector seeds and hop targets are packed too. Reason codes are `MemoryLexicalOnlyReasons`. Not built: `policy_denied` is never reported, because `IVaultQueryEmbedder` has no policy signal; a gateway refusal surfaces as `provider_error`. The `profile_mismatch` check against the profile's dimension is not done in the source (the embedder exposes no dimension); a wrong-length vector fails the vector query and reports `vector_query_error`.

### Retrieval operation [PROPOSED]

The server-side operation a model turn uses to ask for context. It is a thin layer over the existing `memory_retrieve` tool (`MemoryToolService.RetrieveAsync`, contracts in `src/Bazaar.Contracts/Memory/MemoryRetrieveContracts.cs`) and the graph retriever. This slice does not add a second tool: it extends the contracts so the graph path and the lexical path return one shape.

Wire shape, in the style of the existing Memory contracts (sealed records in `Bazaar.Contracts.Memory`, plain strings for scope, kind and tier so new values do not break older readers, `MemoryJson.Options`, `IWireCodec` for transport per CLAUDE.md):

- `MemoryContextRequest(AgentId, QueryText, ProjectScope, TokenBudget, MaxHits, AlreadyInContext)`. `AgentId` is the caller. `ProjectScope` is optional: a project slug, or null for the agent's private area plus the project the turn runs in. `AlreadyInContext` is the list of note ids the turn already carries (see turn wiring). `TokenBudget` and `MaxHits` are clamped by the server (see limits).
- `MemoryContextRequest(AgentId, QueryText, ProjectScope, TokenBudget, MaxHits, AlreadyInContext, IncludeCompany, Trigger)`. `AgentId` is the caller. `ProjectScope` is optional: a project slug, or null for the agent's private area plus the project the turn runs in. `IncludeCompany` defaults to true: the company tier is in scope whatever `ProjectScope` says. `Trigger` names why the call runs (`turn_start`, `task_change`, `gc_change`, `new_project`, `tool`), for the injection metrics of doc 64 section 13.5. `AlreadyInContext` is the list of note ids the turn already carries (see turn wiring). `TokenBudget` and `MaxHits` are clamped by the server (see limits).
- `MemoryContextResult(Hits, LexicalOnly, LexicalOnlyReason, TokensUsed, Truncated, AuditId)`. `Hits` reuses `MemoryHitDto` with these additions: `Score`, `Hop` (0 for a seed, 1 or 2 for an expanded neighbour), `ViaNoteId` (the parent for an expanded hit) and `Version`. Provenance for each hit is the existing `Origin`, `Tier`, `Kind` and `Labelled` fields (T1 notes are quoted data, never instructions). `AuditId` is the id of the audit entry for this call.
- Errors use the existing outcome style of `MemoryToolOutcome` with lower snake case codes: `unauthorized`, `bad_request`, `rate_limited`, `too_large`, `unavailable`. An embedding failure is not an error; it is the lexical-only flag.

Behaviour: build lexical seeds from the Vault full-text index, vector seeds as above, call `MemoryGraphRetriever.RetrieveAsync` with the caller's `VaultPrincipal`, then apply tier, scope, kind, recency and decay adjustments (doc 08 section 8), drop ids in `AlreadyInContext`, pack with `TokenBudgetPacker.Pack`, and write the audit entry.

Authorisation:

- The caller is the agent's own session. The server takes the agent identity from the session, not from `AgentId` in the body; a body value that differs from the session identity is refused with `unauthorized`. An agent reads only its own scope plus what its role grants (doc 19, "Agent memory governance", DECIDED).
- The owner may read all. An owner read of another agent's private area names that agent in `AgentId` and is always logged in the durable access log below. The cover agent never reads private memory (doc 08 section 8).
- Taint rules of doc 15 apply: results from T1 notes carry `Labelled = true`. No `memory_retrieve` result raises trust.

Rate and size limits (each a setting under the configurability rule; shipped defaults are PROPOSED values for the owner to confirm, hard caps cannot be raised by settings):

- `memory.context.max_tokens`: default 2000, hard cap 8000 per call.
- `memory.context.max_hits`: default 12, hard cap 50.
- `memory.context.max_query_chars`: default 2000.
- `memory.context.rate_per_agent_per_minute`: default 30. Over the limit returns `rate_limited`; no partial result.
- Graph expansion keeps the existing caps: `MaxHops` default 1, cap 2.

Audit: one entry per call, `memory.context.retrieved`, actor `agent:<id>` (or the owner principal), payload: `agent_scope`, `project_scope`, result count, `tokens_used`, `lexical_only`, the lexical-only reason code, taint tags, and the returned note ids. Never the query text, titles, bodies or vault paths. This matches the data-read record of doc 48 section 5.1 (principal, session, route, object ids and types, result count, taint tags).

**Built (2026-11-20).** In `Bazaar.Server.Constellation` (namespace `Bazaar.Server.Constellation.Memory.Context`), registered by `AddMemoryContext(MemoryContextHostOptions)`, which the host calls after `AddMemoryGraphProjection` when Constellation is enabled:

- Contracts `MemoryContextRequest`, `MemoryContextResult`, `MemoryContextHitDto` and `MemoryContextErrorCodes` in `Bazaar.Contracts.Memory`, in `WireJsonContext`, with golden files. Hits are a separate record (`MemoryContextHitDto`: the `MemoryHitDto` fields plus `DocId`, `Score`, `Hop`, `ViaNoteId`, `Version`, `IsHistory`, `SupersededBy`) so the `memory_retrieve` shape is unchanged. `Origin` is a provenance label (`agent:<id>` for a memory note, `vault` for another doc). `Kind` is the memory kind name or the Vault doc type. `AlreadyInContext` takes note ids or doc ids.
- `IMemoryContextService` / `MemoryContextService`. Caller from the session (`MemoryContextCaller.Agent` or `.Owner`); an agent whose body `AgentId` differs is refused `unauthorized`. Scope: `agents/<AgentId>/memory/`, plus `projects/<ProjectScope>/` when given (the turn runtime passes the turn's project; null means the private area only). Lexical seeds come from `vault_blocks.tsv` with the same SQL path filter as the vector query, then `IVaultAccess`. Quarantined (T0) notes are never packed; T1 excerpts are quoted line by line and `Labelled`; excerpts are capped at `MemoryContextOptions.ExcerptChars` (1600). `TokenBudget` and `MaxHits` of 0 take the setting; larger values are clamped. `Truncated` is true when an eligible candidate was left out by the budget or the hit limit.
- Owner reads: every candidate the owner reads in another agent's private area is passed to `IMemoryAccessLog` when one is registered; a failing log refuses the call with `unavailable`. The durable log is `IMemoryReadLog` (see "Owner-read access log"); when registered, an unloggable read is refused with `unavailable` too.
- Audit: one `memory.context.retrieved` entry per successful call, actor `agent:<id>` or the owner actor, payload `call_id` (returned as `AuditId`), `route`, `session`, `agent_scope`, `project_scope`, `result_count`, `budget_used`, `truncated`, `lexical_only`, `lexical_only_reason`, `taint_tags` (`labelled_quoted_data` when a T1 hit is returned), `doc_ids`, `note_ids`. The key is `budget_used`, not `tokens_used`, because the shared payload guard refuses any key containing "token". A call that cannot be audited is refused with `unavailable`.
- Settings (`MemoryContextSettings`, contributed to the registry): `memory.context.max_tokens` (2000, hard cap 8000), `memory.context.max_hits` (12, cap 50), `memory.context.max_query_chars` (2000, cap 8000), `memory.context.rate_per_agent_per_minute` (30, cap 600). The caps for query length and rate are this build's values, open for the owner with question 3. Validators refuse a value above the cap and the service clamps again. The rate limit is an in-memory sliding window per caller actor, per server process.
- Wire command `memory.context` (`MemoryContextWireHandler`): owner principal only, because wire principals carry no agent identity; agents call the service in process. Errors are wire errors with the codes above.
- `IVaultAccess` defaults to `VaultPathAccess` over the registered `IProjectMembership`. No production membership source exists yet, so until one is registered agents are members of no project and project scopes return nothing for them (deny by default).
- Not built: the tier, kind, recency and decay adjustments of doc 08 section 8, the drop order of "Agent-turn wiring" item 5 beyond the packer, and the turn runtime call.

### Agent-turn wiring [PROPOSED]

How retrieved memory enters a model turn without bloating the context. The turn assembler (doc 06) owns the final prompt; this section only fixes the memory side of the budget.

1. **Budget split.** The turn assembler gives memory a fixed share of the context budget, `memory.context.share_percent` (default 15, range 0 to 40, applied to the model's window after the system prompt and Soul). Recent GC context is filled first up to its own share and is never shrunk to make room for memory. If the share is smaller than the smallest hit, no memory is added and the turn proceeds.
2. **One call per turn.** The runtime issues one retrieval at turn start. The query text is the triggering message plus the agent's current task title, not the whole transcript. A tool call to `memory_retrieve` during the turn is a separate call with its own limits.
3. **Dedup.** The runtime passes `AlreadyInContext`: note ids from earlier retrievals in the same session still in the window, and note ids cited in the recent GC context. The server drops those before packing. A note whose newer version supersedes one already in context is returned and marked with the superseded id in `ViaNoteId`.
4. **Citation.** Each injected note is rendered as a short block carrying its note id, version, tier and kind, and an excerpt. The model is told to cite by note id. The id is the audit and provenance handle, so a claim can be traced back through the Vault and the audit log.
5. **Cap and drop order.** The packer already stops at the token budget. When the result still exceeds the share, drop in this order: expanded hops before seeds; deepest hop first; lowest score first; superseded or older versions before newer; T1 labelled notes before T2 and T3; episodic before semantic, lessons and failures (failures and T3 keep their decay floors, doc 08 section 8). Seeds ranked first are the last to go. The result sets `Truncated = true` when anything was dropped for budget.
6. **Degraded mode.** With `LexicalOnly = true` the turn still gets lexical seeds and expansion. The turn assembler may add one line to the agent's status (not the prompt text) that retrieval is lexical only.

### Cross-project long-horizon context [PROPOSED; mostly bounded by DECIDED text]

What the design already allows, and nothing more:

- **Within one project.** Project-wide context is allowed by role: an agent of a project reads `projects/<slug>/memory/` when its role grants it (doc 19 "Agent memory governance": an agent reads only its own scope unless a role grant says otherwise). The retrieval operation above applies that rule through `IVaultAccess`. Long-horizon here means more notes over time in the agent's own area and its project's area, found by graph expansion and recency/decay weighting, not a wider scope.
- **Across projects.** The only sanctioned path is the Hive copy flow: doc 08 section 6 and doc 25 (Hive Mind). Copies are Vault notes under `hive/` with `origin_project` and `origin_note` front matter; they are never links into another project's Vault or GC text. Allowed kinds, grants per project pair, the sanitizer and the review window are doc 08 and doc 25 settings. A retrieval can return a `hive` note only when the caller's project holds the grant and the note is not T0 or T1. The retriever treats a Hive copy as an ordinary note in the `hive` scope; it adds no cross-project edges, and the graph projector never creates an edge from one project's note to another project's note.
- **Company area.** `company/memory/` notes are readable per role, agents propose and the owner promotes (doc 08 section 6). The Hive and Company areas are not yet in the graph (see Known open items); until they are, retrieval covers the private and project scopes only.

Nothing in this document gives an agent read access to another project's memory, a global cross-project index, or a merged graph across projects. The Constellation view of multiple projects is a rendering concern (doc 19) and does not widen retrieval scope. Gaps are listed as owner questions 1 and 2 below.

### Owner-read access log [PROPOSED]
2. **Proactive calls, tools as fallback.** The runtime issues one retrieval at turn start, and one more when the agent's task changes, its working GC changes, or it starts work in a project for the first time (doc 64 section 3A.11). Each call's query text is built from the new context (the triggering message plus the current task title at turn start), not the whole transcript, and each includes company-tier seeds. A refresh adds only nodes and versions not already in the window. A tool call to `memory_retrieve` or a traversal tool during the turn is a separate call with its own limits, and is counted for the redundant-search and miss rates.
3. **Dedup.** The runtime passes `AlreadyInContext`: note ids from earlier retrievals in the same session still in the window, and note ids cited in the recent GC context. The server drops those before packing. A note whose newer version supersedes one already in context is returned and marked with the superseded id in `ViaNoteId`.
4. **Citation.** Each injected note is rendered as a short block carrying its note id, version, tier and kind, and an excerpt. The model is told to cite by note id. The id is the audit and provenance handle, so a claim can be traced back through the Vault and the audit log.
5. **Company share.** Company items fill a reserved part of the memory share, `memory.context.company_share` (default 0.15 of it, hard cap 0.3); an unused part returns to the general pool. Each company item is labelled with its tier, trust, company kind, confidence and evidence counts, and shows only sources the caller can read (doc 08 section 6.8).
6. **Cap and drop order.** The packer already stops at the token budget. When the result still exceeds the share, drop in this order: expanded hops before seeds; deepest hop first; lowest score first; superseded or older versions before newer; T1 labelled notes before T2 and T3; episodic before semantic, lessons and failures (failures and T3 keep their decay floors, doc 08 section 8). Seeds ranked first are the last to go. The result sets `Truncated = true` when anything was dropped for budget.
7. **Degraded mode.** With `LexicalOnly = true` the turn still gets lexical seeds and expansion. The turn assembler may add one line to the agent's status (not the prompt text) that retrieval is lexical only.

### Cross-project long-horizon context [PROPOSED; owner answer 2026-11-20]

Retrieval scope has three layers:

- **Within one project.** Project-wide context is allowed by role: an agent of a project reads `projects/<slug>/memory/` when its role grants it (doc 19 "Agent memory governance": an agent reads only its own scope unless a role grant says otherwise). The retrieval operation above applies that rule through `IVaultAccess`. Long-horizon here means more notes over time in the agent's own area and its project's area, found by graph expansion and recency/decay weighting, not a wider scope.
- **Across projects: company knowledge (the default cross-project path).** The company knowledge tier, `company/memory/` (doc 08 section 6), is in scope for every agent that is not quarantined, in every project, by default. Its notes are generalised lessons with no project, customer or person names, admitted through the review window, a Council motion or the owner, with T3 set by the owner only. Their `company.evidence` links to source project notes are provenance only (edge kind `evidence`, weight 0): never expanded, and a source is shown only to a caller who can read it. T0 and T1 never appear; candidates never appear.
- **Across projects: Hive copies (explicit, per grant).** The Hive copy flow, doc 08 section 6.1 and doc 25 (Hive Mind), stays the only path by which one project's own lesson text reaches another named project. Copies are Vault notes under `hive/` with `origin_project` and `origin_note` front matter; they are never links into another project's Vault or GC text. A retrieval can return a `hive` note only when the caller's project holds the grant and the note is not T0 or T1. The retriever treats a Hive copy as an ordinary note in the `hive` scope; the graph projector never creates an edge from one project's note to another project's note.

Nothing in this document gives an agent read access to another project's own notes, a global index of project areas, or a merged graph of project areas. The company tier is a shared area of the one graph, not a merge of projects. The Constellation view of multiple projects is a rendering concern (doc 19) and does not widen retrieval scope.

### Owner-read access log [PROPOSED; built for retrieval as constellation migration 0003]

Built: migration `0003_memory_read_log.sql` (`Bazaar.Server.Constellation`, `Sql/components/constellation`), the port `IMemoryReadLog` (`Bazaar.Constellation.Abstractions`), `PgMemoryReadLog` (INSERT-only role and triggers, `memory.owner_read` hash-chain entry per row), `AddMemoryReadLog` in the host, and `MemoryGraphRetriever`, which records one row per subject agent whose area a non-subject principal read, before the result is returned, and throws `MemoryReadLogException` (mapped to `unavailable` by `MemoryContextService`) when a row cannot be written. Not built: the `get`, `list_revisions` and `export` operations (no such read paths exist yet).

Replaces the "hook" idea (a store-side extension point with no durable record) with an append-only record. Doc 08 section 8 already says every owner read of another agent's private area is logged (Q1, default confirmed by the DECIDED line "the owner reads all"), and doc 19 and doc 48 require provenance and read events in the audit log.

- **Record.** One append-only table, `constellation_memory_read_log`, created by a Constellation migration: `read_id` (ULID), `at` (timestamptz), `reader_principal` (owner or other human principal id), `reader_session`, `route` (the tool or wire route), `subject_agent_id` (whose private area was read), `operation` (`retrieve`, `get`, `list_revisions`, `export`), `note_ids` (ids only, capped; a larger read stores a range descriptor and count, as doc 48 section 5.1 allows), `result_count`, `taint_tags`, `purpose` (optional, given by the caller), and `audit_seq` (the `seq` of the matching audit row). No titles, bodies, paths or query text.
- **What is logged.** Every read by a principal other than the subject agent of a private memory area, whether by the owner or by a maintenance job, and every owner read of project memory through the retrieval operation. An agent's reads of its own area go to the audit log only (the retrieval audit entry above), not to this table.
- **Append only.** The application role has insert and select on the table and no update or delete grant. Rows are never edited. A failure to write the row fails the read: an owner read that cannot be logged is refused with `unavailable`, so there is no unlogged owner read.
- **Chain link.** Each row also appends one audit entry (`memory.owner_read`) in the doc 48 section 5.2 hash chain (`seq`, `prev_hash`, `row_hash`, signed checkpoints, external anchoring), and stores that `audit_seq`. The table is the queryable index over the chain; the chain is the tamper-evidence. A verifier mismatch on those rows is reported by the doc 48 verifier.
- **Retention.** Same as the audit log it links to (doc 48 retention and legal hold apply). Rows are kept at least as long as the memory notes they refer to exist. Crypto-erase of a subject removes no log rows, because they hold ids only.
- **Visibility.** The owner can list the log in the client; each agent can see a count and the list of reads of its own private area, unless the owner turns that off (open question 4).

### Open questions for the owner [PROPOSED]

1. Cross-project long-horizon context: is the Hive copy flow (doc 08 section 6, doc 25) the whole answer, or should a read-only, owner-granted cross-project retrieval scope exist beyond copies? This document assumes the former.
2. Should the Hive and Company areas join the memory graph (as ordinary notes in their scope, with no cross-project edges), and in which release?
1. ANSWERED (owner, 2026-11-20): cross-project long-horizon context is company knowledge, the company tier of doc 08 section 6, in scope by default; the Hive copy flow stays the explicit path for project-private lessons; no owner-granted project-to-project scope is added.
2. SETTLED (follows from 1 and the convergence): the Hive and company areas are ordinary Vault notes, so they join the graph with the convergence (doc 64 WP0); the company tier ships with doc 08 work packages 18 to 22.
3. Defaults for `memory.context.*` (share 15 percent, 2000 tokens, 12 hits, 30 calls a minute) and the hard caps.
4. Should an agent be told when the owner reads its private memory (read log visible to the subject agent), or is the log owner-only?
5. Do episodic notes take part in vector seeds from day one, or only semantic, lesson and failure notes, to keep the index small?
6. Embedding model and dimension remain unchosen (doc 08 section 9, evaluation first); retrieval stays lexical-only until the operator picks a profile.

## Tests

- Projector: link resolution, dangling links dropped, provenance edges, deterministic ordering, empty body.
- Retriever: expansion depth, scope filter (an agent never sees another agent's note, even as a hop target), deprecated exclusion, budget packing, lexical-only flag.
- Store (Postgres, through `ConstellationDb`): store, re-store the same version (error), activate an older version (rollback), `GetActive` after rollback, partial unique index.
- Sink: commit success projects and stores; projection failure does not fail the commit.
- Component: `ConstellationComponent` registers the store and the retriever; the Vault graph source is unchanged.

## Build status

Done (2026-11-16):

- Model, projector (`MemoryGraphProjector`), Postgres store (`PgMemoryGraphStore`, migration `constellation/0001`, registered in `ConstellationComponent` as `IMemoryGraphStore`), retriever (`MemoryGraphRetriever`).
- Sink: `MemoryGraphSink` projects only items under `agents/<batch agent>/memory/`. Items that fail to parse, or whose store call throws, are logged and skipped; the commit result is returned unchanged. `MemoryGraphSink.Wrap` is the helper that the memory component wiring calls when it lands. The decorator is not registered globally.
- Tests: projector, retriever, store (Postgres), sink, rebuilder.
- Rebuild: `MemoryGraphRebuilder` (see Rebuild). Wiring it to a startup or admin trigger waits for the production Vault.

Done (2026-11-20), convergence ("Convergence design"):

- Migration `constellation/0002_memory_doc_id` in `Bazaar.Server.Constellation` (`Sql/components/constellation/`, read by the host's `ComponentMigrationSource`).
- `PgMemoryGraphStore` maps each write to its doc (`MemoryDocIdResolution`), backfills earlier rows of the note, and lists history per doc (`ListRevisionsByDocAsync`).
- `IVaultDocIndex` / `PgVaultDocIndex` and `MemoryGraphProjector.ProjectIndexed`: the sink and the rebuilder project a note the index holds onto its doc with the index's resolved links; a note not yet indexed falls back to `Project`.
- `IConstellationNodeReader` / `PgConstellationNodeReader`: doc nodes with memory attributes, live `vault_links` and mapped revision edges. `MemoryGraphRetriever` now takes the node reader instead of the store, and its seeds, token counts and hits are doc ids (`MemoryGraphHit.DocId`; `NoteId`, `ScopeAgentId` and `Trust` are null for a doc that is not a memory note).
- `MemoryGraphDocIdMigrator` (dry run and run). It is registered by `AddMemoryGraphProjection`. It never runs on startup.
- Operator trigger (2026-11-20): no server admin surface for maintenance exists yet (no admin panel module, no admin wire commands), so the trigger is a host subcommand following `migrate`: `Bazaar.Server.Host memory-graph doc-ids` is a dry run that prints the report (counts, then one line per note: note id and doc id, or note id and reason; no paths) and changes nothing; `memory-graph doc-ids --run --confirm` runs the migrator and appends one `memory.graph.doc_ids.migrated` audit entry (actor `operator:cli`; counts and up to 500 mapped note ids). `--run` without `--confirm` is refused. Both refuse a database whose schema is not current. When the admin panel lands, it should call the same migrator with the same confirm and audit rules.
- Tests: unit tests for the projector, sink, rebuilder and retriever; Postgres tests for the store mapping, the doc id guard, the doc index, retrieval across a project decision and a linked project with hidden hop targets, and the migrator dry run and run.

Known open items:

a. The retriever does not re-expand a neighbour reached later with a higher score. The first score wins for expansion. The spec was silent here; this is the chosen behaviour.
b. Cross-note wiki-link resolution by title needs the Vault index. The sink passes an empty title map today, so `[[Title]]` links produce no edge. Only `[[note id]]` links resolve.
c. Not built: retention, crypto-erase, legal hold, the Hive and Company areas, vector seeds, the owner read-audit, HTTP and wire endpoints, rebuild from the Vault, and the client view.
b. Resolved by the convergence: `[[Title]]` links come from the Vault index (`vault_links`). A note written before the Vault indexer has seen its file is stored with no Link edges and no doc id; retrieval still reads its doc's links live once the index catches up.
c. Not built: retention, crypto-erase, legal hold, the Hive and Company areas, the durable owner read log, an HTTP endpoint, the turn runtime call, and the client view. Built since: supersede-head ranking, vector seeds, the retrieval operation and its `memory.context` wire command (see those sections).
d. PROPOSED specifications now exist for vector seeds, the retrieval operation, agent-turn wiring, cross-project context limits and the durable owner-read access log; see "Server-side GraphDRAG completion". None of it is built. Its open questions are listed at the end of that section.
e. GPU-accelerated retrieval is specified in `docs/design/64-gpu-graph-retrieval.md` (PROPOSED).

## Owner questions

Owner: should a retrieval hop be allowed across linked projects beyond Hive copies and role grants (the "interconnected cross-project" part of the core principle), and if so which link kinds count as a project link? None else blocks the built slice; the PROPOSED completion section lists its own questions. Retention, crypto-erase and whether memory rebuilds from the Vault on startup are open in doc 08 section 9 and doc 48. They are not decided here.
ANSWERED (owner, 2026-11-20): the "interconnected cross-project" part of the core principle is company knowledge, reached through the company tier (doc 08 section 6); retrieval hops between project areas stay limited to Hive copies and role grants. Nothing blocks the built slice; the PROPOSED completion section lists its own questions. Retention, crypto-erase and whether memory rebuilds from the Vault on startup are open in doc 08 section 9 and doc 48. They are not decided here.
