# Bazaar.Server packaged PostgreSQL

Status: PROPOSED (design note, owner-delegated decisions noted below). Date: 2026-10-30.
Follows: `docs/research/notes/Bazaar server database options/options.md` (kept PostgreSQL; packaging instead of a new engine).
Owner decision (2026-10-30): keep PostgreSQL, target the latest and upcoming-latest major (forward-facing rule), and make the install fully packaged so Bazaar does not feel like a separate server to install.

Nothing here changes a DECIDED item. Items marked **[UNVERIFIED]** are not yet checked against a primary source.

---

## 1. Versions to target

- **PostgreSQL 19** is the upcoming major. Release candidate 1 is out (released 2026-10-15); RC 2 is planned for 2026-11-12; GA is planned for **2026-11-19** (PostgreSQL news page; release-candidate notice). Source: https://www.postgresql.org/about/news/postgresql-19-rc-1-3409/
- **PostgreSQL 18** is the current stable major and stays supported. Bazaar's schema uses `uuidv7()`, which needs 18 or later.
- **PostgreSQL 14** reaches end of life on 2026-11-12, so it is not a target.
- **pgvector on PostgreSQL 19:** **[UNVERIFIED]**. The search did not confirm PG 19 support. Gate: the packaging build must not ship a PG 19 bundle until the pgvector build passes against PG 19 RC.

Rule: the code path is one (`server_version_num >= 180000`). Bazaar supports 18 and 19, and CI tests both. When 19 GA ships, 19 becomes the default bundle; 18 is kept for one release cycle.

---

## 2. Modes

A new setting, `Bazaar:Database:Mode`, with two values:

