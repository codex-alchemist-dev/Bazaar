# Vault (company wiki)

Status: RECONCILED with DECISIONS-LOG (2026-10-25) · Owner questions: 15
Depends on: 02-wire-protocol-and-sync.md, 03-server-host-and-providers.md, 04-postgres-schema-and-migrations.md, 06-agent-runtime.md, 14-identity-service-and-passport.md, 15-security-taint-and-approvals.md · Used by: 01-gc-data-model.md, 07-soul-system.md, 08-memory-system.md, 10-council-and-voting.md, 17-coding-agent-harness.md, 19-constellation-and-git-solar-system.md, 21-github-integration.md, 24-company-systems-core.md, 25-company-systems-knowledge.md, 33-backup-dr-and-export.md

## Purpose
The Vault is the company's durable written memory: Markdown files with Obsidian-style wiki-links, kept in a private git repo and edited by agents the way code is. It holds almost everything written down, including each agent's persistent memory. PostgreSQL holds the GC log, structured operational state, and the search and embedding indexes over the Vault. Markdown in git is the source of truth; the index can always be rebuilt from it.

## Decided inputs
- Markdown in git is the source of truth; PostgreSQL indexes it (Constellation s3; DECISIONS-LOG round 11).
- The Vault holds every durable written artifact, including agent memory as per-agent private and shared areas. PostgreSQL holds the GC log, structured state and the indexes (C38, round 11). This scope is Claude's reading, to be confirmed (Q12).
- Obsidian-style `[[wiki-links]]`; a project Vault has an Index, a Plan, Deep Dives and Research docs (Constellation s3). The Vault is accessible from all relevant GCs and is one view of the knowledge graph shared with the Constellation (Constellation s1, s3).
- Search is hybrid lexical plus vector, behind one interface: Postgres full-text on Windows, `pg_search` on Linux (Server s3; C23, round 4). OSI-licensed components only; no paid components.
- One embedding profile per install, chosen at setup, stored as `halfvec` (C39, round 10).
- Decision records are `decisions/NNNN-<slug>.md`, written only through `VaultWriter` (C24, round 7).
- Git engine: one `IGitProvider`. LibGit2Sharp stays decided unless spike S23 shows it cannot do worktrees, merges or concurrency; the git CLI is an alternative implementation (C12, round 3).
- Agents act through GitHub Apps, one per agent (Server s2). Git pushes run host-side; tokens never enter a VM (C05, round 2).
- The company repo is private and separate from the public platform repo (Server s3). No Vault content enters the public repo.
- GC history is append-only (Core s2). Constitution (`/company`) and Bazaar Archive are v1 scope (Core s4); the Constitution is not yet drafted.
- The authenticated HTTP surface, including `GET /vault/search`, is specified in 02 (C15, round 6).

## Design
**Repository [PROPOSED].** A dedicated private repo per company Vault (Q1). Per-agent areas live in the same repo, behind the access layer below.

| Path | Doc type | Notes |
|---|---|---|
| `Index.md` | index | Company home |
| `projects/<slug>/Index.md`, `Plan.md` | index, plan | One each per project |
| `projects/<slug>/deep-dives/<system>.md` | deep-dive | One per system or API |
| `projects/<slug>/research/<topic>.md` | research | Evidence labels kept as written |
| `company/Constitution.md` | constitution | Sections as separate headings |
| `decisions/NNNN-<slug>.md` | decision | Number assigned by the writer |
| `agents/<agent-id>/memory/<slug>.md` | memory | Private to that agent (Access) |
| `shared/<area>/<slug>.md` | memory, summary | Shared memory and summaries |
| `archive/` | archive | Generated only |

**Front matter [PROPOSED].** YAML, parsed by a real YAML library:

```yaml
id: 01J...            # ULID, required, stable across renames
type: memory          # index|plan|deep-dive|research|constitution|decision|archive|memory|summary|twin
title: Auth service   # required; no # | ^ : %% [[ ]] characters
project: example      # optional
status: draft         # decisions: proposed|accepted|superseded
aliases: [Auth]
owner: agent:<id>     # memory only: the agent whose private area this is
source_trust: agent   # provenance, doc 15's enum (C20)
tier: T1              # memory promotion tier T0-T3, doc 08's scale (C20)
```

**Links [PROPOSED].**

