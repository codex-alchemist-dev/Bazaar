# Vault (company wiki)

Status: RECONCILED with DECISIONS-LOG (2026-10-25), cross-doc pass done · Owner questions: 8
Depends on: 02-wire-protocol-and-sync.md, 03-server-host-and-providers.md, 04-postgres-schema-and-migrations.md, 06-agent-runtime.md, 14-identity-service-and-passport.md, 15-security-taint-and-approvals.md · Used by: 01-gc-data-model.md, 07-soul-system.md, 08-memory-system.md, 10-council-and-voting.md, 17-coding-agent-harness.md, 19-constellation-and-git-solar-system.md, 21-github-integration.md, 24-company-systems-core.md, 25-company-systems-knowledge.md, 33-backup-dr-and-export.md

## Purpose
The Vault is the company's durable written memory: Markdown files with Obsidian-style wiki-links, kept in a private git repo and edited by agents the way code is. It holds almost everything written down, including each agent's persistent memory. PostgreSQL holds the GC log, structured operational state, and the search and embedding indexes over the Vault. Markdown in git is the source of truth; the index can always be rebuilt from it.

## Decided inputs
- Markdown in git is the source of truth; PostgreSQL indexes it (Constellation s3; DECISIONS-LOG round 11).
- The Vault holds every durable written artifact, including agent memory as per-agent private and shared areas. PostgreSQL holds the GC log, structured state and the indexes (C38, round 11). This scope is Claude's reading, to be confirmed (Q7).
- Obsidian-style `[[wiki-links]]`; a project Vault has an Index, a Plan, Deep Dives and Research docs (Constellation s3). The Vault is accessible from all relevant GCs and is one view of the knowledge graph shared with the Constellation (Constellation s1, s3).
- Search is hybrid lexical plus vector, behind one interface: Postgres full-text on Windows, `pg_search` on Linux (Server s3; C23, round 4). OSI-licensed components only; no paid components.
- One embedding profile per install, chosen at setup, stored as `halfvec` (C39, round 10).
- Decision records are `decisions/NNNN-<slug>.md`, written only through `VaultWriter` (C24, round 7).
- Git engine: one `IGitProvider`. LibGit2Sharp stays decided unless spike S23 shows it cannot do worktrees, merges or concurrency; the git CLI is an alternative implementation (C12, round 3).
- Agents act through GitHub Apps, one per agent (Server s2). Git pushes run host-side; tokens never enter a VM (C05, round 2).
- The company repo is private and separate from the public platform repo (Server s3). No Vault content enters the public repo.
- GC history is append-only (Core s2). Constitution (`/company`) and Bazaar Archive are v1 scope (Core s4); the Constitution is not yet drafted.
- The authenticated HTTP surface, including `GET /vault/search`, is specified in 02 (C15, round 6).
- Install-level choices that were open questions are settings (configurability rule, DECISIONS-LOG round 13); see the Settings table.

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
| `agents/<agent-id>/charter.md` | charter | Charter text; its SHA-256 is in `soul_versions` (doc 07) [PROPOSED] |
| `shared/<area>/<slug>.md` | memory, summary | Shared memory and summaries |
| `drafts/<author>/<slug>.md` | draft | Constellation drafts (doc 19) [PROPOSED]; visibility per doc 19 Q3 |
| `twin/<project>/<page>.md` | twin | Generated Twin prose pages (doc 25 names no folder) [PROPOSED] |
| `archive/` | archive | Generated only |

**Front matter [PROPOSED].** YAML, parsed by a real YAML library:

