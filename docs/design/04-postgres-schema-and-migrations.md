# PostgreSQL schema and migrations

Status: DRAFT for owner review (2026-10-25) · Owner questions: 12
Depends on: `docs/deep-dives/Bazaar.Server.md`, `Bazaar.Soul.md`, `Bazaar.Constellation.md` (design docs 01 to 03 not yet written) · Used by: GC service, agent runtime, Vault indexer, search, usage and quota, backup runbook

## Purpose
The relational store for Bazaar.Server: the GC log, agent records, memory and embeddings, the Vault index and usage. It sets how the schema is versioned, how GC appends reach clients, and how one schema runs on Windows and Linux.

## Decided inputs
- PostgreSQL 18 with pgvector; Npgsql 10 with one shared `NpgsqlDataSource`; SQL-first repositories; `NpgsqlBatch`; no ORM in the core; embedded versioned SQL migrations with a small runner (CLAUDE.md; `docs/implementation-plan.md`).
- GCs are append-only; edits keep every version with an "edited" marker (Bazaar.md §2).
- GC append and `NOTIFY` in one transaction, payload `{gc_id, seq}`, one `LISTEN` connection per process, replay from the log on reconnect (implementation-plan.md).
- Search: `pg_search` on Linux, Postgres full-text on Windows, behind one interface (CLAUDE.md).
- Vault: Markdown in git is the source of truth; Postgres only indexes it (CLAUDE.md).
- No money budgets; limits are quotas and the Work-Life Simulation (Bazaar.md §4).
- Votes reveal independently; one follow-up round (Bazaar.md §5). Passport lives outside the VM disk (Bazaar.Soul.md).

## Design
**[PROPOSED] Components.** Data-source factory (one per process), migration runner, one repository per area, `GcLogWriter`, `GcNotifyListener`.

**[PROPOSED] Migrations.** Embedded resources in `common/`, `linux/` and `windows/`. The runner applies `common/`, then the host's provider folder. Provider folders may only add extensions and indexes, never change a table a repository uses (a test enforces this).
- `schema_migrations(version, name, provider, checksum sha256, applied_at)`.
- `pg_advisory_lock` for the run; one transaction per file, except files marked `-- @no-transaction` (for `CREATE INDEX CONCURRENTLY`).
- A checksum mismatch on an applied file stops startup. Forward-only; rollback means restore.
- Separate migration role (DDL) and runtime role (DML only).

**[PROPOSED] GC append, one transaction.**
1. `UPDATE gc SET next_seq = next_seq + 1 WHERE gc_id = $1 RETURNING next_seq`. The row lock serialises writers per GC; a rollback also rolls back the counter, so there are no gaps.
2. `INSERT gc_event` (the authoritative row); for a message, also `gc_message_version` and the `gc_message` current pointer.
3. `SELECT pg_notify('gc', '{"gc_id":…,"seq":…}')`.
4. Commit. The notification is delivered only on commit.

## Data and interfaces

Conventions: UUIDv7 ids (native `uuidv7()` in PG 18); `timestamptz` UTC; agent-local time derives from `agent.timezone`; enums are `text` with CHECK; JSONB only for opaque payloads (anything filtered on gets a column); RESTRICT foreign keys, no cascades on log tables.

