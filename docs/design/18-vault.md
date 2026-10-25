# Vault (company wiki)

Status: DRAFT for owner review (2026-10-25) · Owner questions: 12
Depends on: `docs/deep-dives/Bazaar.Server.md` (providers, GitHub Apps, repo split), `docs/deep-dives/Bazaar.Constellation.md` (Vault layout, graph) · Used by: `docs/deep-dives/Bazaar.Constellation.md`, `docs/deep-dives/Bazaar.Windows.md` (views), GC messages (section 9). No other design docs exist yet.

## Purpose
The Vault is the company wiki: Markdown files with Obsidian-style wiki-links, kept in a private git repo and edited by agents the way code is. PostgreSQL indexes the files for search and the Constellation graph. It is never the source of truth.

## Decided inputs
- Markdown in git is the source of truth; PostgreSQL indexes it (Constellation s3, 2026-10-24).
- Obsidian-style `[[wiki-links]]`. A project Vault has an Index, a Plan, a Deep Dive per system/API, and Research docs (Constellation s3).
- The Vault is accessible from all relevant GCs (Constellation s3).
- Search is hybrid lexical plus vector. `pg_search` on Linux, Postgres full-text on Windows, behind one interface (Server s3, 2026-10-24).
- Company Constitution (`/company`) and Bazaar Archive are v1 scope (Core s4, 2026-10-24). The Constitution is not yet drafted.
- GC history is append-only; every edit version is kept (Core s2). Decision logs live in the GC record plus Vault docs (Core s5).
- Agents act through GitHub Apps, one per agent, not user accounts (Server s2).
- The company repo is private and separate from the public platform repo (Server s3).
- The Vault and the Constellation are two views of one knowledge graph (Constellation s1).

## Design

**Repository [PROPOSED].** A dedicated private repo per company Vault (Q1).

| Path | Doc type | Notes |
|---|---|---|
| `Index.md` | index | Company home |
| `projects/<slug>/Index.md`, `Plan.md` | index, plan | One each per project |
| `projects/<slug>/deep-dives/<system>.md` | deep-dive | One per system or API |
| `projects/<slug>/research/<topic>.md` | research | Evidence labels kept as written |
| `company/Constitution.md` | constitution | Sections as separate headings |
| `decisions/NNNN-<slug>.md` | decision | Number assigned by the writer |
| `archive/` | archive | Generated only (section 10) |

**Front matter [PROPOSED].** YAML, parsed by a real YAML library:

```yaml
id: 01J...            # ULID, required, stable across renames
type: deep-dive       # index|plan|deep-dive|research|constitution|decision|archive
title: Auth service   # required; no # | ^ : %% [[ ]] characters
project: example      # optional
status: draft         # decisions: proposed|accepted|superseded
aliases: [Auth]
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
1. One `VaultWriter` service per repo owns every write. It keeps a queue per document id and merges one commit at a time per vault.
2. An agent edits on its own `agent/<task>` branch in a worktree and submits `{docId, baseCommit, branch, gcId, taskId}`.
3. The writer merges the branch into the vault's main branch and records the new commit.
4. `.gitattributes` sets `*.md merge=text`. `merge=union` is never used for Markdown; a test enforces this.
5. Overlapping edits to the same lines: the merge is aborted, the branch is kept, and a conflict card goes to the task's GC with both diffs. The agent rebases and resubmits. Non-overlapping edits to one file merge automatically.
6. Moves and renames go through one command that runs `git mv` and rewrites inbound links in the same commit.

**Commit attribution [PROPOSED].** Author = the agent's GitHub App identity (bot name and email as GitHub issues them; format unverified). Committer = `VaultWriter`. Trailers: `Bazaar-Agent`, `Bazaar-GC`, `Bazaar-Task`, `Bazaar-Edit-Base`.

**Indexing [PROPOSED].** After each commit the writer emits `(oldSha, newSha)`. The indexer runs `git diff --name-status -M old new`, then for each changed file in one transaction re-parses it and replaces its rows. Renames keep the document id. Links into deleted files become unresolved, not deleted. Unresolved links re-resolve when a matching title or path appears. Blocks are hashed, and embeddings are recomputed only for changed hashes. `vault_state.indexed_commit` lets startup catch up, or triggers a full rebuild if it is missing.

**Search [PROPOSED].** Two candidate lists: full-text (or BM25 on Linux) over blocks, and HNSW cosine over block embeddings. Fused with Reciprocal Rank Fusion (k = 60 default, Q6), then collapsed to documents. With no embedding provider, search is lexical only and the response says so. Results are filtered by the caller's project access before ranking.

**GC references [PROPOSED].** A GC message can carry `vaultRef {docId, anchor, commit}`. The commit pins the version the GC discussed, so an append-only GC still reads correctly. The UI offers "this version" and "current".

**Decisions, Constitution, Archive [PROPOSED].**
- Decision records: `status`, `supersedes`, `decidedIn` (a GC message reference). After acceptance only `status` and `superseded_by` may change. The writer enforces this.
- Constitution: changes need owner approval (Q2).
- Archive: written by a job under a `bazaar-archive` identity. Never hand-edited.

## Data and interfaces

```sql
vault_docs   (doc_id uuid pk, vault_id, path text unique, title text, type text,
              project text, frontmatter jsonb, head_commit char(40), updated_at timestamptz)
