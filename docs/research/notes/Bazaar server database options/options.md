# Bazaar.Server database options

Status: PROPOSED (research note, not a design decision). Date: 2026-10-30.
Triggered by: the owner's surprise that Bazaar.Server needs a separate PostgreSQL server, and the follow-up question whether a different or custom store would be more novel and performant, or what fully switching to SQLite would imply.

This note does not start any migration. Nothing here changes DECIDED items in `docs/design/DECISIONS-LOG.md`. Where a claim is not checked against a primary source it is marked **[UNVERIFIED]**.

---

## 1. Bottom line

1. **Keep PostgreSQL 18 as the single engine for the full server.** Solve the "separate server" problem with packaging, not with a different database (sections 5 and 6).
2. **A full switch to SQLite is feasible but expensive** (about 6 to 9 engineer-weeks for the data layer and its tests, plus a permanent second-class mode). It gives one-file install and simpler backup. It removes database-enforced guarantees the design relies on (roles, column grants, DB-level append-only triggers, LISTEN/NOTIFY across processes). Those guarantees are the reason for the Postgres choice, so the trade is not worth it for the main product.
3. **SQLite is a good fit for an optional single-user mode, or as a backup and export format,** if the owner wants that later. Decide it as a setting, not as the default engine.
4. **A custom event-log store is not recommended.** It would take months to make durable, crash-safe, recoverable and queryable, and it would have to rebuild FTS, vector search, JSON and the access control that Postgres gives today.
5. **"More novel" is not a reason to change the store.** The principles are Novel and Performant, but the novelty of Bazaar is in the agent and group-chat model. Storage should be boring and proven, which also fits Forward-facing.

---

## 2. What Bazaar.Server actually needs from its database

Checked in the repo (`src/Bazaar.Server/Data/Sql/common/`, `src/Bazaar.Server/Gc/`, `src/Bazaar.Server.Host/Startup/`):

| Need | Where it is used today | Notes |
|---|---|---|
| **PostgreSQL 18 functions** | `DEFAULT uuidv7()` on nearly every id column (0001, 0008, 0009, 0010, 0012, 0016) | Hard dependency on PG 18 (the dev run script says so). |
| **Vector search** | `CREATE EXTENSION vector`, `halfvec` embedding columns with a dimension trigger (0009) | pgvector. |
| **Full-text search** | `tsvector GENERATED ... STORED` with GIN, `'simple'` and `'english'` configs (0007, 0009) | Also the Windows search provider (doc 04 / C23). |
| **Hash partitioning** | `gc_event`, `gc_message`, `gc_message_version`, `gc_mention`, `entry_ref` are `PARTITION BY HASH (gc_id)` (0001, 0008) | Partition count is a setting (`gc.partition_count`, default 16). |
| **Pub/sub fan-out** | `pg_notify` in `CoalescingGcNotifier`; one `LISTEN gc` connection in `GcNotifyListener` with reconnect and resend | Doc 04 / C07: GC fan-out is NOTIFY. Cache and presence are Valkey or Garnet. |
| **Append-only enforcement in the database** | PL/pgSQL triggers that refuse UPDATE, DELETE and TRUNCATE on `gc_event`, `gc_message`, `gc_message_version`, `audit_log`, `task_events`, `person_key`, `gc_member` (0001, 0004, 0008, 0012) | Stronger than application checks, and it matches the "append-only, never purged" decision. |
| **Roles and column grants** | `bazaar_app` (DML only, `UPDATE` on named columns), `bazaar_erasure` (EXECUTE on `erase_person_key`), an audit role (INSERT and SELECT only), tested in `AuditRoleTests`, `ApprovalRoleTests` | Tamper resistance in depth. Tests in `tests/Bazaar.Server.Tests/Security/`. |
| **Advisory locks** | Instance lock (`pg_advisory_lock`), migration lock, audit hash chain under an advisory lock, org `reports_to` cycle check (`pg_advisory_xact_lock`, 0018) | |
| **Queue claims** | `FOR UPDATE SKIP LOCKED` in the job queue (0006) | |
| **JSON** | `jsonb` payloads; the audit hash uses canonical `jsonb::text`; soul preferences deliberately use `text` to keep key order (0014) | |
| **Concurrency** | Several agents and GCs write at once. The S3 spike (`docs/spikes/S3.md`) sets the pass criteria: p95 append under 50 ms at 500 appends/s with NOTIFY on, gap-free sequences under 200 simultaneous appends to one GC, and listener-reconnect recovery. | Results are not summarised in this note. Check S3 before relying on performance claims. |