| Area | Tables (outline) | Notes |
|---|---|---|
| GC | `gc(gc_id, parent_gc_id, kind, name, next_seq)`; `gc_member(gc_id, agent_id, joined_seq, left_seq)` | A DM is a GC with two members. |
| GC log | `gc_event(gc_id, seq, kind, actor_id, message_id, payload, created_at)` PK `(gc_id, seq)`; `gc_message(message_id, gc_id, seq, author_id, current_version)`; `gc_message_version(message_id, version, body, editor_id, created_at)` | Insert-only for the runtime role; a trigger also raises on UPDATE and DELETE. |
| Mentions | `gc_mention(target_id, gc_id, seq)` | Feeds the "what changed while away" dashboard. |
| Agents | `agent(agent_id, name, role, timezone, schedule, status)`; `agent_soul_ref(agent_id, soul_id, soul_hash)`; `agent_passport(agent_id, ciphertext, key_id, nonce)`; `agent_model_allow(agent_id, provider_id, model_id)` | Passport holds ciphertext only; the key is outside the DB. Whitelist is [PROPOSED]. |
| Memory | `memory(memory_id, agent_id, scope, body, source_seq, superseded_by)`; `memory_embedding(memory_id, profile_id, embedding halfvec(N))` | scope: private, project, working, lesson. |
| Tasks | `task(task_id, gc_id, title, status, owner_id, version)`; `task_edge(blocker_id, blocked_id)` | "Blocked" is a view. Cycle check with a recursive CTE in the same transaction. |
| Votes | `vote(vote_id, gc_id, subject, opened_at, closed_at)`; `ballot(vote_id, round, agent_id, choice, reasoning, cast_at, revealed_at)` | Queries return ballots only once `revealed_at` is set. |
| Reputation | `reputation_event(event_id, agent_id, source, outcome, ref, created_at)` | Raw events; weights in config (OPEN); scores are a view. |
| Usage | `usage_record(usage_id, agent_id, provider_id, model_id, input_tokens, output_tokens, status, error_code, retry_after_s, created_at)` | Token counts only. |
| Audit | `audit_event(audit_id, actor_id, action, target, decision, details, created_at)` | Insert-only. |
| Vault index | `vault_doc(doc_id, repo_path UNIQUE, commit_sha, content_hash, frontmatter)`; `vault_block(doc_id, block_id, start_line, end_line, body, tsv)`; `vault_link(src_doc_id, src_block_id, target_raw, target_doc_id NULL, target_anchor, kind)`; `vault_tag(doc_id, tag)`; `vault_block_embedding(doc_id, block_id, profile_id, embedding halfvec(N))` | Unresolved links keep `target_doc_id` NULL. Backlinks are a query. Rebuildable from git. |

**Hybrid search [PROPOSED], RRF with k = 60.** Scope filters sit inside both branches, before ranking.
```sql
WITH lex AS (  -- Windows: ts_rank on tsv; Linux: BM25
  SELECT block_id, row_number() OVER (ORDER BY rank DESC) AS r
  FROM lexical_candidates($q, $scope) LIMIT 50),
vec AS (
  SELECT block_id, row_number() OVER (ORDER BY embedding <=> $qv) AS r
  FROM vault_block_embedding WHERE profile_id = $p AND <scope>
  ORDER BY embedding <=> $qv LIMIT 50)
SELECT coalesce(lex.block_id, vec.block_id) AS block_id,
       coalesce(1.0/(60+lex.r), 0) + coalesce(1.0/(60+vec.r), 0) AS rrf
FROM lex FULL JOIN vec USING (block_id) ORDER BY rrf DESC LIMIT 20;
```