| Mode | What it does | Default for |
|---|---|---|
| `managed` | The host starts a private PostgreSQL from the bundled binaries and data directory as a child process, and stops it with the host. | Windows installs (owner's PC) and the Linux packaged install |
| `external` | The host connects to `DatabaseConnectionReference` (today's behaviour, `BAZAAR_DB`). | Dev runs, existing servers, the Compose setup |

Settings may tighten but not loosen the floor (CLAUDE.md configurability rule). The data root is the existing `DataRoot` (`%ProgramData%\Bazaar` on Windows; fixed path on Linux).

---

## 3. Managed mode, layout and lifecycle

**Files**

- Bundled binaries: `<install>\pg\<major>\bin`, `lib`, `share` (one folder per supported major, so 18 and 19 can sit side by side).
- pgvector: built for each bundled major, shipped in `lib` and `share\extension`, and checked by a startup probe (`SELECT extversion FROM pg_extension WHERE extname='vector'` after `CREATE EXTENSION`).
- Cluster: `<DataRoot>\pg\<major>\data`, created by `initdb` on first run, with a `pg-version` file recording the major.
- Superuser password: random, generated once, stored through `ISecretStore` (doc 14). Never logged, never in `postgresql.conf`.

**Configuration written at initdb** (no user input needed)

- `listen_addresses = '127.0.0.1'`; `unix_socket_directories = ''` on Windows.
- A free port from 54320 upward, recorded in the cluster settings. Pinned, not random per start.
- `shared_buffers` default of 25% of RAM, capped at 4 GB until measured **[UNVERIFIED sizing; benchmark]**.
- `password_encryption = scram-sha-256`.
- The Bazaar roles from `0001`/`0004`/... migrations are created by `migrate`, as today. Only the migration role owns the schema.

**Lifecycle**

1. Host start: if `managed`, check the cluster exists (initdb if not), start Postgres as a child process, wait for readiness (`pg_isready` equivalent over TCP), then continue the existing startup (instance lock, schema version check, readiness).
2. Host stop: send a fast shutdown to Postgres, wait with a timeout, then force if needed. Hard host kill leaves Postgres running; the next start adopts it if the PID file and port match, or refuses to start with a clear message.
3. Crash: the host restarts Postgres with backoff and logs the reason. Postgres's own crash recovery handles the data.
4. Version upgrade (18 to 19): a planned `pg_upgrade` command, not automatic. The host refuses to start a cluster whose major differs from the bundle and points to the upgrade command.

**Windows service.** The Bazaar host is already a Worker Service (`AddWindowsService`). Postgres runs as a child of the host, not as its own service, so one service controls both. An optional separate Postgres service stays out of scope for v1.

**Installer and first run.** The installer lays down binaries and creates the data root. The first run of the host performs initdb and `migrate`, with the setup wizard showing progress. No command line is needed. A `bazaar db status|backup|upgrade` subcommand covers the rest.

**Backups.** `pg_dump` (custom format) on a schedule, written under the data root and retained per doc 33. Restore is a documented command.

---

## 4. Linux

Two paths, one code path in the host:

- **Managed mode**, same as Windows, using PGDG-built binaries bundled in the Linux package, or the system's PostgreSQL if its major matches (`pg_config` probe).
- **Compose one-liner** for development and self-hosting: one service on the PostgreSQL 19 image with pgvector, plus the Bazaar host. A PostgreSQL 18 variant is kept for one cycle. The Compose file is `external` mode. This is the fastest route to "one command".

Search on Linux: the `pg_search` provider stays optional (AGPL review needed before bundling it; see `docs/design/DECISIONS-LOG.md` and the stack research). The Postgres FTS provider is the default on both OSes, so packaging does not need `pg_search` to work.

---

## 5. Licensing and distribution

- PostgreSQL is under the PostgreSQL License (permissive). Bundling its binaries is allowed; keep the notice.
- pgvector licence: the research note says permissive, the name was not stated on the page. **[UNVERIFIED]** Counsel to confirm before shipping the bundle.
- EDB Windows binary zips are the source for the Windows bundle **[UNVERIFIED]**: confirm they are redistributable as zip binaries, not just as the installer.
- Signing: the bundle's executables (pg_ctl, postgres, initdb) are unsigned upstream. The release pipeline signs what Bazaar ships, or they are verified by hash at install.
- `pg_search` is not bundled on Windows (no Windows build, research note).

---

## 6. Work packages

Each package is one worktree (`agent/<wp>`), with its own tests. Haiku implementers unless marked Sonnet. Windows-only steps run on the owner's PC through Remote Control (`V:\git\Bazaar.Workspace`, own worktree).

| WP | Scope | Owner model | Where it runs | Depends on |
|---|---|---|---|---|
| P1 | This design note (done in this PR) | main session | cloud | none |
| P2 | Setting `Bazaar:Database:Mode` and validation (`managed` / `external`), tests | Haiku | cloud | P1 |
| P3 | `PostgresProcess` child-process controller: initdb, start, stop, readiness, adopt-or-refuse, restart backoff; unit tests with a fake process; Linux integration test against a real local PG 18 | Sonnet (concurrency and lifecycle) | cloud | P2 |
| P4 | Version probe and bundle layout (`pg/<major>/`), refusal on major mismatch, `db status` subcommand | Haiku | cloud | P3 |
| P5 | Host wiring: start managed Postgres before migrate and readiness; stop on host shutdown; tests in `Bazaar.Server.Host.Tests` | Haiku | cloud | P3 |
| P6 | Linux Compose file (PG 19 and PG 18 variants, pgvector) and README; CI job that starts it and runs the schema tests | Haiku | cloud | P2 |
| P7 | Windows bundle build script: fetch PG 18 and 19 Windows binaries, build pgvector with MSVC per major, verify hashes, lay out `pg/<major>/` | Haiku, then review by main session | owner's PC | P4 |
| P8 | Windows smoke run: first-run initdb and migrate on the PC, host restart, crash test (kill postgres), reboot test | main session | owner's PC | P5, P7 |
| P9 | `pg_upgrade` command (18 to 19) and `pg_dump` backup job, with restore test | Haiku, review by Sonnet | cloud (Linux) | P4 |
| P10 | Licence and notices file for the bundle; counsel question list | main session | cloud | P7 |

Sequencing: P2 and P6 start now in parallel. P3 follows P2. P7 can start as soon as P4's layout is agreed.

---

## 7. Risks and open questions

1. **pgvector on Windows and on PG 19.** Build cost and maintenance per major. If it fails for 19, ship 18 managed and 19 external until fixed. This is the biggest technical risk.
2. **Redistribution of EDB binaries.** Confirm before shipping (section 5).
3. **Antivirus or SmartScreen on a bundled `postgres.exe`.** Test on the PC; signing helps.
4. **Port collisions.** Pinned port with a fallback search, recorded once.
5. **Hard-kill adoption.** Covered by adopt-or-refuse; needs a test.
6. **Resource use on the PC.** `shared_buffers` and `max_connections` are settings; the default is measured on the PC before it is chosen.
7. **Owner decisions still needed:** none blocking. If the owner wants a separate Postgres Windows service, that is a later package.

## 8. Tests that gate each step

- Unit: setting validation; process controller with a fake process (start, stop, timeout, adopt, mismatch refusal).
- Integration (Linux CI): real PG 18 and PG 19 RC when available; initdb, start, migrate, readiness, stop; crash and restart.
- Windows (PC, P8): first run, reboot, kill, upgrade refusal, backup and restore.
- Every behaviour change carries a test (CLAUDE.md).
