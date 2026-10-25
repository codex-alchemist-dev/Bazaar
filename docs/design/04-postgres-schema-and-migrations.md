# PostgreSQL schema and migrations

Status: RECONCILED with DECISIONS-LOG (2026-10-25), cross-doc pass done · Owner questions: 4
Depends on: `01-gc-data-model.md` (GC types, log semantics), `03-server-host-and-providers.md` (`migrate` command, config, providers, settings registry), `14-identity-service-and-passport.md` (key custody), `18-vault.md` (Vault index tables), `33-backup-dr-and-export.md` (restore, backup retention) · Used by: `02-wire-protocol-and-sync.md`, `05-model-gateway.md`, `06-agent-runtime.md`, `07-soul-system.md`, `08-memory-system.md`, `09-work-life-simulation.md`, `10-council-and-voting.md`, `11-reputation-and-peer-review.md`, `12-kanban-and-dependencies.md`, `13-skimmer-bouncer.md`, `15-security-taint-and-approvals.md`, `21-github-integration.md`, `22-email-subsystem.md`, `23-observatory-and-usage.md`, `24-company-systems-core.md`, `25-company-systems-knowledge.md`, `34-testing-strategy.md`

## Purpose
The relational store for Bazaar.Server, limited to conventions, the group-chat (GC) log, the per-person key store, and two small records (schema settings and the embedding profile). Each area's tables are defined by its own design doc (C16); this doc sets how every table is versioned, migrated, partitioned and tested, on Windows and Linux alike.

## Decided inputs
- PostgreSQL 18 with pgvector; Npgsql 10 with one shared `NpgsqlDataSource`; SQL-first repositories; `NpgsqlBatch`; no ORM in the core; embedded versioned SQL migrations (CLAUDE.md; `implementation-plan.md`).
- GCs are append-only and never purged; edits keep every version with an "edited" marker, stored as a full copy per version (Bazaar.md §2; DECISIONS-LOG R1, C09).
- GC log model: `gc_event` plus `gc_message` plus `gc_message_version` (DECISIONS-LOG R1, C09).
- GC fan-out uses Postgres `NOTIFY`; Valkey (Linux) or Garnet (Windows) serve cache and presence only (DECISIONS-LOG R1, C07).
- `gc_event` is hash-partitioned by `gc_id` from the first migration; the partition count is a setting (DECISIONS-LOG R1, R12, C10).
- Erasure: personal fields are stored as per-person encrypted payloads from day 1; erasure destroys the key and appends a tombstone; structure, authorship and timestamps stay. Legal sufficiency needs counsel (DECISIONS-LOG R2, C01).
- Migrations run only through an explicit `migrate` command; the server at startup checks the schema version and refuses to run on a mismatch (DECISIONS-LOG R11, C44).
- One embedding profile per install, chosen at setup, stored as `halfvec` (DECISIONS-LOG R10, C39).
- Vault files (including agent memory) are the source of truth; Postgres holds only their indexes and embeddings (DECISIONS-LOG R11, C38).
- Usage is `gw_usage` in doc 05; `usage_record` is dropped (DECISIONS-LOG R9, C33).
- Search: Postgres full-text on Windows, `pg_search` on Linux, one interface (DECISIONS-LOG R4, C23).

## Design
**[PROPOSED] Components.** Data-source factory (one per process), migration runner, one repository per area, `GcLogWriter`, `GcNotifyListener`, and a `KeyStore` that wraps GC per-person keys under the bootstrap key from doc 14.

**Table ownership.** This doc owns the conventions, `schema_migrations`, `schema_setting`, the GC tables, `person_key` and `embedding_profile`. Every other table is created by its owner doc through a migration in `common/`: agent runtime (06, including turns, steps, leases and inbox items), Soul versions (07), memory and its embeddings (08), schedule and availability (09), votes (10), reputation (11), tasks (12), Passport (14), audit log (15), Vault index (18), usage and quotas (05), activity (23), calendar (24). Digest and other wake kinds (13) are rows in owner tables, not new tables.

**[PROPOSED] Migrations.** Embedded resources in `common/`, `linux/` and `windows/`. The runner applies `common/`, then the host's provider folder. Provider folders may add extensions and indexes only; a test enforces that they never change a table a repository uses.
- `schema_migrations(version, name, provider, checksum sha256, applied_at)`.
- `pg_advisory_lock` for the run; one transaction per file, except files marked `-- @no-transaction` (for `CREATE INDEX CONCURRENTLY`).
- `migrate` refuses an applied file whose checksum has changed. Forward-only; rollback means restore (doc 33).
- Startup reads the highest applied version and refuses to run if the database is older or newer than the binary expects. Startup never runs DDL.
- Two roles: a migration role (DDL) and a runtime role (DML only, with no UPDATE or DELETE on log tables).

