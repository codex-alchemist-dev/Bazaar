# Data access and migrations for the Bazaar server (.NET 10, PostgreSQL), as of 2026-10-24

Labels: **verified** = primary source read in this session; **secondary** = search snippet or third-party summary, not opened; **unverified** = my inference or background knowledge with no source read. NuGet "Last Updated" dates are not release dates. Research stopped at about 20 tool calls, past the usual budget, because the main questions were answered from primary pages. Dapper, Testcontainers, migration tools and Wolverine have thinner coverage (see Gaps).

Context check against the repo: `/home/user/Bazaar/docs/deep-dives/Bazaar.Server.md` already marks PostgreSQL as DECIDED, names Npgsql as the PostgreSQL driver (line 48), and says "Native AOT: not for v1" (line 52). AOT is therefore a nice-to-have, not a requirement, for this decision. Monthly partitioning is recorded there as the future scaling lever (line 34).

## Key question 1: EF Core 10 (Npgsql provider, pgvector, JSON/arrays, partitioning, compiled models, AOT, performance vs Dapper)

### Takeaway
EF Core 10 is a current LTS release that requires .NET 10, and the Npgsql provider 10.0.3 is on NuGet. EF Core 10's headline vector and JSON work is aimed at SQL Server and Cosmos; for PostgreSQL, pgvector comes through a separate package. Compiled models have documented limits and no Native AOT statement was found, so EF is a workable choice for ordinary CRUD but not the hot path for the GC log.

