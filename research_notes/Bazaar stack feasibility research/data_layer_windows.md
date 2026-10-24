# Bazaar Data Layer on a Windows-Only Server Host (as of 2026-10-24)

Scope: whether PostgreSQL + pgvector, ParadeDB pg_search, and Valkey (or alternatives) run natively on Windows for a headless NUC-class server (~32 GB RAM) with no WSL2/Docker/VM. Sources are primary (postgresql.org, GitHub repos, redis.io, valkey.io, microsoft.github.io/garnet, paradedb.com) unless marked otherwise. Items marked **[UNVERIFIED]** came only from search-result snippets or third-party pages, or were not checked at all.

Design-doc claims checked: Bazaar.Server.md (Valkey/Redis licence history, DB choice, Discord-style chat claim) and Bazaar.Constellation.md (pg_search AGPL claim).

## Bottom line

| Component | Native on Windows? | Verdict |
|---|---|---|
| PostgreSQL 18 | Yes. EDB-built Windows installer, tested by EDB on Windows Server 2022/2025. | Native. Use it. |
| pgvector | Builds on Windows with MSVC (`nmake /F Makefile.win`). No official prebuilt binary was confirmed. | Native, but you compile it (or use a VM/Docker). |
| ParadeDB pg_search | No. Prebuilt binaries only for Debian, Ubuntu, RHEL, and macOS. No Windows statement. | Not native. Needs Linux (VM/WSL2/Docker) or drop it. |
| Valkey | No. Official docs say "Valkey is not officially supported on Windows." Upstream issue closed with "no plans to provide compiled binaries for Windows." | Not native. WSL2/Docker, or use a different cache. |
| Microsoft Garnet | Yes. Microsoft says it is tested "equally efficiently on both Linux and Windows." MIT licence. .NET. | Native candidate. Best fit for a Windows-only cache/pub-sub server. |
| Memurai | Native Windows Redis-compatible server (vendor claim). Commercial. | Possible fallback. Not verified. |

Practical read: a fully native Windows stack is PostgreSQL + pgvector + Garnet (or in-process/Postgres-based pub/sub). ParadeDB pg_search is the component that forces a Linux environment. The simplest option is to replace it with Postgres built-in full-text search (tsvector/GIN) plus pgvector, which is native and has no extra licence.

---

## 1. PostgreSQL on Windows

### Takeaway
PostgreSQL has an official Windows path via an EDB-built installer, hosted by EDB, that is tested on Windows Server 2022 and 2025. Support status is current for 18.6 and the 19 release candidate is out.

