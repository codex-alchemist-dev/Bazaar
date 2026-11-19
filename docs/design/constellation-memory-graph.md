# Constellation memory graph (GraphDRAG, server side)

Status: IN PROGRESS (2026-11-16): projector, store, retriever and sink are built; see Build status. Placement and governance are DECIDED (owner, 2026-11-13; doc 19 "Agent memory governance", DECISIONS-LOG round 11). The rules here are the implementation spec for that placement. They do not change any DECIDED item.

## Scope of this slice

Builds the server-side graph over agent memory notes, the path from a note write to the graph, and graph-expanded retrieval with per-agent scope. In:

1. Graph model and wire-independent records (`Bazaar.Server.Constellation.Memory`).
2. A pure projector: one `MemoryNote` revision becomes one `MemoryGraphRevision` with edges.
3. A Postgres store (`constellation` namespace, migration `0001`): every revision is kept, one revision per note is active, and rollback re-activates an earlier revision.
4. A retriever: RRF-fused seeds, expanded along active edges, filtered by `IVaultAccess`, packed under the token budget.
5. A sink decorator: after `IMemoryBatchSink.CommitAsync` succeeds, the batch's notes are projected and stored.
6. Component wiring in `ConstellationComponent`, and tests for each piece.

Out of this slice (named so nobody assumes they exist): crypto-erase and retention (blocked on the per-person key table, doc 04 and doc 48), legal hold, the Hive and Company areas (doc 08 section 6), vector seeds (the embedding adapter is doc 08 WP6), the owner-audit trail of reads (doc 48; the store exposes the hook only), HTTP and wire endpoints for the graph, and the client view.

## Records

Namespace `Bazaar.Server.Constellation.Memory`. Node ids are note ids (`MemoryNote.Id`). Edge targets use prefixes for non-note nodes: `agent:<id>`, `gc:<id>`, `message:<id>`.

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

1. Fuse the seeds with `RrfFusion.Fuse` (ids are note ids here, not vault paths).
2. Expand up to `options.MaxHops` (default 1, cap 2) along `Link`, `DerivedFrom` and `Supersedes` edges of active revisions. A neighbour's score is the parent's score times the edge weight times 0.5 per hop.
3. Visibility: every candidate's `VaultPath` must pass `IVaultAccess.CanRead(who, path)`. Candidates that fail are dropped before scoring, so a hidden note never shows up as a hop target or a count. Scope is never widened by expansion.
4. Status: only `Active` revisions are returned. Deprecated revisions are kept for history and never returned.
5. Pack with `TokenBudgetPacker.Pack`, using the token counts supplied by the caller.

## Sink

`MemoryGraphSink : IMemoryBatchSink` wraps the real sink. On a successful commit it parses each `MemoryWriteItem.Content` with `NoteDocument.Parse`, projects the note, and calls `StoreRevisionAsync`. A projection failure is logged and does not fail the commit: the Vault is the source of truth and the graph can be rebuilt from it. Rebuild from the Vault is a later slice.

## Audit

Doc 19 ("Agent memory governance", DECIDED) requires that each memory item's source and author are written to the audit log (doc 48). `AuditingMemoryGraphStore` decorates `IMemoryGraphStore` and appends one `AuditEntry` after each change that succeeds:

- `memory.revision.stored` after `StoreRevisionAsync`. Actor `agent:<scope agent id>`. Payload: `note_id`, `version`, `scope_agent_id`, `memory_kind`, `trust`, and the provenance edges as `written_by`, `from_gc` and `from_messages` (ids only). Never the title, the body or the vault path.
- `memory.revision.activated` after `ActivateRevisionAsync` returns true (a rollback). Actor `system:memory`, because the store interface carries no acting principal; the endpoint slice adds one. Payload: `note_id`, `version` and the same provenance as the activated revision.

The payload goes through the shared payload guard, so no secret-like key can enter. The audit append happens after the store call and its exceptions propagate: a revision that could not be audited is reported, not hidden. Failing the audit never rolls the stored revision back (revisions are append-only). The memory component registers the decorator as its own service (`AuditingMemoryGraphStore`) and uses it for the write-path sink when an `IAuditLog` is registered; without one the graph store is used as is. The Constellation registration of `IMemoryGraphStore` is left alone, because component start order is not guaranteed. Callers that roll a note back (the endpoint slice) take the decorator so the rollback is audited too.

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
- Tests: projector, retriever, store (Postgres), sink.

Known open items:

a. The retriever does not re-expand a neighbour reached later with a higher score. The first score wins for expansion. The spec was silent here; this is the chosen behaviour.
b. Cross-note wiki-link resolution by title needs the Vault index. The sink passes an empty title map today, so `[[Title]]` links produce no edge. Only `[[note id]]` links resolve.
c. Not built: retention, crypto-erase, legal hold, the Hive and Company areas, vector seeds, the owner read-audit (the store only exposes the hook), HTTP and wire endpoints, rebuild from the Vault, and the client view.

## Owner questions

None blocking this slice. Retention, crypto-erase and whether memory rebuilds from the Vault on startup are open in doc 08 section 9 and doc 48. They are not decided here.