```yaml
id: 01J...            # ULID, required, stable across renames
type: memory          # index|plan|deep-dive|research|constitution|decision|archive|memory|summary|twin|charter|draft
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

Resolution: path first, then title or alias. If several match, prefer the same project, then apply `vault.ambiguous_link_policy`. No match: store as unresolved and show a "create" affordance.

**Parsing [PROPOSED].** Markdig pipeline with its YAML front-matter extension, plus an in-house inline parser for `[[...]]` inserted ahead of Markdig's link parser. Markdig has no wiki-link extension (research s1). Every node keeps its source span. A write replaces only the bytes of the changed node; all other bytes stay identical.

**Write path [PROPOSED].**
1. One `VaultWriter` service per repo owns every write: agents, memory consolidation (08), the Council's decision records (10), and the Archive job. It keeps a queue per document id and merges one commit at a time per vault.
2. An agent edits on its own `agent/<task>` branch in a worktree and submits `{docId, baseCommit, branch, gcId, taskId}`. Memory writes with no GC or task send `gcId` and `taskId` as null.
3. The writer merges the branch into the vault's main branch and records the new commit.
4. `.gitattributes` sets `*.md merge=text`. `merge=union` is never used for Markdown; a test enforces this.
5. Overlapping edits to the same lines: the merge is aborted, the branch is kept, and a conflict card goes to the task's GC with both diffs, offering the actions in `vault.conflict_card_actions`. The agent rebases and resubmits. Non-overlapping edits to one file merge automatically.
6. Moves and renames go through one command that runs `git mv` and rewrites inbound links in the same commit.
7. Batching: memory writes from one agent coalesce into one commit per batch, per `vault.memory_commit_batching`. A commit carries one batch only.
8. Decisions: `VaultWriter` assigns the next free `NNNN` under the queue and returns it in `EditResult`, so the Council (10) can cite `decisions/NNNN-<slug>.md`.

**Commit attribution [PROPOSED].** Author = the agent's GitHub App identity (bot name and email as GitHub issues them; format unverified). Committer = `VaultWriter`. Trailers: `Bazaar-Agent`, `Bazaar-GC`, `Bazaar-Task`, `Bazaar-Edit-Base`.

**Indexing [PROPOSED].** After each commit the writer emits `(oldSha, newSha)`. The indexer runs `git diff --name-status -M old new` and, per changed file in one transaction, re-parses it and replaces its rows. Renames keep the document id. Links into deleted files become unresolved and re-resolve later. Embeddings are recomputed only for changed block hashes, per profile. `vault_state.indexed_commit` lets startup catch up, or triggers a full rebuild. The same pass writes `vault_commit` rows (the Git DAG index for the Vault repo; code repos are owned by doc 17). Owner of the DAG index: this doc for the Vault repo, doc 17 for code repos [PROPOSED].

**Search [PROPOSED].** Two candidate lists: full-text (or BM25 on Linux) over blocks, and HNSW cosine over block embeddings. Fused with Reciprocal Rank Fusion (k from `vault.search.rrf_k`), then collapsed to documents. Results are filtered by the caller's access before ranking. With no embedding provider, search is lexical only and the response says so.

**Access (per-agent privacy) [PROPOSED].**
- Git has no per-path ACLs. The Vault service enforces access: every read, search hit and link resolution checks the caller's principal against the document's `area` and `owner`.
- Defaults: the owner reads everything. An agent reads its own private memory, `company/`, its projects' areas and shared areas. Other agents do not read private memory (08's rule). Peer reads of private memory follow `vault.memory_peer_read` (default: none).
- Review rationale notes (doc 11) are readable by their author, the owner and members of the linked GC. Drafts follow doc 19's default (owner and author).
- Links into a private area that the caller cannot read resolve as unresolved, so existence is not leaked.
- Any full clone exposes every file and all history. Agent workspaces therefore receive only the paths the agent may read (sparse checkout), and the full repo stays on the host. Untested; see Testing.

**Memory [PROPOSED].** Agent memory notes are Markdown files written through `VaultWriter` (C38). Doc 08 owns note content, tiers and consolidation. pgvector only indexes these files.

**GC references [PROPOSED].** A GC message can carry `vaultRef {docId, anchor, commit}`. The commit pins the version the GC discussed, so an append-only GC still reads correctly. The UI offers "this version" and "current".

**Decisions, Constitution, Archive [PROPOSED].**
- Decision records: `status`, `supersedes`, `decidedIn` (a GC message reference). After acceptance only `status` and `superseded_by` may change. The writer enforces this. The decision index is the `vault_docs` rows with `type = decision`; no separate decision table exists. Council and decision requests go to `VaultWriter` (C24).
- Constitution: changes need owner approval, enforced by `vault.merge_approval_paths` (default `company/Constitution.md`). Other paths auto-merge.
- Archive: written by a job under a `bazaar-archive` identity, on the cadence in `vault.archive_cadence`. Never hand-edited.

**Erasure [OPEN].** Personal data in Vault files and in git history has no design. C01 encrypts personal database fields from day 1; the file equivalent is undecided and needs counsel (Q8). Until decided, personal data is kept out of shared areas, the default of `vault.personal_data_in_files`.

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
              embedding halfvec(N),       -- profile and N from setup (C39, Q4)
              PRIMARY KEY (doc_id, ordinal, profile_id))
vault_links  (src_doc, src_ordinal, kind 'link'|'embed', target_raw text,
              target_doc uuid null, target_anchor text null, alias text null)
vault_commit (vault_id, sha char(40), parents char(40)[], author text,
              committed_at timestamptz, refs text[], touched_paths text[],
              gc_id uuid null, task_id uuid null, is_merge bool,
              PRIMARY KEY (vault_id, sha))                      -- [PROPOSED] DAG index
vault_state  (vault_id pk, indexed_commit char(40))
```
GIN on `tsv`; HNSW on `embedding`. On Linux the provider adds a BM25 index on `body`.