### Cited Findings
- Installer is built and hosted by EDB, not the PostgreSQL community servers; PostgreSQL 13–18 offered; installers "certified by EDB for all supported Postgres versions." — [PostgreSQL: Windows installers](https://www.postgresql.org/download/windows/)
- EDB tests the 64-bit installer on: PG 18 on Windows Server "2025, 2022"; PG 17 and 16 on "2022, 2019"; "They can generally be expected to run on other comparable versions, for example, desktop releases of Windows." — [PostgreSQL: Windows installers](https://www.postgresql.org/download/windows/)
- Version support table: 18.6 (supported to Nov 14, 2030), 17.11 (to Nov 8, 2029), 16.15 (to Nov 9, 2028), 15.19 (to Nov 11, 2027), 14.24 (final release Nov 12, 2026). — [PostgreSQL Versioning Policy](https://www.postgresql.org/support/versioning/)
- The PostgreSQL download page banner reads "October 15, 2026: PostgreSQL 19 RC 1 Released!" — [PostgreSQL: Windows installers](https://www.postgresql.org/download/windows/). Release candidate only; use 18.x for the initial build.
- Windows package managers: `winget install -e --id PostgreSQL.PostgreSQL.18` — [WinGet listing via winstall.app](https://winstall.app/apps/PostgreSQL.PostgreSQL.18) **[UNVERIFIED: third-party listing; the ID is consistent with the EDB package but not checked against winget-pkgs]**
- Huge pages on Windows are "large pages" and require the "Lock pages in memory" user right for the account running PostgreSQL. — [PostgreSQL 18 docs, Resource Consumption](https://www.postgresql.org/docs/current/runtime-config-resource.html)

### Inferences
- Major-version choice for a new install on 2026-10-24: PostgreSQL 18 (current major with a Windows installer). 14 reaches end of life on 2026-11-12, so do not start on it.
- The EDB installer bundles pgAdmin and StackBuilder. StackBuilder is the GUI route to extra extensions on Windows. Whether pgvector or pg_trgm is available there was **not verified**.

### Gaps
- The Windows-specific page of the PostgreSQL 18 docs (`postgresql.org/docs/current/windows.html`) returned HTTP 404 during research. Statements about running PostgreSQL as a Windows service (`pg_ctl register`, service account, not running as administrator) are therefore **[UNVERIFIED]** here. The EDB installer normally registers the service; confirm on a test machine.
- Version numbers from third-party pages (18.4-1, 18.6-5 on WinGet) were not reconciled with EDB's own listing beyond "18.6."

---

## 2. pgvector on Windows

### Takeaway
pgvector documents a native Windows build with Visual Studio C++ tools and `nmake`. Docker and conda-forge are the documented alternatives. The latest release on the page is v0.8.7.

### Cited Findings
- Windows install: "Ensure C++ support in Visual Studio is installed," then run the "x64 Native Tools Command Prompt for VS" as administrator and build with `nmake /F Makefile.win`, with `PGROOT` pointing at the PostgreSQL 18 install. — [pgvector README](https://github.com/pgvector/pgvector)
- Alternatives listed: "You can also install it with Docker or conda-forge." WSL is not mentioned. — [pgvector README](https://github.com/pgvector/pgvector)
- Latest release shown: v0.8.7 (from the `git clone --branch v0.8.7` example). No release date was on the page. — [pgvector README](https://github.com/pgvector/pgvector)
- pgvector's LICENSE file is a permissive licence with "Portions Copyright (c) 1996-2026, PostgreSQL Global Development Group," which is consistent with the PostgreSQL licence. The licence name was not stated on the page. — [pgvector LICENSE](https://github.com/pgvector/pgvector/blob/master/LICENSE)

### Inferences
- Plan for a one-time MSVC build of pgvector against each PostgreSQL minor upgrade you take. This is a maintenance cost, not a blocker.
- ParadeDB's pg_search (section 3) needs pgvector installed first, so the pgvector build is a dependency even if pg_search is dropped.

### Gaps
- No prebuilt pgvector Windows binary was confirmed. Whether EDB's StackBuilder carries pgvector was **not verified**.
- Maturity of the Windows build in production was **not verified**; I found no reports either way.

---

## 3. ParadeDB pg_search

### Takeaway
pg_search does not document Windows support. Prebuilt extension binaries cover Linux and macOS only, so the component needs a Linux environment on this host, or a replacement. Its licence is AGPL-3.0 for the Community edition.

### Cited Findings
- Prebuilt binaries are provided "for Postgres 15+ on": Debian 12/13, Ubuntu 24.04/26.04, RHEL 9/10, macOS 15/26. "The content does not mention Windows." — [ParadeDB self-hosted extension docs](https://www.paradedb.com/docs/deploy/self-hosted/extension) (fetched via the redirect from docs.paradedb.com)
- Install requires superuser, pgvector installed first ("As of `0.25.0`, `pg_search` requires its `vector` type"), `shared_preload_libraries = 'pg_search'` with a server restart, then `CREATE EXTENSION pg_search CASCADE;`. — [ParadeDB self-hosted extension docs](https://www.paradedb.com/docs/deploy/self-hosted/extension)
- "ParadeDB Community is licensed under the GNU Affero General Public License v3.0." ParadeDB Enterprise is commercially licensed. — [ParadeDB GitHub repository](https://github.com/paradedb/paradedb)
- The extension docs page itself contains no licence section. The AGPL statement comes from the repository page. — [ParadeDB self-hosted extension docs](https://www.paradedb.com/docs/deploy/self-hosted/extension)
- No official Windows statement was found in search. A SourceForge mirror lists Windows; that is a third-party page and weak evidence. — [ParadeDB SourceForge mirror](https://sourceforge.net/projects/paradedb.mirror/) **[UNVERIFIED as evidence]**
- Neon deprecated pg_search for new projects on March 19, 2026 and retired it from existing projects on September 21, 2026. — [Neon docs, pg_search](https://neon.com/docs/extensions/pg_search) **[UNVERIFIED: from a search snippet, not fetched]**

### Inferences (licence)
- The design doc says pg_search is AGPL and used via SQL "same relationship as using Postgres itself." That is a plausible reading: a separately-run server extension queried over SQL is generally treated as a separate program, not linked into Bazaar's binary. But AGPL obligations attach to pg_search itself if Bazaar **distributes** a bundle that includes it or modifies it, and AGPL §13 network-use duties apply to modified versions. The design doc's "not a blocker" framing is an unreviewed opinion. **Get legal review before shipping** a bundle or installer that contains pg_search. This is not legal advice.
- Because the Community edition is AGPL and some features are Enterprise-only, check which BM25 features are in Community before depending on them.

### Alternatives (ranked for a Windows-native goal)
1. **PostgreSQL built-in full-text search (tsvector + GIN, ts_rank).** Native on Windows, PostgreSQL licence, no extra extension. Weaker relevance ranking than BM25 (ts_rank is not BM25). Verified only from general knowledge; **the PostgreSQL full-text docs were not fetched** in this session.
2. **pg_trgm** (contrib module, fuzzy/substring matching). Likely ships with the EDB installer. **[UNVERIFIED]**
3. **Hybrid lexical + semantic in SQL**: tsvector rank + pgvector distance fused in one query (e.g. reciprocal rank fusion). Native, no new service. Standard pattern; **no source fetched** for this session.
4. **Other BM25 extensions** (names only, **not researched**): ParadeDB's own earlier pg_bm25 lineage, VectorChord-BM25, and other Postgres BM25 projects. Their Windows support and licences need a separate check.
5. **Application-side search** (.NET Lucene.NET or similar) if BM25 quality matters more than a single database. Adds a sync problem the design doc wanted to avoid.

### Gaps
- Did not check ParadeDB's GitHub Releases for any Windows binary. The releases page on the repo returned no content.
- Did not verify Neon's deprecation from the Neon page directly.

---

## 4. Valkey vs Garnet vs Redis vs Memurai on Windows

### Takeaway
Valkey has no official Windows support and upstream has no plans to ship Windows binaries. Microsoft Garnet is a native-Windows, .NET, Redis-protocol server under MIT. Valkey's BSD-3 licence is verified.

### Cited Findings (Valkey)
- "Valkey is not officially supported on Windows." Installation guide: "you can install Valkey on Windows for development using WSL." — [Valkey installation docs](https://valkey.io/topics/installation/)
- Upstream issue #92 ("Native windows support") opened March 30, 2024, closed May 4, 2025 as completed. Maintainer comments: "Native Windows support would require a huge effort" (zuiderkwast, Mar 30, 2024), "Windows doesn't support fork() natively" (PingXie, Mar 31, 2024), "There are no plans to provide compiled binaries for Windows at this time" (xbasel, May 4, 2025), "Likely needs a new design using threads and locks" (xbasel, May 6, 2025). — [valkey-io/valkey issue #92](https://github.com/valkey-io/valkey/issues/92)
- An April 1, 2026 comment says a community PR (#3427) adds native Visual Studio 2026 builds; the page does not show whether it was merged. Treat any community MSVC build as unsupported. — [valkey-io/valkey issue #92](https://github.com/valkey-io/valkey/issues/92); community repo at [SamuelMarks/valkey-windows](https://github.com/SamuelMarks/valkey-windows) **[UNVERIFIED status]**
- Valkey's COPYING file: BSD 3-Clause, "Copyright (c) 2024-present, Valkey contributors," plus "Copyright (c) 2006-2020, Redis Ltd." — [valkey-io/valkey COPYING](https://github.com/valkey-io/valkey/blob/unstable/COPYING)
- Memurai (vendor claim): sells a Valkey-compatible edition that runs natively on Windows and Linux. **[UNVERIFIED: vendor blog only; not fetched; pricing and licence not checked]** — [Memurai announcement](https://www.memurai.com/blog/memurai-valkey-for-windows-linux)

### Cited Findings (Garnet)
- "Garnet has been extensively tested to work equally efficiently on both Linux and Windows." — [Garnet docs](https://microsoft.github.io/garnet/docs)
- Redis RESP is the primary wire protocol, so unmodified Redis clients connect. "Garnet supports a large (and growing) API surface," including pub/sub, transactions, and Lua scripting. The docs do not give a percentage of Redis command coverage. — [Garnet docs](https://microsoft.github.io/garnet/docs)
- Cluster mode with sharding, replication, and dynamic key migration; "fast non-blocking checkpointing, recovery, operation logging for durability." — [Garnet docs](https://microsoft.github.io/garnet/docs). The docs do not say whether persistence behaves differently on Windows.
- Licence: MIT. Repository README: "This project is licensed under the MIT License." — [microsoft/garnet](https://github.com/microsoft/garnet)
- Repo has a `hosting/Windows/Garnet.worker` folder, per the repo file listing. Not explained on the page. — [microsoft/garnet](https://github.com/microsoft/garnet)
- A community project wraps Garnet as a Windows service and requires the .NET 10 SDK. **[UNVERIFIED: community, not fetched]** — [garnet-as-win-service](https://github.com/MichalMoudry/garnet-as-win-service)
- NuGet `Microsoft.Garnet` listed at version 2.2.4, updated 10/22/2026, targeting .NET 10. **[UNVERIFIED: from search snippet, not fetched]** — [NuGet profile](https://www.nuget.org/profiles/Garnet)

### Cited Findings (Redis licence history)
- Redis moved to SSPL in March 2024, as the company's post states: "in March 2024, we decided to move Redis to the SSPL license." The post says SSPL "is not truly open source because the Open Source Initiative clarified it lacks the requisites to be an OSI-approved license." — [Redis, "Redis is now available under the AGPLv3 open source license" (published 2025-05-01, updated 2025-05-23)](https://redis.io/blog/agplv3/)
- AGPLv3 added "as an additional licensing option for Redis, starting with Redis 8 (available now)." — [Redis AGPLv3 post](https://redis.io/blog/agplv3/)
- The design doc's "2024 SSPL/RSAL" wording: SSPL is confirmed. The "RSAL" part was **not confirmed** from a fetched page; the Redis post refers to a "dual source-available" licence and I did not open it. **[UNVERIFIED: RSAL]**

### Inferences
- For a Windows-only, .NET-heavy host, Garnet is the strongest native option: Microsoft tests it on Windows, it is MIT (less licence friction than AGPL for an open-source platform), and it fits the .NET-everywhere decision.
- Valkey is the right choice on Linux or in a container. On Windows it means WSL2 or Docker, which the decision-making should count as added operational surface.
- Design doc claim "Valkey currently benchmarks ahead on throughput": **not checked**. Treat as unverified.
- Memurai is the commercial fallback if Redis-compatible behavior matters more than open source. Not evaluated in depth.

### Gaps
- Garnet's current Redis command coverage against the features Bazaar needs (pub/sub, key expiry, streams if used) was not verified against Garnet's compatibility list.
- StackExchange.Redis compatibility with Garnet (and with Valkey) was **not verified** in this session.
- Did not check whether Garnet's persistence is production-tested on Windows specifically.

---

## 5. Pub/sub alternatives without a separate cache server

Not fetched from primary sources in this session.
- PostgreSQL LISTEN/NOTIFY: built into PostgreSQL, so native on Windows. Limits (payload size, no persistence of messages, connection-bound listeners) are from general knowledge **[UNVERIFIED in this session]**.
- In-process .NET channels (`System.Threading.Channels`) for single-server fan-out. Works on Windows; does not survive restarts and does not span processes. **[UNVERIFIED: general knowledge]**

For a single Windows server, these may cover the event fan-out without any cache server. Cache-style hot reads can stay in process memory or be dropped until measured.

---

## 6. .NET clients

### Cited Findings
- pgvector-dotnet supports Npgsql, Dapper, Entity Framework Core, and Npgsql.FSharp. The core package is `Pgvector`; `Pgvector.EntityFrameworkCore` maps vector types and translates LINQ to vector operators. — [pgvector-dotnet via search summary](https://deepwiki.com/pgvector/pgvector-dotnet) **[summary is auto-generated; read the repo README directly before relying on it]**
- Latest EF Core package is 0.3.0; it works with EF Core 9 and 10. For EF Core 8, use 0.2.2. — [NuGet, Pgvector.EntityFrameworkCore](https://www.nuget.org/packages/Pgvector.EntityFrameworkCore) **[version details from search snippet]**
- Setup: `HasPostgresExtension("vector")` in `OnModelCreating` and `UseNpgsql` with `o.UseVector()`. — [Pgvector.EntityFrameworkCore NuGet/readme, via search summary]
- Maturity disagreement: one blog calls the EF Core package a community package maintained by Andrew Kane (who also maintains the pgvector clients for other languages), and predicts first-class Npgsql support is coming. Treat as opinion. — [Promptly blog](https://blog.promptly.mt/pgvector-efcore-postgres/)
- Npgsql (the .NET PostgreSQL driver) is pure managed .NET and runs natively on Windows. **[UNVERIFIED in this session: not fetched; it is well known but no primary source was checked]**

### Gaps
- Npgsql version, EF Core provider status, and StackExchange.Redis/Garnet compatibility were not verified from primary sources.

---

## 7. Discord-style chat at small scale and partitioning

### Cited Findings
- PostgreSQL 18 docs on partitioning: benefits are "worthwhile only when a table would otherwise be very large. ... a rule of thumb is that the size of the table should exceed the physical memory of the database server." — [PostgreSQL 18 docs, Table Partitioning](https://www.postgresql.org/docs/current/ddl-partitioning.html)
- The planner "is generally able to handle partition hierarchies with up to a few thousand partitions fairly well," provided pruning works. "Never just assume that more partitions are better than fewer partitions." — [PostgreSQL 18 docs, Table Partitioning](https://www.postgresql.org/docs/current/ddl-partitioning.html)
- Partition pruning depends on the partition key, not on indexes; indexes are a workload decision. Creating an index on a partitioned table propagates it to partitions. — [PostgreSQL 18 docs, Table Partitioning](https://www.postgresql.org/docs/current/ddl-partitioning.html)

### Inferences
- The design doc claims standard indexing on `(gc_id, timestamp)` suffices for chat at small scale and that monthly partitioning is the future lever. The PostgreSQL docs support this direction: partitioning is not needed until a table is larger than RAM, and a composite btree on the query key is the normal starting point. This is consistent with the claim, but it is **inference, not verification** of the throughput claim.
- The design doc's reference to Discord's own database problems was **not checked**. No source for Discord's scale numbers was located. Treat "nowhere near the billions-of-messages scale" as unverified until a source is cited.

### Gaps
- No benchmark or primary source found for Postgres chat workloads at the Bazaar scale. Recommend a local load test with realistic message sizes before committing.

---

## 8. Running PostgreSQL alongside a Bazaar service on a 32 GB Windows host

### Cited Findings
- shared_buffers: "a reasonable starting value ... is 25% of the memory in your system," and "it is unlikely that an allocation of more than 40% of RAM to shared_buffers will work better." On systems with less than 1 GB of RAM, a smaller percentage is appropriate. — [PostgreSQL 18 docs, Resource Consumption](https://www.postgresql.org/docs/current/runtime-config-resource.html)
- work_mem: default 4 MB; memory per query operation, and "several running sessions could be doing such operations concurrently." Hash operations use `work_mem` times `hash_mem_multiplier` (default 2.0). — [PostgreSQL 18 docs, Resource Consumption](https://www.postgresql.org/docs/current/runtime-config-resource.html)

### Inferences (not sourced)
- The 25% rule is for a dedicated database server. On this host, VMs and the Bazaar service share the 32 GB, so a fixed share below 25% is the sensible starting point. A reasonable starting point to test is 4–6 GB shared_buffers, with work_mem kept small (a few MB) and set per-session where needed. These numbers are **my inference, not a sourced recommendation**; benchmark them.
- Every VM that holds agent environments competes for the same RAM. Reserve memory for the host and PostgreSQL before sizing VMs.

### Gaps
- Windows service registration, backup (pg_dump / base backups under Windows, Volume Shadow Copy), and in-place major upgrade procedure on Windows were **not verified**. The PostgreSQL Windows page returned 404. Use `pg_upgrade` and `pg_dump` from the same major version install; confirm on Windows before relying on it.
- effective_cache_size guidance was not available in the fetched page.

---

## Open items to verify before the design is final

1. Test PostgreSQL 18 (EDB installer) + pgvector MSVC build on the actual NUC image: install, service registration, restart after reboot, pgvector load.
2. Decide the search path: drop pg_search for Windows-native tsvector/GIN + pgvector, or run a Linux VM just for search. Get AGPL legal review before shipping pg_search in any bundle.
3. Benchmark Garnet on Windows against the Bazaar pub/sub and cache pattern. Confirm StackExchange.Redis works with Garnet for the commands used.
4. Confirm the Redis RSAL detail from the Redis dual-licence post if the design doc keeps that wording.
5. Load-test Postgres chat indexing at expected volume before relying on the "small scale" claim.
</content>