**Scale.** Bazaar is a small number of humans and agents on one host. Write rates are likely in the hundreds per second at peak, not thousands. The S3 targets are a fair bar for any candidate.

---

## 3. Options compared

Scoring is relative to Bazaar's needs (section 2), with the owner's platforms in mind: a Windows PC first, then Linux.

| Option | Concurrent writers | Append-only and DB-enforced rules | FTS | Vector | JSON | Windows self-host | Ops burden | Fit |
|---|---|---|---|---|---|---|---|---|
| **A. PostgreSQL 18 (status quo)** | Strong, MVCC | Triggers, roles, column grants | Strong, GIN | pgvector (HNSW, halfvec) | jsonb | Native EDB installer; pgvector needs an MSVC build (research note `data_layer_windows.md`) | Separate server, unless packaged | Best fit |
| **B. SQLite only (WAL)** | One writer at a time; fine for this load if writes are batched | Triggers only (RAISE ABORT). No roles, no grants, no TRUNCATE, no LISTEN | FTS5 (BM25 ranking, porter stemmer) | sqlite-vec (brute-force or IVF-style; **[UNVERIFIED]** current index types) | JSON1 functions on TEXT | Single file, no service, trivial | Lowest | Good for single-user; weakens guarantees |
| **C. SQLite plus PostgreSQL (two modes)** | As B for single-user, as A for multi-human | Mode-dependent | Mode-dependent | Mode-dependent | Mode-dependent | Both paths | Two data layers to test | Only if the single-user mode is wanted |
| **D. DuckDB** | Single writer; built for analytics | Weak | Weak | Extensions exist | Good | Native .NET bindings | Low | Analytics only (Observatory, reports), not the system of record |
| **E. FoundationDB** | Excellent, transactional key-value | Build it yourself | Build it yourself | Build it yourself | Build it yourself | **[UNVERIFIED]** server is Linux and macOS only, so Windows self-host is unlikely to be supported | High | Rejected: overkill and wrong platform |
| **F. LMDB or RocksDB plus your own indexes** | Single writer (LMDB) or good (RocksDB) | Build it yourself | Build it yourself | Build it yourself | Build it yourself | .NET bindings exist (**[UNVERIFIED]** versions and Windows status) | High | Rejected: rebuilds the whole query layer |
| **G. Custom event-log store** | Whatever you build | Build it yourself | Build it yourself | Build it yourself | Build it yourself | Yours to make work | Very high | Rejected: the hardest part (durability, recovery, indexes, access control) is what a database already does |

Notes:
- **Event-log shape.** The GC log is an append-only sequence per GC with per-GC sequence numbers. That maps well onto any of A, B or F. It does not need a special engine. A plain table with an index on `(gc_id, seq)` does it; the S3 spike measures that.
- **Hash partitioning** is a Postgres feature. In SQLite the same effect comes from sharding files, or from doing nothing: one file per install is usually enough at this scale.

---

## 4. What a full switch to SQLite would break or require

Concrete, file by file. Estimates assume the existing SQL-first repositories are kept, with a dialect layer where needed.

### 4.1 Migrations (`src/Bazaar.Server/Data/Sql/common/`, 0001 to 0018)
- **`uuidv7()` defaults:** must move to application code. Ids are generated in C# (for example a UUIDv7 library or a small in-house generator). Easy, but every insert path changes.
- **`PARTITION BY HASH`:** dropped. The partition-count setting and the `schema_setting` check for it go away.
- **PL/pgSQL triggers:** rewritten as SQLite triggers with `RAISE(ABORT, ...)`. Append-only triggers for UPDATE and DELETE can be written. **TRUNCATE triggers have no equivalent**, because SQLite has no TRUNCATE. The DELETE trigger covers the same case, and the `BEFORE TRUNCATE` tests have to be removed.
- **Functions** (`erase_person_key`, `org_relation_write_check`, `vault_block_embedding_dims`, `person_key_guard`): rewritten in application code or as triggers. `erase_person_key` is a SECURITY DEFINER-style function today; in SQLite it becomes an application-level operation with no separate role.
- **Roles and column-level GRANTs:** **no equivalent.** There is no database-enforced separation between the server and a migration or erasure identity. The DB-level tamper tests (`AuditRoleTests`, `AuditTamperTests`, `ApprovalRoleTests`, the `person_key` guard) lose their meaning and must be rewritten as application tests, with a weaker guarantee. This is the most important loss.
- **`tsvector` generated columns and GIN:** replaced by an FTS5 virtual table kept in sync by triggers. Ranking changes (BM25 instead of `ts_rank`), and the `'english'` stemming becomes the porter tokenizer. Results will differ and need new golden tests.
- **`halfvec` and the pgvector extension:** replaced by sqlite-vec with float32 (or int8) vectors. `halfvec` is lost. The dimension check moves to application code or a trigger. **[UNVERIFIED]** how sqlite-vec scales to the vector counts Vault will produce. Assume brute force until measured.
- **`jsonb`:** becomes `TEXT` with JSON1 functions. Canonical-text hashing (the audit chain) gets simpler, because TEXT is stored byte for byte. Queries using `@>` or jsonb indexes must be rewritten. There are few of these.
- **Schema versioning, `schema_migrations`, checksums:** can stay. Advisory locks become `BEGIN IMMEDIATE` plus an in-process lock.