### Cited Findings
- EF Core 10.0 was released November 2025, is LTS, supported until November 10, 2028, and requires the .NET 10 SDK and runtime; it will not run on earlier .NET versions. — [Microsoft Learn: What's New in EF Core 10](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew) (**verified**)
- EF 10's vector search and new `json` type support are described for Azure SQL / SQL Server 2025 and Cosmos DB; the page says "For support with other databases, consult the documentation for your EF provider." — [Microsoft Learn: What's New in EF Core 10](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew) (**verified**)
- EF 10 adds complex types mapped to JSON (`ComplexProperty(...).ToJson()`), `ExecuteUpdateAsync` on JSON properties (SQL Server 2025 examples), named query filters, and a new default for parameterized collections (one scalar parameter per value, padded). — [Microsoft Learn: What's New in EF Core 10](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew) (**verified**)
- Complex types are said to be the better choice over owned entities for JSON and table splitting. — [Microsoft Learn: What's New in EF Core 10](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew) (**verified**)
- Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3 is the latest stable package on NuGet; it targets net10.0, requires `Microsoft.EntityFrameworkCore >= 10.0.4 && < 11.0.0` and `Npgsql >= 10.0.3`. The page lists 11.0.0-rc.2 as a prerelease. The 10.0.3 entry shows "Last Updated 7/10/2026". — [NuGet: Npgsql.EntityFrameworkCore.PostgreSQL](https://www.nuget.org/packages/Npgsql.EntityFrameworkCore.PostgreSQL) (**verified** for package metadata, read via a fetch summary)
- The Npgsql EF provider's repository says it lets you "query JSON, array or range columns, as well as many other advanced features," and links to Npgsql's own docs. It does not mention vector, AOT or compiled models. — [GitHub: npgsql/efcore.pg](https://github.com/npgsql/efcore.pg) (**verified**)
- pgvector for .NET: packages `Pgvector`, `Pgvector.Dapper`, `Pgvector.EntityFrameworkCore`; "The latest version works with Entity Framework Core 9 and 10" (EF 8 uses 0.2.2). Vector, HalfVector and SparseVector types; LINQ distance methods include CosineDistance, L2Distance, MaxInnerProduct, L1Distance, HammingDistance, JaccardDistance; index operator classes `vector_l2_ops`, `vector_ip_ops`, `vector_cosine_ops`. The page does not state a version or date. — [GitHub: pgvector/pgvector-dotnet](https://github.com/pgvector/pgvector-dotnet) (**verified**)
- EF's compiled models (`dotnet ef dbcontext optimize`) are documented with these limitations: no global query filters, no lazy-loading or change-tracking proxies, value converters must not reference private methods, and the model must be regenerated whenever the model changes. The docs say to use them "only if your EF Core startup time is too slow." — [Microsoft Learn: Advanced Performance Topics](https://learn.microsoft.com/en-us/ef/core/performance/advanced-performance-topics) (**verified**)
- The same page says EF generally opens connections just before each operation and closes them right afterwards, and that DbContext pooling is orthogonal to driver connection pooling. Its only benchmark is against SQL Server: about 701.6 µs without DbContext pooling and 350.1 µs with it, for a single-row fetch. — [Microsoft Learn: Advanced Performance Topics](https://learn.microsoft.com/en-us/ef/core/performance/advanced-performance-topics) (**verified**; benchmark is SQL Server, not PostgreSQL)
- The same page says that for PostgreSQL the query plan cache is not LRU-managed by the server, and prepared statements give a similar effect. — [Microsoft Learn: Advanced Performance Topics](https://learn.microsoft.com/en-us/ef/core/performance/advanced-performance-topics) (**verified**)
- The EF Core docs I read do not mention Native AOT or trimming support for EF Core itself. — [Microsoft Learn: Advanced Performance Topics](https://learn.microsoft.com/en-us/ef/core/performance/advanced-performance-topics) (**verified** absence, only the performance topics page was read)

### Inferences
- EF Core is viable for Kanban, reputation, metadata and admin CRUD, where LINQ and migrations save effort. It is a poor fit for the append-only GC log and the fan-out path, where a generated SQL statement per call and tracking overhead add nothing.
- Because EF's documented PostgreSQL features (vector, JSON, arrays) live in separate packages with their own version lines, EF adds a second version axis to track against Npgsql. This is a maintenance cost, not a blocker.
- EF compiled models would be a late optimization for startup time only, and they are incompatible with global query filters (which some multi-tenant or soft-delete designs would want).

### Gaps
- No source read for EF Core on Native AOT. Status is **unverified**; the server doc says AOT is not for v1 anyway.
- No PostgreSQL-specific EF benchmark against Dapper or raw Npgsql was found. The claim "EF is slower than Dapper" is **unverified** for this workload.
- The exact date of the Npgsql EF provider 10.0.3 release is not confirmed (the NuGet date is "Last Updated").

## Key question 2: Dapper, Dapper.AOT, plain Npgsql (NpgsqlDataSource, batching, binary COPY), Npgsql 10 features

### Takeaway
Plain Npgsql 10 with `NpgsqlDataSource` is the simplest and most AOT-friendly base: it has batching, prepared statements, and a slim builder for trimming. Dapper.AOT removes Dapper's reflection with a build-time generator, but I could not verify its current version, its .NET 10 support, or its Npgsql-specific limits, so it should be a later optional layer rather than a foundation.

### Cited Findings
- Npgsql 10.0 is on NuGet (package page last updated 5/27/2026 for 10.0.3). Its package targets .NET 8 and above (assemblies for net8.0, net9.0, net10.0). — [NuGet: Npgsql](https://www.nuget.org/packages/Npgsql) (**verified** for package metadata; release date not confirmed)
- Npgsql 10.0 release notes: .NET 6 is dropped; `date` and `time` map to `DateOnly` and `TimeOnly` by default (a `LegacyDateAndTimeResolverFactory` restores the old mapping); `cidr` maps to `IPNetwork`; metric names and tracing span tags were renamed; TLS validation uses only the supplied root CA certificate; `DataTypeName` takes precedence over `NpgsqlDbType`; NativeAOT apps using mutable JSON types must call `EnableJsonTypes()` on `NpgsqlSlimDataSourceBuilder`. — [Npgsql 10.0 release notes](https://www.npgsql.org/doc/release-notes/10.0.html) (**verified**)
- Npgsql 10.0 adds tracing and metrics aligned with OpenTelemetry stable conventions; COPY operations are fully traced; GSSAPI session encryption (`GssEncryptionMode`, default `Prefer`); `RequireAuth` connection option; `cube` extension type. — [Npgsql 10.0 release notes](https://www.npgsql.org/doc/release-notes/10.0.html) (**verified**)
- Npgsql plans to start throwing on synchronous I/O by default in Npgsql 11.0 (with a switch to re-enable), and possibly remove the sync APIs in 12.0. This is stated as "considering" in the release notes, so it is a plan, not a commitment. — [Npgsql 10.0 release notes](https://www.npgsql.org/doc/release-notes/10.0.html) (**verified**, planned)
- `NpgsqlDataSource` is the entry point and should be a single shared instance; data sources are thread-safe and "usually correspond to a connection pool inside Npgsql." For NativeAOT and trimming, the docs suggest `NpgsqlSlimDataSourceBuilder`, which includes only minimal functionality by default. — [Npgsql: Basic usage](https://www.npgsql.org/doc/basic-usage.html) (**verified**)
- `NpgsqlBatch` sends several statements in one roundtrip; "All statements and parameters are efficiently packed into a single packet"; with no explicit transaction the batch is "automatically wrapped in an implicit transaction." Parameters are needed to use prepared statements; positional `$1`-style parameters are recommended. — [Npgsql: Basic usage](https://www.npgsql.org/doc/basic-usage.html) (**verified**)
- The Npgsql basic-usage page I read did **not** cover binary COPY (`BeginBinaryImport`). — [Npgsql: Basic usage](https://www.npgsql.org/doc/basic-usage.html) (**verified** absence)
- Dapper.AOT is a build-time tool that replaces Dapper calls "with code that bypasses all the reflection and ref-emit code," plus usage guidance. Packages: `Dapper.AOT` (full suite) and `Dapper.Advisor` (guidance only). — [Dapper.AOT docs](https://aot.dapperlib.dev/) (**verified**)
- A search snippet lists Dapper.AOT 1.0.48 on the main NuGet page, Dapper.Advisor 1.0.52 (last updated 5/16/2026), and says the docs target .NET 8+. — [NuGet: Dapper.AOT](https://www.nuget.org/packages/Dapper.AOT) (**secondary**, search snippet only, not opened)

### Inferences
- Hand-written Npgsql with a small row mapper (or `NpgsqlDataReader` ordinal reads) gives the most control, zero reflection, and no source generator, which matches Bazaar's "no Windows-only APIs outside a provider" and AOT-friendly direction. The cost is more boilerplate than Dapper.
- Batching is the right tool for appending a GC message together with its index rows and outbox/notify rows in one roundtrip and one implicit transaction.
- Npgsql 11 is in RC (per the EF provider NuGet page), and sync I/O removal is planned. New code should use async APIs only.

### Gaps
- Binary COPY (`BeginBinaryImport`) behaviour and throughput: **unverified**. Npgsql's COPY page was not read. Needed for bulk backfills or imports only.
- Dapper's current version and .NET 10 support were not read from the Dapper project. **Unverified**.
- Known Dapper.AOT limitations with Npgsql (for example, parameter-type or `NpgsqlDbType` gaps) were not found. **Unverified**.
- No measured throughput numbers for Npgsql versus Dapper versus EF on PostgreSQL were found. The "write throughput on one small host" claim is **unverified** for all options.

## Key question 3: Marten (JasperFx), Wolverine, Weasel and other options

### Takeaway
Marten is a mature, MIT-licensed PostgreSQL document and event-store library on .NET 9 and 10, and it could store GC events. It owns the schema, adds a JasperFx dependency tree, and its AOT story is not documented, so it is a poor fit for the core store. Weasel is the underlying schema library and is MIT-licensed, but I did not verify its migration features for PostgreSQL.

### Cited Findings
- Marten's repository says it is "an ACID-compliant event store with user-defined projections against event streams," uses MIT licence, requires .NET SDK 9.0 and 10.0 (libraries multi-target net9.0;net10.0), and requires PostgreSQL 13 or later (the async daemon depends on `pg_current_snapshot()`). — [GitHub: JasperFx/marten](https://github.com/JasperFx/marten) (**verified**)
- The repository's "Native AOT smoke test" is a CI target; the page does not state that Marten supports Native AOT in general. — [GitHub: JasperFx/marten](https://github.com/JasperFx/marten) (**verified** for what is and isn't stated)
- The repository page does not mention full-text search or vector support; it references pgvector only in the test Docker image (`pgvector/pgvector:pg17`). — [GitHub: JasperFx/marten](https://github.com/JasperFx/marten) (**verified** absence)
- NuGet: Marten 9.53.0 is the latest stable version listed; it targets net9.0 with net10.0 compatible; depends on `JasperFx >= 2.94.0` and `JasperFx.Events >= 2.94.0`. Shown "Last Updated 10/23/2026". — [NuGet: Marten](https://www.nuget.org/packages/Marten) (**verified** for package metadata)
- Marten 8 migration guide (from a search snippet): depends on Npgsql 9, requires PostgreSQL 13+, removed most synchronous APIs, and the projection model no longer uses code generation. Sync `LINQToList()`/`ToArray()` slated for removal in Marten 9. — [Marten migration guide](https://martendb.io/migration-guide) (**secondary**, search snippet, not opened)
- A release tracker (search snippet) reports that before Marten 8.36.1, full-text search APIs interpolated a user-supplied `regConfig` parameter directly into SQL; fixed in 8.36.1. This is a relevant security history for any Marten full-text use. — [releasealert.dev: JasperFx/marten CVEs](https://releasealert.dev/github/JasperFx/marten/cves) (**secondary**, search snippet, not opened)
- Weasel: "a library for low level database development with PostgreSQL, SQL Server, Oracle, MySQL, SQLite and Firebird," extracted from Marten; MIT licence per the repository. The page does not describe PostgreSQL migration features. — [GitHub: JasperFx/weasel](https://github.com/JasperFx/weasel) (**verified** for description and licence)
- Wolverine was in the brief but was not researched in this session. — (no source)

### Inferences
- Marten's event store is a reasonable model for the GC log (one stream per GC, append-only events, projections for read models). But the Bazaar server doc already says the GC log is a plain append-only table indexed on `gc_id` + timestamp, and a hand-written table with `NOTIFY` covers that without a document store. Adopting Marten would also commit Bazaar's domain to Marten's stream and projection model. Recommend against it for v1, with a spike only if projections become complex.
- Marten's Constellation or vector use would still need pgvector wired by hand, and its full-text support has had at least one injection issue (secondary source), so Bazaar would be checking Marten's SQL either way.
- Licence (MIT) is compatible with Bazaar's AGPL-3.0 server, but a dependency with JasperFx's own release cadence adds coupling.

### Gaps
- Marten AOT status is **unverified** beyond the CI smoke test.
- Marten's behaviour on Windows and Linux was not checked, though it runs on PostgreSQL and .NET, so it should not be OS-specific. **Unverified**.
- Wolverine, linq2db, SqlKata: **not researched** in this session. No claims made.
- Weasel's PostgreSQL migration feature set: **unverified** (only the README was read).

## Key question 4: Migrations (EF migrations vs SQL-file migrators; conditional extensions by OS)

### Takeaway
SQL-file migrations, applied by a small runner, fit the Bazaar requirement of OS-conditional extensions (pgvector everywhere, `pg_search` only on Linux, built-in full-text on Windows) better than EF migrations, because the runner can branch on a provider flag and run the right SQL. I did not read any migration tool's documentation, so the tool choice itself is unverified.

### Cited Findings
- ParadeDB (home of `pg_search`) describes itself as "a Postgres extension" and states that ParadeDB Community is "licensed under the GNU Affero General Public License v3.0"; the page gives no supported PostgreSQL versions or platforms, does not mention Windows, and mentions Docker for local use. — [GitHub: paradedb/paradedb](https://github.com/paradedb/paradedb) (**verified**)
- EF Core 10's migration changes (per the What's New page) include "Stop spanning all migrations with a single transaction," reverting an EF9 change. — [Microsoft Learn: What's New in EF Core 10](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew) (**verified**)
- Bazaar's server doc describes Postgres full-text search for Windows and `pg_search` for Linux, consistent with the CLAUDE.md provider rule. — [/home/user/Bazaar/CLAUDE.md](/home/user/Bazaar/CLAUDE.md) (**verified** as project rule, not a technical source)

### Inferences
- Recommended shape: versioned `.sql` files embedded in the assembly, applied in order, with a `schema_version` table and a checksum per file. Extension and index steps that differ by OS live in separate provider-specific files chosen by a `IDatabaseProvider` (or a config flag) at runtime. Migrations must be idempotent or guarded (`CREATE EXTENSION IF NOT EXISTS`) and must never edit a shipped script.
- Keep the append-only GC log tables out of any "drop and recreate" path. Migrations should only add, never rewrite, the GC tables.
- The EF migrations model does not handle "this extension only on Linux" cleanly, because the model is one code path. EF can still be used for code-first types if the team chooses EF; the runner would then need to skip EF-generated extension steps on Windows.
- Candidate tools (DbUp, Grate, Roundhouse, FluentMigrator, Flyway, Atlas, Weasel's schema tooling) were named in the brief but **not researched**. A small in-house runner is probably about 200 lines and avoids a dependency, but this is an inference.

### Gaps
- No primary source read for DbUp, Grate, FluentMigrator, Flyway, Atlas or Roundhouse. Their .NET 10 support and licences are **unverified**.
- `pg_search` availability on Windows: the paradedb page does not say, so "not available on Windows" is **unverified** (consistent with the project's own plan, not a source).
- `CREATE EXTENSION` privilege requirements on managed or Windows PostgreSQL were not checked. **Unverified**.

## Key question 5: Testing with real PostgreSQL (Testcontainers for .NET, alternatives, speed)

### Takeaway
Testcontainers for .NET is the standard way to run real PostgreSQL in tests, and it works on Windows hosts, but it needs a Docker-API runtime with the Linux engine (Docker Desktop is the tested Windows path). The package page gives no version or .NET floor, and no speed numbers were found. A local PostgreSQL service is the fallback for machines without Docker.

### Cited Findings
- Testcontainers requires "a Docker-API compatible container runtime"; recent Docker on Linux and Docker Desktop on Mac and Windows are "actively tested" and detected automatically. Other setups (remote Docker hosts, Docker alternatives) "aren't actively tested." Podman is not mentioned. — [Testcontainers for .NET](https://dotnet.testcontainers.org/) (**verified**)
- "Testcontainers supports Windows, Linux, and macOS as host systems"; native Windows containers need the host OS version to match the container OS version. — [Testcontainers for .NET](https://dotnet.testcontainers.org/) (**verified**)
- The Testcontainers page does not state supported .NET versions (it says "all compatible .NET Standard versions" and links to a separate Framework page), nor a release version. — [Testcontainers for .NET](https://dotnet.testcontainers.org/) (**verified** absence)
- The Docker getting-started guide lists .NET 8.0+ SDK and a supported Docker environment. — [Docker: Testcontainers .NET getting started](https://docs.docker.com/guides/testcontainers-dotnet-getting-started.md) (**secondary**, search snippet, not opened)
- A third-party guide suggests 8 GB RAM minimum (16 GB preferred) and Docker Desktop with about 6 GB and 4 CPUs. Not an official requirement. — (**secondary**, search snippet only, not opened; source not captured)
- `Wslc.Testcontainers` 0.5.0 is an alternative that uses a WSL-based runtime; it needs WSL 2.9.3+ and .NET 8, 9 or 10. — [NuGet: Wslc.Testcontainers](https://www.nuget.org/packages/Wslc.Testcontainers) (**secondary**, search snippet only; not a mainstream option)

### Inferences
- For the NUC and GitHub-hosted-style CI: Windows CI runners need Docker Desktop or a Linux runner with Docker. A Linux CI job can run the same tests against the Linux `pg_search` path, and Windows developers can use Docker Desktop with the Linux engine.
- Because the server must run on Windows and Linux, tests should run once per OS on their own runner, which is a CI cost rather than a code problem.
- A test database per test class (using a schema or database per fixture) is the usual speed compromise; I did not verify timings.

### Gaps
- Testcontainers version, .NET floor and speed data: **unverified** (not stated on the read page).
- Embedded PostgreSQL options for .NET (for example, an embedded binary) and Aspire's PostgreSQL resource were not researched. **Not researched**.
- Windows CI runner Docker availability was not checked against GitHub's hosted runner docs. **Unverified**.

## Key question 6: LISTEN/NOTIFY with Npgsql for fan-out of new GC messages to WebSocket sessions

### Takeaway
Npgsql can deliver NOTIFY events through a dedicated listening connection and its `Wait`/`WaitAsync` loop, but notifications are only delivered between transactions and the payload must stay under 8000 bytes. The sound design is to NOTIFY a small wake-up (GC id and sequence number), have one listener per server process, and read the message body from the append-only log, which keeps GCs as the source of truth.

### Cited Findings
- Npgsql exposes notifications via the `Notification` event on `NpgsqlConnection`; `Wait()` blocks a thread until a notification arrives, with timeout and async overloads. Without `Wait()` or another command, notifications are not delivered in real time; they are processed "the next time a command is sent and processed." — [Npgsql: Wait for notifications](https://www.npgsql.org/doc/wait.html) (**verified**)
- The Npgsql wait page does not say a dedicated connection is required; its example uses the same connection for `LISTEN` and then blocks on it. Keepalives are recommended. — [Npgsql: Wait for notifications](https://www.npgsql.org/doc/wait.html) (**verified** for what is stated; the dedicated-connection point is an inference from the example)
- NOTIFY payload "must be shorter than 8000 bytes" in the default configuration; the docs say to store larger data in a table and send the key. — [PostgreSQL docs: NOTIFY](https://www.postgresql.org/docs/current/sql-notify.html) (**verified**; page is PostgreSQL 18.6 docs)
- A NOTIFY inside a transaction is delivered only after commit; a listening session receiving a notification inside its own transaction gets it "just after the transaction is completed"; the docs advise keeping transactions short. — [PostgreSQL docs: NOTIFY](https://www.postgresql.org/docs/current/sql-notify.html) (**verified**)
- Within one transaction, identical channel and payload are de-duplicated; distinct payloads are all delivered; order is preserved per transaction and in commit order across transactions. — [PostgreSQL docs: NOTIFY](https://www.postgresql.org/docs/current/sql-notify.html) (**verified**)
- The notification queue is about 8 GB in a standard installation. If it fills, transactions calling NOTIFY fail at commit. A session that LISTENs and then sits in a long transaction blocks cleanup; warnings appear once the queue is half full. `pg_notification_queue_usage()` reports the fraction used. — [PostgreSQL docs: NOTIFY](https://www.postgresql.org/docs/current/sql-notify.html) (**verified**)
- A transaction that has executed NOTIFY cannot be prepared for two-phase commit. — [PostgreSQL docs: NOTIFY](https://www.postgresql.org/docs/current/sql-notify.html) (**verified**)

### Inferences
- Use one dedicated listener connection per server process (not one per WebSocket), with an in-process fan-out to subscribed sessions. Each WebSocket session then receives events from memory, and on reconnect it catches up by reading the GC log from its last-seen sequence number. This keeps the append-only log authoritative and makes missed notifications recoverable.
- NOTIFY should carry `{gc_id, seq}`, not message bodies, and should be sent in the same transaction that appends the message, so subscribers never see a notification for an uncommitted message.
- Keep the NOTIFY transaction short (append plus notify, then commit). Do not hold a transaction open while sending to clients.
- A LISTEN connection behind a transaction-mode connection pooler would lose its session state. I did not verify this for any specific pooler; if one is ever used, the listener should bypass it.
- Events and software reactions serve GCs and never replace them (CLAUDE.md). NOTIFY is a hint layered over the GC log, which matches that rule.

### Gaps
- Real throughput and latency of Npgsql NOTIFY fan-out: **unverified**; no benchmark was read.
- Whether Npgsql's `WaitAsync` integrates cleanly with `BackgroundService` and cancellation: **unverified** (the async overload exists per the wait page, but no code sample was read).
- Keepalive settings for long-lived listeners: the keepalive page was linked but not read. **Unverified**.

## Recommendation per data shape (for the owner and the main session to decide)

This is the main session's call to make. My recommendation, with the confidence level of each part:

- **Overall baseline (medium confidence):** Npgsql 10 with one shared `NpgsqlDataSource`, SQL-first repositories, positional parameters, `NpgsqlBatch` for multi-statement writes, and hand-written row mapping on hot paths. Verified basis: Npgsql 10 release notes, Npgsql basic-usage page. This is the least novel option and the most AOT-friendly, and the owner asked for "novel," so say plainly: **the boring choice is the better engineering choice here**.
- **GC message log (high confidence in the shape, medium in details):** one append-only table partitioned later by month if needed (as Bazaar.Server.md already plans), indexed on `(gc_id, seq)` and timestamp; append and notify in one transaction; raw Npgsql, no ORM. Sources: Bazaar.Server.md; NOTIFY and Npgsql wait docs above.
- **Fan-out to WebSocket sessions (medium confidence):** one dedicated LISTEN connection per process plus in-memory fan-out; replay from the log on reconnect. Sources: Npgsql wait docs and PostgreSQL NOTIFY docs.
- **Agent memory with embeddings (medium confidence):** `Pgvector` (raw Npgsql) or `Pgvector.EntityFrameworkCore` if EF is adopted; `vector_cosine_ops` or the chosen index class; verify index behaviour and dimension limits before committing. Source: pgvector-dotnet README (verified).
- **Kanban tasks, dependency edges, reputation, votes, agent metadata (medium confidence):** EF Core 10 with Npgsql is reasonable if the team wants LINQ and migrations for CRUD; use complex types (not owned entities) for JSON-shaped metadata. Keep these paths off the GC hot path. Sources: EF 10 What's New; Npgsql EF provider NuGet page. The choice between EF and raw Npgsql for this shape is a judgement call; I would pick EF here only if the team accepts EF's query translation cost and the lack of AOT evidence.
- **Vault full-text and BM25 search (low-medium confidence):** Postgres built-in full-text on Windows, `pg_search` on Linux, behind one provider interface. Source: ParadeDB page (verified for AGPL-3.0 and extension nature; platform support unverified). No Marten full-text (the 8.36.1 injection issue is a warning).
- **Bulk import or backfill (low confidence):** binary COPY via Npgsql. Not verified here; check Npgsql's COPY docs before relying on it.
- **Migrations (medium confidence in the approach, low in tool choice):** embedded versioned SQL plus a small runner with OS-specific extension steps. Tool choice (DbUp and others) **unverified**.
- **Testing (medium confidence):** Testcontainers for .NET with a Linux PostgreSQL image, using Docker Desktop on Windows dev machines and CI; a local PostgreSQL service as fallback. Source: Testcontainers docs (verified for runtime requirements, not for version or speed).
- **Native AOT (low confidence, not a v1 requirement per Bazaar.Server.md):** raw Npgsql with `NpgsqlSlimDataSourceBuilder` is the most AOT-plausible path (verified as documented for Npgsql). EF Core and Marten AOT status: **unverified/not supported in docs read**. Dapper.AOT is an option, but its Npgsql compatibility is unverified.
- **Marten (recommend against for v1 core store):** MIT, .NET 9/10, PostgreSQL 13+, real event store and projections, but it would take over schema and domain modelling and adds a JasperFx dependency tree. Revisit only for a projections-heavy read model.

## Versions and dates (as read)

| Item | Version | Date / note | Source |
|---|---|---|---|
| EF Core | 10.0 LTS | Released Nov 2025; supported to 2028-11-10 | [What's New](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew) (verified) |
| Npgsql | 10.0 (NuGet 10.0.3) | NuGet last updated 2026-05-27 | [Npgsql release notes](https://www.npgsql.org/doc/release-notes/10.0.html), [NuGet](https://www.nuget.org/packages/Npgsql) (verified) |
| Npgsql EF Core provider | 10.0.3 | NuGet last updated 2026-07-10; prerelease 11.0.0-rc.2 listed | [NuGet](https://www.nuget.org/packages/Npgsql.EntityFrameworkCore.PostgreSQL) (verified) |
| Marten | 9.53.0 | NuGet last updated 2026-10-23 | [NuGet](https://www.nuget.org/packages/Marten) (verified) |
| Weasel | not read | MIT | [GitHub](https://github.com/JasperFx/weasel) (verified licence only) |
| Dapper.AOT | 1.0.48 main / Dapper.Advisor 1.0.52 | Date not confirmed | [NuGet](https://www.nuget.org/packages/Dapper.AOT) (secondary) |
| pgvector-dotnet | not stated | Supports EF Core 9 and 10 | [GitHub](https://github.com/pgvector/pgvector-dotnet) (verified) |
| Testcontainers for .NET | not stated | Docker-API runtime required | [Docs](https://dotnet.testcontainers.org/) (verified) |
| PostgreSQL (NOTIFY docs) | 18.6 docs page | Payload limit 8000 bytes default | [NOTIFY docs](https://www.postgresql.org/docs/current/sql-notify.html) (verified) |

## Gaps (summary)

- Not researched: Wolverine, linq2db, SqlKata, DbUp, Grate, Roundhouse, FluentMigrator, Flyway, Atlas, Aspire PostgreSQL resource, embedded PostgreSQL for .NET tests.
- Not verified: EF Core AOT; Marten AOT; Dapper and Dapper.AOT current versions and Npgsql compatibility; binary COPY; any PostgreSQL throughput benchmarks for EF vs Dapper vs Npgsql; Testcontainers speed, version and .NET floor; `pg_search` Windows status; Npgsql keepalive settings; Npgsql `WaitAsync` integration.
- Search snippets used but not opened: Dapper.AOT NuGet, Marten migration guide, Marten CVE tracker, Docker Testcontainers guide, Wslc.Testcontainers.
- This is research, not legal advice. The AGPL-3.0 and MIT licence statements are as published on the cited pages; no licence compatibility opinion is given.