**[PROPOSED] Partitioning.** `gc_event` is `PARTITION BY HASH (gc_id)`. The default is **16 partitions** (setting `gc.partition_count`): enough to spread writes on one host, and cheap for small installs. The count is recorded in `schema_setting` at the first `migrate` and written into the DDL when the table is created. `migrate` refuses to start if the configured count differs from the recorded one. Changing the count after data exists needs a planned data move (a copy-and-switch maintenance window, designed later); the setting is free only before the first data.
- `gw_usage` and `usage_hour` are **unpartitioned in v1** [PROPOSED]; revisit after volume is measured. No time partitioning anywhere (C10).

**[PROPOSED] GC append, one transaction.**
1. `UPDATE gc SET head_seq = head_seq + 1 WHERE gc_id = $1 RETURNING head_seq`. `head_seq` is the last assigned seq (doc 01), so the returned value is the seq this write takes. The row lock serialises writers per GC; a rollback also rolls back the counter, so there are no gaps.
2. `INSERT gc_event`, the authoritative row, carrying its provenance. A new message also inserts `gc_message` and its first `gc_message_version`. An edit inserts a `message_edit` event (carrying the editor's provenance) and a new `gc_message_version`.
3. Commit.
4. After commit, hand `{"gc":…,"seq":…}` to the coalescing notifier, which sends `pg_notify('gc', …)` for the newest seq per GC in one transaction every few milliseconds (spike S3: `pg_notify` inside the append transaction gave unstable tails). Nothing is sent for a rolled-back append. Consumers treat it as a hint (see NOTIFY limits).

## Data and interfaces
Conventions: UUIDv7 ids (native `uuidv7()` in PG 18); `timestamptz` in UTC; singular table names; enums are `text` with CHECK; JSONB only for opaque payloads (anything filtered on gets a column); RESTRICT foreign keys, no cascades on log tables. Any column that can hold personal data is stored as `(key_id, ciphertext, nonce)`, never as plaintext.

**Per-person encrypted fields.** `gc_message_version.body`; `council_ballot.reasoning` and `council_ballot_ver.reasoning` (doc 10); `community_action.body` and `bot_profile.contact` (their owner docs); mail bodies (doc 22); vent and Mirror payloads (doc 26). GC bodies are encrypted under the author's key (Question 2).

| Table | Shape | Notes |
|---|---|---|
| `gc` | `gc_id, parent_gc_id, type_id, name, head_seq` | `head_seq` is the last assigned seq (doc 01). `type_id` references the GC type registry in doc 01. A DM is a GC with two members. |
| `gc_member` | `gc_id, principal_id, joined_seq, left_seq` | Keyed by `principal_id`; humans are principals. Principal identity is owned by doc 14. |
| `gc_event` | `gc_id, seq, kind, source_trust, tainted_at_write, taint_sources, derived_from, actor_id, message_id, payload, created_at`; PK `(gc_id, seq)` | Hash-partitioned. **Provenance placement (doc 01):** every provenance column lives on the event row, for every kind. `source_trust` uses doc 15's enum (owner, system, agent, member-human, external, quarantined). `tainted_at_write` is boolean (C20). `taint_sources` and `derived_from` are uuid arrays of source events. A version's provenance is the event that wrote it. Insert-only for the runtime role, plus a trigger that raises on UPDATE and DELETE. |
| `gc_event_kind` | `kind` PK, `description` | Seed rows: `message`, `message_edit`, `system`, `structured` (task and PR state, written by the doc 12 task service), `external_mail` (doc 22; `source_trust = external`; replaces the earlier `external-mail`), `quarantine`, `handover` (doc 15), `council.open`, `council.reveal`, `council.transition`, `council.decision` (doc 10). Other docs add rows, not code. See Question 3. |
| `gc_message` | `message_id, gc_id, seq, author_id` | Insert-only. No current-version column: the highest `gc_message_version` is current. |
| `gc_message_version` | `message_id, version, body_ct, body_nonce, key_id, editor_id, tombstone, created_at`; PK `(message_id, version)` | Full copy per version. Bodies are encrypted under the author's key (Question 2). A tombstone row has no ciphertext. |
| `gc_mention` | `target_id, gc_id, seq` | Feeds "what changed while away" (doc 29). Index `(target_id, seq)`. |
| `person_key` | `key_id, person_id, wrapped_key, created_at, destroyed_at` | GC per-person keys only. Soul and Passport DEKs are doc 14's. `wrapped_key` is wrapped under the bootstrap key (doc 14). Destruction sets `destroyed_at` and clears the wrapped key. |
| `embedding_profile` | `profile_id, provider_id, model_id, dimensions, created_at, active` | One active profile per install, chosen at setup. Changing it means re-embedding. The vector columns live with their owners (08, 18). |
| `schema_setting` | `key, value` | Records values fixed at first `migrate`, such as the partition count. Operator settings are owned by the registry in doc 03. |

### Settings
Owned by the settings registry in doc 03 [PROPOSED]; values are stored in Postgres and every change writes an audit entry (doc 15).

| Key | Type | Default | Scope (install/project/agent) | Who may change | Change needs approval? |
|---|---|---|---|---|---|
| `gc.partition_count` | int | 16 | install | operator, in setup wizard or advanced settings | Yes (owner tier), and only before the first GC data; `migrate` refuses a changed count afterwards |
| `db.migrate_on_fresh_install` | bool | true | install | operator, in setup wizard | No; applies to an empty database only. Upgrades are always a manual `migrate` after a backup |
| `erasure.backup_window_days` | int | 35 | install | operator, with counsel | Yes (owner tier). **Needs counsel.** The 35 days is a placeholder; a shorter window is the more protective choice. Sets backup retention (doc 33) |
| `test.db_source` | text: `auto`, `testcontainers`, `local` | `auto` | install (developer machine) | developer | No. `auto` uses Testcontainers when Docker is reachable, else `BAZAAR_TEST_PG` |

**[PROPOSED] Erasure hook.** Erasure (flow owned by docs 32 and 33) runs one function under a privileged role: it clears the `person_key` row, sets `destroyed_at`, and appends a tombstone version to each affected message. Author, sequence and timestamps remain.

**Lexical and vector search.** One interface: `ts_rank` on Windows, BM25 through `pg_search` on Linux, plus pgvector `halfvec` filtered by `profile_id`, merged by reciprocal rank fusion. The query is defined in doc 18.

**Indexes.** `gc_event (gc_id, seq)` primary key (serves replay); `gc_member (principal_id, gc_id)`; `gc_message (gc_id, seq)`; `gc_mention (target_id, seq)`.

## Behaviour and edge cases
- **Ordering.** Order is per GC by `seq`. No cross-GC order is promised. The counter is gap-free.
- **NOTIFY limits.** Delivered on commit only. A disconnected listener misses notifications, so on reconnect it re-reads `gc_event` above the last delivered `seq` per GC. Keep the listener outside transactions, monitor `pg_notification_queue_usage()`, and never run it behind a transaction-mode pooler.
- **Retention.** GC log, message versions and mentions are never purged. The only removal of personal content is erasure: key destruction plus tombstone (C01). Vault indexes are rebuilt from git; embeddings are rebuilt from the active profile.
- **Backups.** Backups keep wrapped keys until the erasure window passes; backup retention equals that window (`erasure.backup_window_days`, doc 33). Restoring an older backup rolls back the log and is an incident, handled by the restore policy in doc 33.
- **Encrypted bodies and search.** Message bodies are ciphertext, so there is no full-text index over them until Question 4 is decided.
- **Failed migration.** A failed file rolls back its own transaction and releases the lock; the database stays at the last good version and startup refuses to run until `migrate` succeeds.
- **Runtime role.** No UPDATE or DELETE on `gc_event` or `gc_message_version`. Only the erasure function may write `person_key`.

## Testing
Every data test runs against a real PostgreSQL 18 with pgvector.
- **Runner.** Testcontainers when a Docker daemon is reachable; otherwise `BAZAAR_TEST_PG` points at a local server. Selected by `test.db_source` (default `auto`).
- **Speed [PROPOSED].** Migrate one template database per run; each test class clones it with `CREATE DATABASE … TEMPLATE`.
- **Migrations.** Fresh apply; re-run is a no-op; an edited applied file is refused; provider folders change only extensions and indexes (schema diff against `common/`).
- **Startup guard.** The server refuses to start on an older or newer schema; a runtime-role session cannot run DDL.
- **Partitions.** The count is recorded at first `migrate`; a different configured count refuses startup; a lookup by `gc_id` prunes to one partition (EXPLAIN).
- **GC log.** 50 concurrent appends to one GC give seq 1 to 50 with no gaps; a rollback leaves no gap; NOTIFY is seen only after commit; a listener drop and replay returns exactly the missed seqs; runtime-role UPDATE and DELETE are denied on `gc_event` and `gc_message_version`.
- **Erasure.** After key destruction, every version of that person's bodies is unreadable, while author, sequence and timestamps remain and a tombstone is appended.
- **Search.** Contract tests on both providers compare result sets and filters; rank order is asserted per provider only.

## Work packages
1. Migration runner, `migrate` command, history table, migration tests.
2. Data-source factory, configuration, test fixture, template database. Depends on 1.
3. `common/0001` core schema: `gc`, `gc_member`, `gc_event` (hash-partitioned, count from setting `gc.partition_count`), `gc_event_kind` with seed rows, `gc_message`, `gc_message_version`, `gc_mention`, `schema_setting`, roles. Depends on 1.
4. `person_key`, encryption wrapper over doc 14's bootstrap key, erasure function. Depends on 3 and doc 14.
5. `GcLogWriter`, `GcNotifyListener`, replay. Depends on 2, 3 and `Bazaar.Contracts`.
6. `embedding_profile` and setup-time profile creation. Depends on 1.
7. Lexical search provider (`ts_rank` on Windows, `pg_search` on Linux) behind one interface, with contract tests. Depends on 6 and the Vault tables of doc 18 (Wave 3).
8. Backup runbook and restore drill: owned by doc 33; no work package here.

## Conflicts
1. **Erasure beyond the log.** Personal content also sits in Vault memory files and git history (a consequence of C38). Doc 08 and doc 18 need an erasure answer for those. Legal sufficiency of key destruction is unverified and needs counsel (Server.md Q17).
2. **Owner read-all versus per-person keys.** The owner reads all DMs (doc 01), but each person's bodies are encrypted under that person's key. The owner's access path must be defined in docs 01 and 14 (Question 2).
3. **Scale claim.** `(gc_id, seq)` with hash partitions is unbenchmarked (spike S3 pending). The partitioning is a hedge until measured.
4. **Search and licences.** `pg_search` is AGPL; bundling needs counsel. The Windows build stays on `ts_rank` to remain OSI-only (C23). Rankings differ between providers, so the search contract is set-based.
5. **Windows backup.** `pg_basebackup` and WAL archiving under a Windows service account are unverified (doc 33 C4). Windows key custody versus recovery on new hardware is still open (doc 33 C1).
6. **Implementation plan.** `implementation-plan.md` still allows EF Core for CRUD; CLAUDE.md (no ORM in the core) governs and the plan must be corrected.
7. **Requests to other docs.** Doc 06 adds its tables through `common/` migrations and FKs into `gc_member`. Doc 05 owns `gw_usage` with 5-minute and 1-hour cache-write columns. Docs 08 and 18 store embeddings keyed by `embedding_profile`. Doc 15 owns `audit_log`. Doc 01 owns the GC type registry. Doc 33 owns the restore policy and erasure flow. `Bazaar.Server.md` §3 still says "monthly partitioning" and must be corrected.

Moved to owner docs (not decided here): raw usage retention (05), audit retention (15), backup targets (33), reputation weights (11), ballot secrecy (10), cost-chip estimates (05, 23).

## Questions for the owner
ANSWERED: Pub/sub: NOTIFY for GC fan-out; Garnet or Valkey only for cache and presence, with Garnet gated by spike S2 (DECISIONS-LOG R1).
ANSWERED: Embeddings: one profile per install, chosen at setup, `halfvec` (DECISIONS-LOG R10).
ANSWERED: Windows search: Postgres full-text, OSI-only build (DECISIONS-LOG R4).
ANSWERED: Edit storage: full copy per version (DECISIONS-LOG R1).

1. **Restore policy.** Default: adopt doc 33's sequence floor plus a `system` recovery entry, recorded as an exception to doc 01. Why: it is the only restore that never reuses a sequence number. This is still FOLLOW-UP in the log (C10).
2. **Key scope for shared GCs.** Default: each message body is encrypted under its author's key; erasing an author removes that author's text from every GC. Why: the author is the person with the erasure right; the owner's read-all access needs its own grant (docs 01, 14).
3. **Event kinds.** Default: a `gc_event_kind` lookup table, so new kinds are rows. Alternative: a CHECK list in the migration. Why: docs 13, 22 and 26 each add kinds. This is a schema shape choice, so it stays a question.
4. **Search over encrypted messages.** Default: no full-text index over GC bodies; search covers Vault content and non-personal fields until decided. Why: a plaintext index would undo per-person encryption.

MOVED TO SETTINGS: gc.partition_count, db.migrate_on_fresh_install, erasure.backup_window_days, test.db_source (DECISIONS-LOG round 13)