### 4.2 Queries and repositories (SQL-first, Npgsql)
- **Driver:** Npgsql is replaced with `Microsoft.Data.Sqlite`. The `NpgsqlDataSource` pattern, `NpgsqlBatch` and `NpgsqlCommand` all change. `$1` positional parameters become named parameters.
- **Types:** `timestamptz` becomes ISO-8601 TEXT or INTEGER epoch. `uuid` becomes TEXT or BLOB. Time-zone handling is the application's job.
- **`SKIP LOCKED` and row locks:** replaced by a single writer, so the job claim becomes an `UPDATE ... RETURNING` inside `BEGIN IMMEDIATE`. Works, but serialises all writes.
- **`INSERT ... ON CONFLICT`, `RETURNING`:** both supported in SQLite 3.35 and later. Good.
- **Sequences:** per-GC `head_seq` via `UPDATE ... RETURNING` works and is simpler.
- **Cross-table constraints** that Postgres enforces with exclusion or deferred checks (org cycle detection, live-edge rules) move to application code under the write lock.

### 4.3 Real-time and concurrency
- **`LISTEN` and `NOTIFY`:** `GcNotifyListener`, `CoalescingGcNotifier` and their reconnect tests are deleted. Fan-out becomes an in-process channel. This is correct for one process. It breaks if `migrate` or any second process needs to wake the server, because a single SQLite file is not a notification source across processes.
- **Write concurrency:** WAL allows one writer at a time. For chat-scale writes, batching (group commit) can reach the S3 targets, but it must be measured. Long write transactions by Vault indexing or agent turns could starve GC appends. Separate write queues per class would be needed.
- **Instance lock:** a lock file, not an advisory lock.

### 4.4 Tests and tooling
- `Bazaar.Testing/Postgres/*` (PgFixture, PgTemplate, PgRoles) is replaced by a SQLite fixture. Tests get faster (in-memory or temp files).
- Every DB-level security test is rewritten as an application test. Those tests would then prove less.
- The "one schema, provider folders add only extensions" rule (doc 04) is kept, but the Windows and Linux provider folders collapse into one.
- A dual-mode option (C) doubles the repository and migration tests unless the repositories are abstracted over both dialects.

### 4.5 What SQLite would give
- **Install:** no server, no service, no port, no password. One file in the data root.
- **Backup:** copy the file (with `VACUUM INTO` or the online backup API). Simple to version and export (doc 33).
- **Latency:** in-process reads, no network round trip. Likely faster for single-user reads at this scale.
- **Memory and disk:** lower baseline.
- **Test speed:** much faster test setup.
- **Single-host deployment:** nothing else to run beside the VMs.

### 4.6 What SQLite would cost (the honest list)
- The database-enforced append-only, role and grant guarantees that the audit and erasure design rely on.
- Multi-process or multi-host operation, and LISTEN/NOTIFY across processes.
- Concurrent writers at high rates.
- pgvector HNSW-class performance and `halfvec` storage.
- Partitioning and the planned data-move path (doc 04).
- Point-in-time recovery. Litestream-style replication is an option **[UNVERIFIED]** for Windows.
- The multi-human future: several people on one instance, row-level access rules that the design may later need.

---

## 5. Effort estimates

Rough, for one experienced engineer, excluding review. These are my estimates, not measured.