```csharp
public interface IVaultWriter {
    Task<EditResult> SubmitAsync(EditRequest request, CancellationToken ct);  // EditResult carries NNNN for decision creates [PROPOSED]
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

### Settings [PROPOSED, doc 03 registry; DECISIONS-LOG round 13]
Keys are typed and shown in the setup wizard and advanced settings (27, 32). Every change is written to `audit_log` (15). Settings may tighten security floors, never loosen them.

| Key | Type | Default | Scope | Who may change | Change needs approval? |
|---|---|---|---|---|---|
| `vault.push_cadence` | duration | 15 min | install | operator | No; sets the Vault RPO in doc 33 |
| `vault.max_document_bytes` | int | 1048576 (1 MiB) | install | operator | No |
| `vault.merge_approval_paths` | list of path prefixes | `company/Constitution.md` | install | owner; Council may propose | Yes (owner) |
| `vault.ambiguous_link_policy` | enum: warn, refuse | warn | install | operator | No |
| `vault.conflict_card_actions` | list | keep mine, keep theirs, agent rebases | install | operator | No |
| `vault.transclusion_render` | bool | false (parse only) | install | operator | No |
| `vault.heading_rename_rewrite` | bool | true | install | operator | No |
| `vault.archive_cadence` | enum: weekly+milestones, weekly, milestones | weekly+milestones | install | operator | No |
| `vault.memory_peer_read` | enum: none, on-request-via-gc | none | agent | owner | Yes (owner); privacy floor |
| `vault.memory_commit_batching` | enum: per-consolidation-run, per-time-window | per-consolidation-run | install | operator | No |
| `vault.search.rrf_k` | int | 60 | install | operator | No |
| `vault.personal_data_in_files` | enum: excluded, encrypted-fields, history-rewrite | excluded | install | operator | Yes; needs counsel. Only `excluded` is usable until Q8 is answered |

## Behaviour and edge cases
- Crash mid-merge: on startup, abort any unfinished merge, remove stale worktrees, and catch the index up from `indexed_commit`.
- Index lag: edit responses return the commit SHA; search responses return the indexed commit so callers can see staleness.
- Title change rewrites title-form links to path form. Heading renames rewrite `#anchor` links when `vault.heading_rename_rewrite` is on. Broken anchors are kept and flagged in the Index, never deleted.
- Documents are capped at `vault.max_document_bytes`. No binary attachments in v1 (Q6).
- Only one writer per repo. Writes from one agent never bypass the queue.
- Every memory write is a commit, so history and clone size grow with memory activity. Unmeasured; S23 measures it.
- LibGit2Sharp ships native libgit2 binaries; packaging is checked in S23. The CLI alternative needs `git` on the host, a provisioning step on Windows.