**Partitioning.** `usage_record` and `audit_event`: monthly range partitions on `created_at`, created ahead by a job. `gc_event`: unpartitioned until larger than RAM (PostgreSQL's rule of thumb); then hash by `gc_id`, which keeps `(gc_id, seq)` valid. Do not partition by time, because the key would need `created_at`.

**Indexes.** `gc_event (gc_id, seq)` primary key serves replay. `gc_mention (target_id, seq)`. `task (gc_id, status)`; `task_edge (blocked_id)`. Partial HNSW per embedding profile (limits: 2,000 dimensions for `vector`, 4,000 for `halfvec`). GIN on `vault_block.tsv` (Windows) or BM25 (Linux). `usage_record (agent_id, created_at)`.

## Behaviour and edge cases
- **Retention.** GC log, message versions and memory are never purged. Audit is kept (owner to confirm). Raw usage: drop old monthly partitions after an owner-set window, keeping daily aggregates. Vault index is rebuilt, not kept.
- **Backup.** `wal_level = replica`, `archive_mode = on`; test `archive_command` before the first base backup. Nightly `pg_basebackup` plus continuous WAL to an archive, shipped offsite by a file tool. Nightly `pg_dump -Fc` is a logical copy only; it cannot give point-in-time recovery.
- **Restore drill.** Compare GC row counts and last `seq` per GC with the source at a fixed time; confirm no pending migrations. Restoring an older backup rolls back the log: treat it as an incident.
- **NOTIFY limits.** Payload under 8000 bytes by default (ours is about 80). Delivered on commit only; duplicates within one transaction collapse. Not stored: a disconnected listener misses them, so on reconnect re-read `gc_event` per subscribed GC above the last delivered `seq`. The queue is about 8 GB; if it fills, commits fail. Keep the listener outside transactions; monitor `pg_notification_queue_usage()`. Do not run the listener behind a transaction-mode pooler.
- **Ordering.** Order is per GC by `seq`; no cross-GC order is promised.

## Testing
Every data test runs against a real PostgreSQL 18 with pgvector.
- **Runner.** Testcontainers when a Docker daemon is reachable (Docker Desktop on Windows); otherwise `BAZAAR_TEST_PG` points at a local server. The Docker CLI exists on the build machine, but no daemon ran during research.
- **Speed [PROPOSED].** Migrate one template database per run; each test class clones it with `CREATE DATABASE … TEMPLATE`.
- **Migrations.** Fresh apply; re-run is a no-op; an edited applied file is refused; provider folders change only extensions and indexes (schema diff against `common/`).
- **GC log.** 50 concurrent appends to one GC give seq 1 to 50 with no gaps; a rollback leaves no gap; NOTIFY is seen only after commit; listener drop and replay returns exactly the missed seqs; runtime role UPDATE and DELETE on `gc_event` are denied.
- **Search.** Contract tests on both providers compare result sets and filters; rank order is asserted per provider only.
- **Partitions.** Monthly create and drop; EXPLAIN shows pruning.

## Work packages
1. Migration runner, history table, migration tests.
2. Data-source factory, configuration, test fixture, template database. Depends on 1.
3. `common/0001` core schema: gc, gc_event, gc_message, gc_member. Depends on 1.
4. `GcLogWriter`, `GcNotifyListener`, replay. Depends on 2, 3, and `Bazaar.Contracts`.
5. Agent, Soul reference, Passport and model-allow tables. Depends on 3.
6. Tasks, edges, blocked view, votes, reputation. Depends on 3.
7. Usage and audit with partitions and partition job. Depends on 2.
8. Memory and embedding profiles. Depends on 3.
9. Vault index tables and incremental indexer from `git diff`. Depends on Vault writer (Wave 3).
10. Search provider (FTS and BM25) and RRF query. Depends on 8, 9.
11. Backup runbook and restore-drill scripts. No code dependency.

## Conflicts
1. **Erasure versus append-only.** Every version is kept and the log is never purged, yet personal-data erasure may be required. Per-person key destruction is the proposed route; its legal sufficiency is unknown and backups keep keys until expiry. Needs counsel (Bazaar.Server.md §17, OPEN).
2. **Scale claim.** Bazaar.Server.md marks `gc_id` plus timestamp indexing as "sufficient" under RESEARCHED. No Postgres chat benchmark was found (`data_layer_windows.md` §7). Treat as unverified until load-tested.
3. **Garnet scope.** CLAUDE.md names Garnet as the Windows pub/sub provider; GC fan-out is decided on NOTIFY; the feasibility report recommends Garnet only if a benchmark needs it.
4. **Search parity and AGPL.** ts_rank and BM25 rank differently, so the search promise must be set-based. `pg_search` needs AGPL counsel review before any bundle. Minimum PostgreSQL version is 14 (PGXN) or 15 (ParadeDB docs).
5. **ORM.** `implementation-plan.md` allows EF Core for CRUD "if ever needed"; the data-access note recommends EF 10 for Kanban and reputation. CLAUDE.md forbids an ORM in the core; this draft follows CLAUDE.md.
6. **Windows backup.** `pg_basebackup` and WAL archiving under a Windows service account are unverified; the PostgreSQL Windows page returned 404.

## Questions for the owner
1. **Garnet in v1?** Default: NOTIFY only; Garnet only for cache after a benchmark.
2. **Embedding profile.** Default: one profile per install, dimension fixed at setup in the first spike; changing it means re-embedding.
3. **Windows search quality.** Default: ts_rank on the first Windows host; BM25 deferred, avoiding a Linux VM and AGPL review for now.
4. **Raw usage retention.** Default: 90 days of raw rows; daily aggregates kept.
5. **Audit retention.** Default: indefinite in v1.
6. **Edit storage.** Default: full copy per version (matches "every version is kept"); diffs save space but read worse.
7. **Erasure and backups.** Default: per-person keys destroyed on request, after counsel review; backups keep old keys for 35 days.
8. **Backup targets.** Default: WAL shipped hourly (RPO about 1 hour); offsite store chosen later.
9. **Reputation weights.** Default: store raw events; weights in config, set later (OPEN).
10. **Ballot secrecy.** Default: enforced in repository queries plus `revealed_at`; a row-level policy is stricter but adds complexity.
11. **Cost chip.** Default: token counts only. Does the GC cost chip also need an estimated cost, for display only?
12. **Test database.** Default: Testcontainers when Docker is available, else local PostgreSQL 18 via `BAZAAR_TEST_PG`. Is Docker Desktop acceptable on dev machines?