| Path | Effort | Risk |
|---|---|---|
| Stay on PostgreSQL, package it (section 6) | 3 to 4 weeks for a managed Windows sidecar (initdb, start and stop, pgvector build pipeline, backup); 2 to 3 days for a Compose one-liner on Linux and as an interim step | pgvector Windows build and signing; EDB and pgvector licence review |
| Full switch to SQLite (B) | 6 to 9 weeks for schema, repositories, tests and the write-queue design, plus ongoing dual-test cost if kept | Loss of DB-enforced guarantees; FTS and vector result changes; write concurrency |
| SQLite as an optional mode next to Postgres (C) | 9 to 13 weeks, then ongoing parity cost | Two data layers forever |
| DuckDB as an analytics side store | 1 to 2 weeks for a read-only export feed | Low |
| FoundationDB (E) | 3 to 5 months | Windows support is doubtful **[UNVERIFIED]** |
| LMDB or RocksDB with own indexes (F) | 4 to 6 months | Rebuilding FTS, vectors, JSON, access control |
| Custom event-log store (G) | 4 to 6 months minimum to reach durability, recovery and query parity | Highest; likely to fail the S3 bar in ways that take longer to find |

Estimates for B and C assume the agent fan-out process from the build plan, which shortens calendar time but not review or verification time.

---

## 6. Recommendation

**Keep PostgreSQL 18. Make it invisible to the owner.**

1. **Windows (the owner's PC, first supported host):** the setup wizard installs and manages a private PostgreSQL 18 instance for Bazaar only, under the data root, bound to `127.0.0.1`, with a generated password held in the secret store (doc 03/14). The host starts and stops it as a child process, and runs `migrate` and the readiness checks. An existing server can still be used through the connection-string setting. Build pgvector with MSVC in the release pipeline. Leave pg_search out on Windows, because the Windows search provider is Postgres FTS already. **Effort: 3 to 4 weeks.** Main risk: pgvector build and signing.
2. **Linux:** a Compose file (PG 18 with pgvector, plus the pg_search image where the Linux search provider is wanted) as a one-liner for development and self-hosting. **Effort: 2 to 3 days**, and it is the fastest visible win. Docker Desktop on Windows needs WSL2 or Hyper-V, so it is not the Windows answer.
3. **Keep SQLite as a documented option** for a future single-user mode (option C), and as an export format. Do not decide it now. If the owner wants it, decide it as a setting with a clear note of the guarantees given up.
4. **Do not pursue a custom store, FoundationDB or LMDB/RocksDB** for v1.

Why this beats a switch: every guarantee in section 4.6 is one the design already depends on. Packaging solves the owner's complaint (separate server to install) without touching the data layer.

---

## 7. Open questions for the owner

1. Accept Postgres packaging as the answer to "one unit", rather than a SQLite default?
2. Is a single-user SQLite mode wanted at all? If yes, does losing DB-enforced audit tamper resistance in that mode matter?
3. Is Docker acceptable on the owner's PC, or must the Windows path have no Docker or WSL2 dependency? (Current answer: the managed sidecar avoids both.)
4. Accept pgvector built from source in the release pipeline, subject to licence review (PostgreSQL and pgvector licences are permissive; counsel to confirm bundling)?
5. Once S3 results are summarised, do they meet the bar at the target scale? That decides whether any SQLite comparison needs a benchmark.

## 8. Sources

- Repo: `src/Bazaar.Server/Data/Sql/common/*.sql`, `src/Bazaar.Server/Gc/Write/CoalescingGcNotifier.cs`, `src/Bazaar.Server/Gc/Read/GcNotifyListener.cs`, `src/Bazaar.Server.Host/Startup/DatabaseConnection.cs`, `scripts/dev/README.md`.
- Design: `docs/design/04-postgres-schema-and-migrations.md`, `docs/design/03-server-host-and-providers.md`, `docs/deep-dives/Bazaar.Server.md`, `docs/design/DECISIONS-LOG.md` (C07, C10, C23, C38, C39).
- Research: `docs/research/notes/Bazaar stack feasibility research/data_layer_windows.md`, `docs/research/notes/Bazaar build tooling research/data_access.md`.
- Spikes: `docs/spikes/S3.md` (GC log at scale).
- Not fetched in this session: SQLite, FTS5, sqlite-vec, DuckDB, FoundationDB and LMDB/RocksDB documentation. Anything about their current features and Windows support is **[UNVERIFIED]**.