| Form | Target |
|---|---|
| `[[projects/x/Plan]]` | by vault path, no `.md` |
| `[[Auth service]]` | by title or alias, case-insensitive |
| `[[Auth service\|the auth doc]]` | display text |
| `[[Auth service#Token flow]]` | heading (slug match) |
| `[[Auth service#^a1b2c3]]` | block ID |
| `[[#Token flow]]` | same document |

Resolution: path first, then title or alias. If several match, prefer the same project, then raise an ambiguity warning (Q4). No match: store as unresolved and show a "create" affordance.

**Parsing [PROPOSED].** Markdig pipeline with its YAML front-matter extension, plus an in-house inline parser for `[[...]]` inserted ahead of Markdig's link parser. Markdig has no wiki-link extension (research s1). Every node keeps its source span. A write replaces only the bytes of the changed node; all other bytes stay identical.

**Write path [PROPOSED].**
1. One `VaultWriter` service per repo owns every write: agents, memory consolidation (08), the Council's decision records (10), and the Archive job. It keeps a queue per document id and merges one commit at a time per vault.
2. An agent edits on its own `agent/<task>` branch in a worktree and submits `{docId, baseCommit, branch, gcId, taskId}`. Memory writes with no GC or task send `gcId` and `taskId` as null.
3. The writer merges the branch into the vault's main branch and records the new commit.
4. `.gitattributes` sets `*.md merge=text`. `merge=union` is never used for Markdown; a test enforces this.
5. Overlapping edits to the same lines: the merge is aborted, the branch is kept, and a conflict card goes to the task's GC with both diffs. The agent rebases and resubmits. Non-overlapping edits to one file merge automatically.
6. Moves and renames go through one command that runs `git mv` and rewrites inbound links in the same commit.
7. Batching: memory writes from one agent coalesce into one commit per batch (default: per consolidation run, Q15). A commit carries one batch only.

**Commit attribution [PROPOSED].** Author = the agent's GitHub App identity (bot name and email as GitHub issues them; format unverified). Committer = `VaultWriter`. Trailers: `Bazaar-Agent`, `Bazaar-GC`, `Bazaar-Task`, `Bazaar-Edit-Base`.

**Indexing [PROPOSED].** After each commit the writer emits `(oldSha, newSha)`. The indexer runs `git diff --name-status -M old new` and, per changed file in one transaction, re-parses it and replaces its rows. Renames keep the document id. Links into deleted files become unresolved and re-resolve later. Embeddings are recomputed only for changed block hashes, per profile. `vault_state.indexed_commit` lets startup catch up, or triggers a full rebuild.

**Search [PROPOSED].** Two candidate lists: full-text (or BM25 on Linux) over blocks, and HNSW cosine over block embeddings. Fused with Reciprocal Rank Fusion (k = 60 default, Q6), then collapsed to documents. Results are filtered by the caller's access before ranking. With no embedding provider, search is lexical only and the response says so.

**Access (per-agent privacy) [PROPOSED].**
- Git has no per-path ACLs. The Vault service enforces access: every read, search hit and link resolution checks the caller's principal against the document's `area` and `owner`.
- Defaults (Q13): the owner reads everything. An agent reads its own private memory, `company/`, its projects' areas and shared areas. Other agents do not read private memory (08's rule).
- Links into a private area that the caller cannot read resolve as unresolved, so existence is not leaked.
- Any full clone exposes every file and all history. Agent workspaces therefore receive only the paths the agent may read (sparse checkout), and the full repo stays on the host. Untested; see Testing.

**Memory [PROPOSED].** Agent memory notes are Markdown files written through `VaultWriter` (C38). Doc 08 owns note content, tiers and consolidation. pgvector only indexes these files.

**GC references [PROPOSED].** A GC message can carry `vaultRef {docId, anchor, commit}`. The commit pins the version the GC discussed, so an append-only GC still reads correctly. The UI offers "this version" and "current".

**Decisions, Constitution, Archive [PROPOSED].**
- Decision records: `status`, `supersedes`, `decidedIn` (a GC message reference). After acceptance only `status` and `superseded_by` may change. The writer enforces this. The decision index is the `vault_docs` rows with `type = decision`; no separate decision table exists. Council and decision requests go to `VaultWriter` (C24).
- Constitution: changes need owner approval (Q2).
- Archive: written by a job under a `bazaar-archive` identity. Never hand-edited.

**Erasure [OPEN].** Personal data in Vault files and in git history has no design. C01 encrypts personal database fields from day 1; the file equivalent (encrypted bodies, or a history-rewrite policy that ends the no-rewrite rule for this repo) is undecided and needs counsel (Q14). [PROPOSED] Until decided, personal data is kept out of shared areas.