## Testing
- Parser golden tests: every link form; `[[x]]` in code spans and fences is not a link; front matter survives unchanged.
- Round-trip property test: unchanged documents re-serialise byte-identical; an edit changes only its node's span.
- Git tests on temporary repos, run against every `IGitProvider` implementation: different sections merge; same section yields a conflict card; `*.md` attributes forbid `union`; a rename rewrites links in one commit.
- Access: another agent's private memory is absent from search, reads and link resolution; the owner sees all; a sparse worktree contains no other agent's area.
- Writer tests: per-document ordering; batching; crash injected mid-merge recovers cleanly; decision numbers are unique under concurrent submits.
- Indexer: incremental state equals a full rebuild over a scripted history; renames keep ids; `vault_commit` matches the git log.
- Search: real Postgres (Npgsql fixture); RRF with fixed ranks; fake embedder; a profile change rebuilds only that profile's rows. The pg_search provider runs on Linux only.
- No real model calls. Avalonia.Headless covers link rendering once a Vault view exists.

## Work packages
1. Markdig pipeline, wiki-link inline parser, front-matter reader. Tests: golden and round-trip.
2. Link resolver (path, title, alias, anchors). Pure code. Needs 1.
3. `IGitProvider` with the LibGit2Sharp implementation and temp-repo tests. The CLI implementation only if S23 fails. Needs Wave 0 and S23.
4. `VaultWriter`: queues, branch flow, conflict cards, trailers, memory batching, decision numbering. Needs 3.
5. `vault_*` migrations (including the embedding profile table and `vault_commit`) and SQL repositories. Needs Wave 0.
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
6. **Erasure in files and history.** OPEN (see Erasure; Q8).
7. **Cross-doc fixes this design requires:**
   - 08: memory as Vault files with pgvector as index only; drop its private memory tables and `company` scope (C38, C39).
   - 04: drop `vault_doc`, `vault_block`, `vault_link` and the fixed 1024-dimension vector; this doc owns the `vault_*` tables (C16, C39).
   - 07: drop `decision_records`; decisions go through `VaultWriter` (C24).
   - 10: the Council writes `decisions/NNNN-<slug>.md` through `VaultWriter`, not `decisions/<yyyy-mm-dd>-<slug>.md` under a Council identity (C24).
   - 19: read the `vault_*` tables defined here, not `documents` and `links` (C16).
   - 17 and 21: adopt `IGitProvider`; pushes run host-side (C12, C05).
   - 02: `GET /vault/search` is an endpoint of its authenticated HTTP surface (C15).
   - `docs/deep-dives/Bazaar.Constellation.md` line 30: still reads as a single `pg_search` decision (C23).
8. **Twin folder and drafts.** Doc 25 names no Twin folder, and doc 19's draft visibility (Q3) and `draft` type are open. The `twin/` and `drafts/` paths above are proposals until 25 and 19 confirm them.

## Questions for the owner
1. Repo: a separate private `<company>-vault` repo (default), or a folder in the company repo?
2. Front matter ID: ULID (default), or path only?
3. Block IDs: write `^id` only when something links to the block (default), or always?
4. Embedding model, dimension N, and its gateway lane. Lexical-only until chosen. (One profile per install is answered; see below.)
5. Full-text config: `english` (default) or `simple` for identifiers?
6. v1 attachments (images, PDFs): none (default), or Git LFS?
7. Read access and scope: GCs read their project's Vault and coworkers read `company/` (default); DM content is never indexed (default). Confirm these, and confirm that the Vault holds every durable artifact (C38 reading).
8. Erasure mechanism for `vault.personal_data_in_files` (needs counsel): (a) encrypted personal fields in files with key destruction, matching C01; (b) a history-rewrite policy that ends append-only for this repo; (c) neither, keep personal data out of files (the default).

MOVED TO SETTINGS: vault.push_cadence, vault.max_document_bytes, vault.merge_approval_paths, vault.ambiguous_link_policy, vault.conflict_card_actions, vault.transclusion_render, vault.heading_rename_rewrite, vault.archive_cadence, vault.memory_peer_read, vault.memory_commit_batching, vault.search.rrf_k, vault.personal_data_in_files (DECISIONS-LOG round 13)

ANSWERED: one embedding profile per install, stored as `halfvec`, chosen at setup (DECISIONS-LOG round 10, C39).