vault_blocks (doc_id, ordinal int, block_id text null, heading_path text[],
              start_line int, end_line int, text_hash bytea, body text,
              tsv tsvector GENERATED ALWAYS AS (to_tsvector('english', body)) STORED,
              embedding halfvec(N) null,              -- N: Q6
              PRIMARY KEY (doc_id, ordinal))
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
public interface IVaultSearch {
    Task<IReadOnlyList<VaultHit>> SearchAsync(VaultQuery query, CancellationToken ct);
}
public interface IVaultGit {   // git CLI implementation first (Conflicts 1)
    Task<string> MergeAsync(string repo, string branch, CancellationToken ct);
    Task<IReadOnlyList<DiffEntry>> DiffAsync(string repo, string oldSha, string newSha, CancellationToken ct);
}
```
Search: `GET /vault/search?q=&project=&type=&limit=`.

## Behaviour and edge cases
- Crash mid-merge: on startup, abort any unfinished merge, remove stale worktrees, and catch the index up from `indexed_commit`.
- Index lag: edit responses return the commit SHA; search responses return the indexed commit so callers can see staleness.
- Title change rewrites title-form links to path form. Heading renames rewrite `#anchor` links. Broken anchors are kept and flagged in the Index, never deleted.
- Documents are capped at 1 MiB [PROPOSED]. No binary attachments in v1 (Q9).
- Only one writer per repo. Writes from one agent never bypass the queue.
- Git must be present on the host. On Windows that is a provisioning step.

## Testing
- Parser golden tests: every link form; `[[x]]` in code spans and fences is not a link; front matter survives unchanged.
- Round-trip property test: unchanged documents re-serialise byte-identical; an edit changes only its node's span.
- Git tests on temporary repos: different sections merge; same section yields a conflict card; `*.md` attributes forbid `union`; a rename rewrites links in one commit.
- Writer tests: per-document ordering; crash injected mid-merge recovers cleanly.
- Indexer: incremental state equals a full rebuild over a scripted history; renames keep ids.
- Search: real Postgres (Npgsql fixture); RRF with fixed ranks; fake embedder. The pg_search provider runs on Linux only.
- No real model calls. Avalonia.Headless covers link rendering once a Vault view exists.

## Work packages
1. Markdig pipeline, wiki-link inline parser, front-matter reader. Tests: golden and round-trip.
2. Link resolver (path, title, alias, anchors). Pure code. Needs 1.
3. Git CLI adapter with per-repo lock and temp-repo tests. Needs Wave 0.
4. `VaultWriter`: queues, branch flow, conflict cards, trailers. Needs 3.
5. `vault_*` migrations and SQL repositories. Needs Wave 0.
6. Incremental indexer and full rebuild. Needs 1, 2, 3, 5.
7. Search interface, FTS provider, pg_search provider, RRF. Needs 5.
8. Move and rename command with link rewrite. Needs 2, 4, 6.
9. Decision and Constitution templates; decision immutability check. Needs 4.
10. HTTP endpoints and `vaultRef` in GC messages. Needs 4, 7 and the Wave 2 GC log.
11. Client Vault and link views in `Bazaar.Client.Core`. Needs 10.

Packages 1 and 3 can run in parallel.

## Conflicts
1. **LibGit2Sharp.** Server s2 and the implementation plan (Wave 3) say LibGit2Sharp. The research could not verify worktree, merge or thread-safety support in libgit2 1.8.x (`github_community_presence.md` s4; `vault_constellation_tech.md` s2). This design uses the git CLI for worktrees and merges. Owner decision or a spike is needed. Bundling `git` in an installer raises GPL-2.0 questions (not verified; needs counsel).
2. **Stale search decision.** Constellation s3 still names `pg_search` as the search decision. Server s3 superseded this on 2026-10-24. Constellation needs a fix.
3. **Windows search is weaker.** Postgres `ts_rank` is below BM25 in ranking quality (unverified against primary docs), and pgvector on Windows needs a manual MSVC build (`data_layer_windows.md` s2). The Windows hybrid path carries more risk than Linux.
4. **Public and private repos.** This doc is in the public repo. Vault content must never enter it. Examples here use placeholders.
5. **Commit email.** The GitHub App bot email format is unverified. Test it on the throwaway repo from Server spike 2.
6. **Markdig wiki-link packages.** Their licences and maintenance are unverified, so this design uses an in-house extension. That is a research recommendation, not an owner decision.

## Questions for the owner
1. Repo: a separate private `<company>-vault` repo (default), or a folder in the company repo?
2. Merge gate: the writer auto-merges all documents (default), or `company/` and `decisions/` need owner approval?
3. Front matter ID: ULID (default), or path only?
4. Ambiguous titles: prefer the same project, then warn (default), or refuse to save?
5. Block IDs: write `^id` only when something links to the block (default), or always?
6. Embedding model, dimension N, its gateway lane, and RRF k (default 60). Lexical-only until chosen?
7. Full-text config: `english` (default) or `simple` for identifiers?
8. Conflict card: offer "keep mine", "keep theirs" and "agent rebases" (default)?
9. v1 attachments (images, PDFs): none (default), or Git LFS?
10. Transclusion `![[...]]`: parse only in v1 (default), or render?
11. Heading renames: rewrite links automatically (default), or leave broken anchors?
12. Read access: GCs read their project's Vault, and all coworkers read `company/` (default). DM content is never indexed. Confirm, and say when Archive runs (default: weekly plus at milestones, under `archive/`).