## Data and interfaces
This doc owns the `vault_*` tables (C16). Doc 04 keeps conventions only.

```sql
vault_docs   (doc_id uuid pk, vault_id, path text unique, title text, type text,
              project text, area text, owner text null,
              frontmatter jsonb, head_commit char(40), updated_at timestamptz)
vault_blocks (doc_id, ordinal int, block_id text null, heading_path text[],
              start_line int, end_line int, text_hash bytea, body text,
              tsv tsvector GENERATED ALWAYS AS (to_tsvector('english', body)) STORED,
              PRIMARY KEY (doc_id, ordinal))
vault_block_embedding (doc_id, ordinal, profile_id, text_hash bytea,
              embedding halfvec(N),       -- profile and N from setup (C39, Q6)
              PRIMARY KEY (doc_id, ordinal, profile_id))
vault_links  (src_doc, src_ordinal, kind 'link'|'embed', target_raw text,
              target_doc uuid null, target_anchor text null, alias text null)
vault_state  (vault_id pk, indexed_commit char(40))
```
GIN on `tsv`; HNSW on `embedding`. On Linux the provider adds a BM25 index on `body`.

```csharp
public interface IVaultWriter {
    Task<EditResult> SubmitAsync(EditRequest request, CancellationToken ct);
    Task<MoveResult> MoveAsync(Guid docId, string newPath, CancellationToken ct);
}
public interface IVaultSearch {   // VaultQuery carries the caller's principal
    Task<IReadOnlyList<VaultHit>> SearchAsync(VaultQuery query, CancellationToken ct);
}
public interface IGitProvider {   // C12: LibGit2Sharp decided unless S23 fails
    Task<string> AddWorktreeAsync(string repo, string branch, IReadOnlyList<string> paths, CancellationToken ct);
    Task<string> MergeAsync(string repo, string branch, CancellationToken ct);
    Task<IReadOnlyList<DiffEntry>> DiffAsync(string repo, string oldSha, string newSha, CancellationToken ct);
}
```
Search endpoint, on the authenticated surface of 02: `GET /vault/search?q=&project=&type=&limit=`.

## Behaviour and edge cases
- Crash mid-merge: on startup, abort any unfinished merge, remove stale worktrees, and catch the index up from `indexed_commit`.
- Index lag: edit responses return the commit SHA; search responses return the indexed commit so callers can see staleness.
- Title change rewrites title-form links to path form. Heading renames rewrite `#anchor` links. Broken anchors are kept and flagged in the Index, never deleted.
- Documents are capped at 1 MiB [PROPOSED]. No binary attachments in v1 (Q9).
- Only one writer per repo. Writes from one agent never bypass the queue.
- Every memory write is a commit, so history and clone size grow with memory activity. Unmeasured; S23 measures it.
- LibGit2Sharp ships native libgit2 binaries; packaging is checked in S23. The CLI alternative needs `git` on the host, a provisioning step on Windows.

## Testing
- Parser golden tests: every link form; `[[x]]` in code spans and fences is not a link; front matter survives unchanged.
- Round-trip property test: unchanged documents re-serialise byte-identical; an edit changes only its node's span.
- Git tests on temporary repos, run against every `IGitProvider` implementation: different sections merge; same section yields a conflict card; `*.md` attributes forbid `union`; a rename rewrites links in one commit.
- Access: another agent's private memory is absent from search, reads and link resolution; the owner sees all; a sparse worktree contains no other agent's area.
- Writer tests: per-document ordering; batching; crash injected mid-merge recovers cleanly.
- Indexer: incremental state equals a full rebuild over a scripted history; renames keep ids.
- Search: real Postgres (Npgsql fixture); RRF with fixed ranks; fake embedder; a profile change rebuilds only that profile's rows. The pg_search provider runs on Linux only.
- No real model calls. Avalonia.Headless covers link rendering once a Vault view exists.

## Work packages
1. Markdig pipeline, wiki-link inline parser, front-matter reader. Tests: golden and round-trip.
2. Link resolver (path, title, alias, anchors). Pure code. Needs 1.
3. `IGitProvider` with the LibGit2Sharp implementation and temp-repo tests. The CLI implementation only if S23 fails. Needs Wave 0 and S23.
4. `VaultWriter`: queues, branch flow, conflict cards, trailers, memory batching. Needs 3.
5. `vault_*` migrations (including the embedding profile table) and SQL repositories. Needs Wave 0.
6. Incremental indexer and full rebuild. Needs 1, 2, 3, 5.
7. Search interface, FTS provider, pg_search provider, RRF, per-profile vectors. Needs 5.
8. Move and rename command with link rewrite. Needs 2, 4, 6.
9. Decision and Constitution templates; decision immutability check. Needs 4.
10. `vaultRef` in GC messages and the `/vault/search` endpoint on 02's surface. Needs 4, 7 and the GC log.
11. Client Vault and link views in `Bazaar.Client.Core`. Needs 10.
12. Access layer: principal checks in reads, search and links; sparse workspace paths. Needs 4, 7.

Packages 1 and 3 can run in parallel; 3 starts after S23 reports.

## Conflicts
1. **Git engine pending.** S23 has not run. LibGit2Sharp's worktree, merge and thread-safety support is unverified (`github_community_presence.md` s4; `vault_constellation_tech.md` s2). If S23 fails, the CLI becomes the implementation, and bundling `git` (GPL-2.0) needs counsel (unverified).
2. **Windows search is weaker.** Postgres `ts_rank` is below BM25 in ranking quality (unverified against primary docs), and pgvector on Windows needs a manual MSVC build (`data_layer_windows.md` s2). The Windows hybrid path carries more risk than Linux.
3. **Commit email.** The GitHub App bot email format is unverified. Test it on the throwaway repo from Server spike 2.
4. **Markdig wiki-link packages.** Licences and maintenance are unverified; the in-house extension is a research recommendation, not an owner decision.
5. **Per-agent privacy in one repo.** Git has no ACLs. The access layer and sparse workspaces are designed here but untested, and history keeps every file ever stored (C38 consequence).
6. **Erasure in files and history.** OPEN (see Erasure; Q14).
7. **Cross-doc fixes this design requires:**
   - 08: memory as Vault files with pgvector as index only; drop its private memory tables and `company` scope (C38, C39).
   - 04: drop `vault_doc`, `vault_block`, `vault_link` and the fixed 1024-dimension vector; this doc owns the `vault_*` tables (C16, C39).
   - 07: drop `decision_records`; decisions go through `VaultWriter` (C24).
   - 10: the Council writes `decisions/NNNN-<slug>.md` through `VaultWriter`, not `decisions/<yyyy-mm-dd>-<slug>.md` under a Council identity (C24).
   - 19: read the `vault_*` tables defined here, not `documents` and `links` (C16).
   - 17 and 21: adopt `IGitProvider`; pushes run host-side (C12, C05).
   - 02: `GET /vault/search` is an endpoint of its authenticated HTTP surface (C15).
   - `docs/deep-dives/Bazaar.Constellation.md` line 30: still reads as a single `pg_search` decision (C23).

## Questions for the owner
1. Repo: a separate private `<company>-vault` repo (default), or a folder in the company repo?
2. Merge gate: `decisions/` and `company/` auto-merge (default), or do they need owner approval? Constitution changes already need approval.
3. Front matter ID: ULID (default), or path only?
4. Ambiguous titles: prefer the same project, then warn (default), or refuse to save?
5. Block IDs: write `^id` only when something links to the block (default), or always?
6. Embedding model, dimension N, its gateway lane, and RRF k (default 60). Lexical-only until chosen. (One profile per install is answered; see below.)
7. Full-text config: `english` (default) or `simple` for identifiers?
8. Conflict card: offer "keep mine", "keep theirs" and "agent rebases" (default)?
9. v1 attachments (images, PDFs): none (default), or Git LFS?
10. Transclusion `![[...]]`: parse only in v1 (default), or render?
11. Heading renames: rewrite links automatically (default), or leave broken anchors?
12. Read access and scope: GCs read their project's Vault and coworkers read `company/` (default); DM content is never indexed (default); Archive runs weekly plus at milestones (default). Confirm these, and confirm that the Vault holds every durable artifact (C38 reading).
13. Private memory: readable by its agent and the owner only (default, per 08), or readable by other agents on request through a GC?
14. Erasure in Vault files and git history: (a) encrypted personal fields in files with key destruction, matching C01; (b) a history-rewrite policy that ends append-only for this repo; (c) wait for counsel and keep personal data out of files meanwhile (rec).
15. Memory commit batching: one commit per consolidation run (default), or per time window?

ANSWERED: one embedding profile per install, stored as `halfvec`, chosen at setup (DECISIONS-LOG round 10, C39).
