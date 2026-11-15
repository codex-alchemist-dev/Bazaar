# 42. Dependencies as modules

Status: DECIDED for the model (owner decisions of 2026-11-13). Design detail below is PLANNED unless marked DECIDED or OPEN. Nothing in this doc is built. As of 2026-11-13 the repo has no dependency manifest, no bundled PostgreSQL, no child-process supervisor for dependencies and no update policy engine. The current design docs still assume a PostgreSQL 18 that the operator installs by hand (see section 17).

Scope: how third-party software that Bazaar needs at runtime (PostgreSQL, pgvector, pg_search, cache servers, language runtimes) is packaged, versioned, installed, supervised, updated, health-checked, licence-checked and shown to the user. Owner's rule: there is no separate dependency manager. Dependencies use the module machinery.

Depends on: 03-server-host-and-providers.md (host, providers, settings registry), 04-postgres-schema-and-migrations.md (PostgreSQL conventions, migrations), 14-identity-service-and-passport.md (secret custody), 15-security-taint-and-approvals.md (permissions, approvals, audit), 16-agent-vms-and-environments.md (golden images), 20-engine-integration-api.md (engine runtimes), 33-backup-dr-and-export.md (snapshots, restore), 34-testing-strategy.md (test matrix), 37-distribution-matrix-and-packaging.md (what is shipped where), BAZAAR-COMPONENTS.md (manifest model). Owner-cited but not present in this worktree: 41 (module contract, present in the main checkout only), 46 (Kodo health contract), 47 (licence checks), 49 (update policies). This doc links them by number and quotes the owner's wording from the 2026-11-13 brief.

---

## 1. Purpose

Bazaar is one product that must run on a machine where nothing else is installed. The owner's rule for the 2026-11-13 decisions is:

- ZERO EXTERNAL INSTALLS for users. A person who installs Bazaar never runs a PostgreSQL installer, a package manager, a PATH edit, a Docker command or a cache-server setup.
- Dependencies are not a second world. A PostgreSQL server is a module, like Kanban or the Vault, with a manifest, a version, a health contract, a licence record and an update policy.

The result this doc specifies: a user sees one list of modules. Some are first-party features, some are third-party engines that Bazaar hosts, and some are dependencies tagged Deps and 3rd party. They share one install path, one update policy model, one audit trail and one health surface.

## 2. Decisions in force

| # | Decision | Status | Source |
|---|---|---|---|
| D1 | No separate dependency manager. Postgres, pgvector and runtimes are modules tagged "Deps" and "3rd party". | DECIDED | Owner brief 2026-11-13 |
| D2 | PostgreSQL 18 and PostgreSQL 19 are two versions of one module. | DECIDED | Owner brief 2026-11-13 |
| D3 | Update policies are owner-set. The model is in doc 49 (updates). | DECIDED | Owner brief 2026-11-13, gap decisions |
| D4 | Dual-version support: the newest and the stable line are both supported at all times. Beta releases get day-0 support. Installs are never left parked on stable. | DECIDED | Owner brief 2026-11-13 |
| D5 | ZERO EXTERNAL INSTALLS. PostgreSQL and pgvector are bundled and run as a hidden, supervised child process. | DECIDED | Owner brief 2026-11-13 |
| D6 | Install and Build-from-source are separate documents. | DECIDED | Owner brief 2026-11-13 |
| D7 | Install is a Bazaar-only action. Bazaar performs it, not the user. | DECIDED | Owner brief 2026-11-13 |
| D8 | Dependencies must be AGPL-compatible. Licence is shown to the user. | DECIDED | Owner brief 2026-11-13, licensing |
| D9 | Module trust: signed modules plus declared permissions approved at install. | DECIDED | Owner brief 2026-11-13, gap decisions |
| D10 | Kodo's mandatory module health contract applies to every module, dependencies included. | DECIDED | Owner brief 2026-11-13, Kodo |
| D11 | Configurability rule: undecided details become operator settings with safe defaults; security invariants are not settings. | DECIDED | CLAUDE.md, round 13 |
| D12 | Windows is the first supported host. The Windows build stays OSI-only (no paid components). | DECIDED | CLAUDE.md, DECISIONS-LOG C23 |

Everything marked PLANNED below is design intent. Anything marked OPEN is listed in section 18 with options.

## 3. Vocabulary

- **Module**: the unit the owner talks about. Repo design docs call the same unit a Bazaar Component (BAZAAR-COMPONENTS.md, PROPOSED). This doc uses "module" as the brief does. The term is OPEN for the design set (section 18, Q8).
- **Dependency module** (dep): a module whose origin is a third-party project and whose purpose is to be used by other modules or by the host. It carries the tag Deps.
- **Tag "Deps"**: role. The module exists to support other modules.
- **Tag "3rd party"**: origin. Its code, or the binary it wraps, is written by someone outside Bazaar. A dep is almost always both. A first-party module is never tagged 3rd party. A third-party engine that users run on purpose (for example a marketplace module) is tagged 3rd party but not Deps.
- **Hidden dependency**: a dep that runs as a supervised child process and has no user-facing pane, palette entry or launcher tile. It is still listed in the module manager under Deps (section 12).
- **Line**: one supported major release of one dep, for example PostgreSQL line 18 or line 19.
- **Role**: where a line sits in the install's life: newest, stable, beta or retired (section 5.3).

## 4. What counts as a dependency

Dependencies are classified by category. The category decides the process model and the update default.

| Category | Examples | Process model (PLANNED) | Where it runs |
|---|---|---|---|
| `database` | PostgreSQL | Hidden supervised child of the host | Server host, one per install |
| `extension` | pgvector, pg_search | Loaded into a `database` dep, no process of its own | Inside the database child |
| `service` | Valkey (Linux cache and presence, doc 03), Garnet (Windows cache and presence) | Hidden supervised child of the host | Server host |
| `runtime` | Language runtimes used by engines and agents: Node, Python, Luau CLI for headless checks, a GDScript runner for Godot | Out-of-process runner, started per job | Agent VM golden images (doc 16) or engine adapters (doc 20) |
| `host-runtime` | The .NET runtime that hosts the server | Embedded in the self-contained server publish | Server host |
| `tool` | git, a diff tool, an archiver, a signing helper | Invoked per job, no supervision | Host or VM image |

Boundary with NuGet (PLANNED): NuGet packages used at build time for C# code stay under central package management (Directory.Packages.props). They are not dependency modules and the module manager does not list them as modules. The module manager may show a read-only "compile-time packages" summary generated from the SBOM (section 14). This keeps one package manager per language and avoids a second one. Q9 in section 18 asks whether the owner wants more than that summary.

Runtimes inside VMs are not host installs. A Luau runner used by agents for headless checks, for example, is a `runtime` dep baked into the Linux VM golden image. Roblox Studio itself runs only on the human's Windows desktop (CLAUDE.md) and is not a Bazaar dependency at all.

## 5. Module identity and dual-version support

### 5.1 One module, many lines

PostgreSQL is one module with the id `postgresql`. PostgreSQL 18 and PostgreSQL 19 are two versions of that module, not two modules. The install keeps one module record and a set of installed versions. Each version is an immutable package with its own manifest, SBOM and signature.

Why one module: the health contract, the licence record, the settings keys, the supervisor, the update policy and the audit trail are the same for both lines. Two modules would double all of them and let the two lines drift.

Why lines still matter: the on-disk data format changes between majors. A line-19 data directory cannot be opened by line 18 binaries. Section 10.3 covers the consequences.

### 5.2 Version scheme (PLANNED)

- `version` is SemVer of the upstream release, normalised to three parts. PostgreSQL `18.6` becomes `18.6.0`. pgvector `0.8.1` stays `0.8.1`.
- `packageRevision` is an integer that Bazaar increments when it rebuilds the same upstream version (a packaging fix, a changed recipe, a new signature). Ordering is by `version`, then `packageRevision`.
- Ranges (section 9) apply to `version` only. A rebuild never changes what a range matches.

The major part of a dep's `version` is the upstream major. A new upstream major always starts a new line.

### 5.3 Roles and lines (PLANNED)

A line's role is a property of the install's registry, not of the package. A package never declares itself "stable". The registry assigns roles:

| Role | Meaning | Who gets it |
|---|---|---|
| `newest` | The latest GA line Bazaar supports. Default for new installs. | Every channel except LTSC |
| `stable` | The previous major line, still supported upstream and still fully tested by Bazaar. The fallback and rollback target. | Every install that has moved to a newer line, and LTSC |
| `beta` | An upstream pre-release. Day-0 CI track. Never a supported line for Stable or LTSC installs. | Beta and Alpha channels only, and only after the CI matrix is green (5.4) |
| `retired` | Upstream end of life, or the owner retired it by policy. Read-only in the module manager. Existing data must be migrated off it first. | Nobody new |

Dual-version support, as the owner wrote it, means: at any time Bazaar supports two GA lines of each database-class dep, the newest and the stable line. The stable line is a supported place to be, but it is not a place to stop. Installs move forward under their update policy (section 10). The one deliberate exception is the LTSC channel (Q4 in section 18).

When PostgreSQL 19 reaches GA (expected around 2026-11-19 per the owner brief), line 19 becomes `newest` and line 18 becomes `stable`. Line 17 is not shipped once it leaves upstream support, which the upstream policy decides, not Bazaar.

### 5.4 Day-0 beta track (PLANNED)

- When upstream publishes a beta or release candidate, a CI job builds that artifact the same day and runs the full matrix (section 16) against it. The target is that the matrix result exists before the upstream announcement is widely read, not a fixed hour count.
- A beta is shown in the module manager as a candidate with its test result. It is installed only on Beta or Alpha channels, and only when the operator chooses it.
- A beta never replaces a GA line automatically. Its promotion to `newest` happens when upstream GA arrives and the GA artifact passes the same matrix.

### 5.5 Data format per major (DECIDED consequence, PLANNED mechanics)

A major upgrade changes the data format. Bazaar never performs an in-place major upgrade of a data directory without the steps in section 10.3. Downgrade of a data directory is not supported by PostgreSQL, so the rollback path is restore of a snapshot, never a reverse upgrade.

## 6. Zero external installs: the bundled, supervised database

### 6.1 What the user sees

The user installs Bazaar. The installer (doc 37) carries the PostgreSQL and pgvector packages for the platform. First run creates the data directory and starts the database. The user is never asked to install PostgreSQL, to choose a port for it, or to set a superuser password. If the wizard offers a choice, it is "database: bundled" (the only option by default, see Q2 in section 18).

The module manager shows the database under Deps with the Bazaar badge "bundled" and the state "running". The database has no palette entry, no launcher tile and no tray icon.

### 6.2 Process model (PLANNED)

- PostgreSQL runs as a child process of `Bazaar.Server.Host` (doc 03). The host is the Windows service or the systemd unit. PostgreSQL is not registered as a service of its own, with no Services entry and no `postgresql` service name.
- The host supervises it: start, liveness watch, crash restart with backoff, and a circuit breaker after repeated crashes, which opens the readiness gate and raises a Kodo incident (doc 46). Restart rules follow Kodo's crash-only design: the database recovers by its own WAL replay, and the host never edits its files.
- Tree kill: on Windows the database process and its children join a Job Object with kill-on-close, so a host crash cannot leave an orphaned `postgres.exe`. On Linux the child is started with the parent-death signal set, and the systemd unit uses the same cgroup kill mode. Both are tested (section 16).
- The host owns the process handle. No other process can start, stop or reconfigure the database. Operators use the host's CLI or the module manager, and every action is audited (doc 15).

### 6.3 Start and stop order (PLANNED)

Start: data directory check, then database start, then extension check (pgvector loaded at the right version), then migrations (doc 04, `migrate`), then readiness for the database role. The host does not accept GC appends before readiness.

Stop: the host first drains GC appenders and turns off new turns (doc 03, shutdown budget `host.shutdown_timeout_seconds`), then stops the database with a smart-then-fast shutdown and a final checkpoint, then exits. A stop that exceeds the budget uses fast shutdown and records the event. The database is never killed as a first step, because that is the only way to get WAL replay on the next start.

### 6.4 Networking and credentials (DECIDED invariant, PLANNED mechanics)

- The database listens on loopback only. On Windows that is `127.0.0.1` TCP. On Linux it is a Unix socket in the data root, with loopback TCP as an alternative. No LAN listener is ever created. This is a security floor (section 15), not a setting.
- The port is chosen at install from the dynamic range, checked for collision, and stored in config. The default 5432 is never used, so a user's own PostgreSQL, if any, cannot collide and is never touched.
- The superuser and application passwords are generated at install with a CSPRNG. They are stored through the secret custody of doc 14, never as command-line arguments (which are visible in the process list) and never in plain config. The connection string is built in memory by the host.
- `pg_hba.conf` is generated by the host and allows only the host's application role over loopback with SCRAM authentication.
- Data checksums are enabled at `initdb`. This is an invariant, not a setting, because it cannot be changed after initialisation.

### 6.5 Extensions (PLANNED)

- pgvector is bundled in every platform package. It is an `extension` dep that requires a `database` line. Its artifact is built per database line, because a compiled extension is tied to a major (section 8.3).
- pg_search (BM25) is bundled on Linux only, behind the search provider (doc 03 `host.provider.search`, doc 04). On Windows the search provider stays on Postgres full-text search, which keeps the Windows build OSI-only (DECISIONS-LOG C23). pg_search's licence is covered in section 13.

### 6.6 Failure the user can act on

When the bundled database cannot start, the module manager shows a plain reason with a fix: "port 49152 is taken by another program: pick another port (auto)", "data directory is on a volume with less than 2 GiB free", "data directory was written by line 19 but the bundled line is 18: start the line 19 package or restore a snapshot". Each has a one-click fix or a runbook (Kodo, doc 46). No message asks the user to install PostgreSQL.

### 6.7 External connection (OPEN)

Some operators already run a managed PostgreSQL. Whether Bazaar may connect to one is Q2 in section 18. If allowed, it is an advanced setting, hidden by default, never offered as an install step, and it never installs or modifies the server. The health contract and the version rules still apply to it.

## 7. Install and Build-from-source are separate documents

D6 and D7 are DECIDED. This doc defines the module. The two paths are documented separately (planned, names to be set when written):

- **Install** is the user path. Bazaar installs its own dependencies from a signed release package. The package contains every dep line Bazaar supports for that platform, already built, tested and signed. The user does nothing except accept the install and the declared permissions (D9). No package manager, no compiler, no network fetch of upstream code. The only network use is the Bazaar release channel and the upstream-independent update feed (doc 49).
- **Build-from-source** is the developer, distribution and auditor path. The same recipes build the same dep lines from pinned upstream sources (tag and commit SHA). The output is a package with its own SBOM and provenance record. Reproducible builds are a later goal (brief, supply chain). Build-from-source may run on the operator's machine, which is why it is a separate document with its own prerequisites.

Doc 37 remains the distribution matrix: which artifact exists for which platform and channel. Dep packages get rows there when the install and build documents exist. This doc does not change the matrix.

## 8. Module manifest for dependencies (PLANNED)

A dep uses the Bazaar Component manifest (BAZAAR-COMPONENTS.md section 2) with the dependency fields below added. There is one manifest format, `bazaar-component.json`, for first-party and third-party modules. The first-party form is a static `ComponentManifest` on the type, and the file form is the same data.

### 8.1 Fields added for deps

| Field | Type | Meaning |
|---|---|---|
| `kind` | `dependency` (new value, see Q8) | Marks the module as a dep. |
| `category` | `database`, `extension`, `service`, `runtime`, `host-runtime`, `tool` | Process model and update default (section 4). |
| `tags` | array, must include `Deps` and `3rd party` for third-party deps | Shown in the module manager filter. |
| `upstream` | object: `project`, `version` (as published), `homepage`, `releaseDate` | Where the code comes from. |
| `packageRevision` | integer | Bazaar rebuild counter (section 5.2). |
| `lines` | array of `{ "line": 18, "requires": {...} }` for a multi-line dep | Lines this package belongs to. The role is not in the manifest (5.3). |
| `provides` | array of contract names | Example: `database:postgresql`, `vector:pgvector`, `search:bm25`. |
| `requires` | array of `{ "id", "range" }` | Other modules this dep needs, with NuGet interval ranges (section 9). |
| `artifacts` | array per platform and per line | Files, sizes, SHA-256, signature, for which line. |
| `lifecycle` | object | How the host starts, stops and probes the process (section 6.2). |
| `health` | object | The Kodo health contract hook (section 11). |
| `permissions` | array | Declared permissions (section 14.3). Approved at install (D9). |
| `licence` | object | SPDX expression, OSI flag, AGPL-compatibility verdict, notice file (section 13). |
| `sbom` | object | Reference and hash of the SBOM for this package. |
| `build` | object | Recipe reference, upstream tag and commit, toolchain versions. Present in build-from-source output. |
| `visibility` | `hidden` or `listed` | Whether the module has any user-facing surface. Hidden deps still appear under Deps in the module manager. |
| `update` | object | Default update policy hints for doc 49. Hints only; the owner's policy wins. |

### 8.2 Example (PLANNED, illustrative values)

```json
{
  "id": "postgresql",
  "name": "PostgreSQL",
  "version": "18.6.0",
  "packageRevision": 3,
  "kind": "dependency",
  "category": "database",
  "tags": ["Deps", "3rd party"],
  "trust": "third-party",
  "process": "out-of-process",
  "visibility": "hidden",
  "upstream": {
    "project": "PostgreSQL",
    "version": "18.6",
    "homepage": "https://www.postgresql.org/"
  },
  "provides": ["database:postgresql"],
  "requires": [],
  "artifacts": [
    { "platform": "win-x64", "sha256": "<hex>", "size": 0, "signature": "<ref>" },
    { "platform": "linux-x64", "sha256": "<hex>", "size": 0, "signature": "<ref>" }
  ],
  "lifecycle": {
    "start": "bin/postgres",
    "dataDir": "data/postgresql",
    "listen": { "windows": "loopback-tcp", "linux": "unix-socket" },
    "shutdown": { "mode": "smart", "fallbackMode": "fast", "fallbackAfterSeconds": 20 },
    "ports": "auto"
  },
  "health": {
    "liveness": { "probe": "supervisor-heartbeat", "timeoutSeconds": 5 },
    "readiness": { "probe": "protocol-ping", "timeoutSeconds": 5 },
    "dependencies": [{ "probe": "extension-loaded", "name": "vector", "range": "[0.8.0,)" }],
    "selfTest": { "schedule": "weekly", "steps": ["vector-roundtrip", "checksum-verify", "restore-drill-sample"] },
    "critical": true
  },
  "permissions": [
    { "id": "fs.dataRoot", "access": "read-write", "scope": "data/postgresql" },
    { "id": "net.listen", "scope": "loopback", "ports": "auto" }
  ],
  "licence": {
    "spdx": "PostgreSQL",
    "osi": true,
    "agplCompatible": true,
    "noticeFile": "NOTICE/postgresql.txt"
  },
  "update": { "securityPolicy": "auto", "majorPolicy": "manual" }
}
```

The example is an illustration. The `artifacts` hashes, the notice path and the exact probes are set by the recipe when the dep is built, and the licence block is verified by the licence scan (section 13), not copied from here.

### 8.3 pgvector and per-line artifacts (PLANNED)

An extension is compiled against a specific database major, so its package carries one artifact per line it supports:

```json
{
  "id": "pgvector",
  "version": "0.8.1",
  "packageRevision": 1,
  "kind": "dependency",
  "category": "extension",
  "tags": ["Deps", "3rd party"],
  "requires": [{ "id": "postgresql", "range": "[18.0.0,20.0.0)" }],
  "artifacts": [
    { "forLine": 18, "platform": "win-x64", "sha256": "<hex>" },
    { "forLine": 19, "platform": "win-x64", "sha256": "<hex>" }
  ]
}
```

Consequence: the pgvector package is not ready for a database line until an artifact for that line exists. The resolver (section 9) refuses to move the database to a line that has no matching extension artifact. This is a preflight check in section 10.3.

### 8.4 Rules

1. `id` is unique per install and stable across versions.
2. A dep package is immutable. A fix is a new `packageRevision`.
3. A dep manifest may not declare a role, a channel or an update policy as a fact. Those belong to the registry and to doc 49.
4. Every third-party dep has `Deps` and `3rd party` in `tags`. The manifest is rejected at load otherwise.
5. A dep with `visibility: hidden` must have no pane, palette entry or launcher tile. The manifest validator enforces this by checking the component's registered surfaces.
6. The manifest is data. The kernel reads it before any dep code runs, so a bad graph fails at start with the ids named (BAZAAR-COMPONENTS.md section 3).

## 9. Version ranges and resolution (PLANNED)

- Range syntax is NuGet interval notation: `[18.0.0,20.0.0)` means 18 up to but not including 20. One syntax for the whole product, matching the .NET tooling the repo already uses.
- A module requires another module with a range. The resolver picks the highest version that satisfies every requirement, is installed or installable, and whose role the install's line setting allows.
- Line preference is a setting: `deps.postgresql.line` (section 15). `newest` picks the newest GA line in range. `stable` picks the stable line. `pinned:<version>` picks exactly that version and is audited.
- Beta versions are excluded from every range unless the range names a prerelease explicitly, and the channel allows it.
- Resolution is deterministic and recorded. The module manager shows the resolved graph and the reason for each choice ("newest in range, line 19, GA, extension artifacts present").
- The resolver never installs a version outside the owner's policy window (doc 49). Resolution that would need one stops with a decision request for the owner.

### 9.1 Compatibility matrix

The module manager shows the matrix that doc 49 and the brief call for: rows are dependent modules, columns are the dep lines, cells say satisfies, fails or untested. An untested cell is shown as a warning, and a fail cell blocks the move that would create it.

## 10. Update policies (owner-set; model in doc 49)

The update model belongs to doc 49. This section says how it applies to deps. Doc 49 is not in this worktree, so the names below are the brief's words and may change when doc 49 lands.

### 10.1 Defaults for deps (PLANNED)

The owner's default is automatic for security and manual for everything else. Applied to deps:

| Change | Default | Notes |
|---|---|---|
| Security patch on a line in use | Auto, in the maintenance window, after a snapshot | Doc 49 default. Rollback is automatic if the health contract fails. |
| Non-security patch on a line in use | Manual | Owner can set auto per module. |
| New minor, same line | Manual | |
| Beta or release candidate | Never automatic | Shown as a candidate with its matrix result (5.4). |
| New GA major line (for example 19 when 18 is in use) | Manual, requires owner approval, runs the section 10.3 procedure | Installs keep their line until the owner approves the move. |
| Retirement of a line | Manual, with a migration plan | Existing data must move off the line first. |

Policies can be set per module, category, version type and channel, with conditions and maintenance windows (doc 49). Pin, defer and skip are available for any dep. Role approvals and delegation follow doc 49.

### 10.2 Dual-version consequence

Because both lines stay supported, an owner who defers the line 19 move is not stuck: line 18 gets its own security patches from Bazaar, and line 19 can be adopted at any time. Deferral has a visible age. After a configurable number of days (setting `deps.update.deferWarnDays`), the module manager and Kodo show the dep as aging. The owner can accept the age, but it is never hidden.

### 10.3 Major line move (PLANNED)

A move from line 18 to line 19 runs as one audited procedure:

1. Preflight: a line-19 artifact exists for every installed extension (pgvector, and pg_search on Linux). Disk space covers the snapshot and the upgrade. Kodo reports green on the current line. The last restore drill is recent enough (doc 33).
2. Snapshot: stop GC appenders, take a base backup and a file-level snapshot of the data directory through doc 33, and record the snapshot id. The snapshot is kept for the retention period in doc 33, not deleted after success.
3. Dry run: restore the snapshot into a scratch data directory running the target line. Run the migrations of doc 04 and the GC consistency checks (row counts and maximum sequence per GC, as in doc 33). The dry run result is shown to the owner.
4. Switch: stop the old line, upgrade the data directory with the method in Q6, start the new line, run migrations.
5. Verify: the health contract (section 11) must be green for the new line, and GC consistency checks must pass. The new line becomes `newest` and the old line becomes `stable` only after this step passes.
6. Rollback: because downgrade is not supported, rollback means stopping the new line, restoring the snapshot into the data directory, and starting the old line. Automatic rollback applies inside the window set by doc 49. After the window, rollback is a manual runbook step.

Every step writes an audit event (doc 15) with the snapshot id and the approving role.

## 11. Health contract hook (Kodo, doc 46)

Kodo's module health contract (D10) has four probes: liveness, readiness, dependencies and self-test. Every dep implements them. The contract tests of doc 46 enforce this in CI, so a dep that cannot answer them does not ship.

| Probe | PostgreSQL line | pgvector | Valkey and Garnet (planned) |
|---|---|---|---|
| Liveness | Supervisor heartbeat from the child; the host sees the process | Not applicable (no process) | Heartbeat from the child |
| Readiness | Protocol ping over the loopback or socket, and the application role can connect | Extension loaded in the database, at a version in range | Protocol ping with a set and get on a reserved key |
| Dependencies | Disk free below `host.health.disk_used_warn_percent`; the running line equals the installed line | The database line is in the pgvector artifact's `forLine` set | Memory under warn threshold |
| Self-test (scheduled) | Checksum verification, sampled restore drill (doc 33), migration level check | Vector round trip (insert, nearest-neighbour query, delete) | Round trip and eviction check |

Drift is a first-class failure. The running upstream version must equal the installed package's `version`. A mismatch turns the dep red, because it means something replaced a binary outside Bazaar's path.

Failure mapping: a failed critical probe (`critical: true`) opens the readiness gate of every dependent module and raises an incident (doc 46 incident mode). Non-critical failures are amber. Every state change is an audit event.

Kodo's 0-100 score counts deps like any module. Chaos mode (owner-enabled, sandbox copy only, doc 46) may kill the database child in the sandbox to test recovery. It never runs against the production data directory.

## 12. How the module manager shows Deps (PLANNED)

- A Deps section in the module manager, with two filter chips: Deps and 3rd party. A dep matches when its tags say so. Chips combine. The palette command for the view has a mnemonic (to be assigned in the mnemonic map of doc 41; Q10), with no Ctrl chords.
- Each row shows: name, line and version (for example `PostgreSQL 18.6.0, line 18, role stable`), state (running, stopped, degraded, aging, candidate), dependents (which modules use it), update status under the policy in force, licence chip (SPDX and the compatibility verdict), health score from Kodo, and source (bundled, built from source, or external if Q2 is accepted).
- A hidden dep shows a "hidden" marker and its supervision details. It has no user action besides the module-level actions.
- Actions are gated by role (custom roles, DECIDED): view, pin, defer, skip, approve move, restart, roll back. Each action is audited and the audit entry names the role.
- The compatibility matrix (section 9.1) is one tab of the same view.
- The licence block is always visible, with the notice file one click away (D8).
- No dep row offers "install", "uninstall" or "edit the path" for a bundled dep. Those controls exist only for external connections if Q2 is accepted.

## 13. Licence checks (link doc 47)

Doc 47 holds the licence checks in full. This section states the rules that apply to deps. Doc 47 is not in this worktree.

### 13.1 Rules (DECIDED where marked, PLANNED otherwise)

1. Dependencies must be AGPL-compatible (DECIDED, D8).
2. The Windows build is OSI-only (DECISIONS-LOG C23). A dep licence must therefore be OSI-approved, not only AGPL-compatible. A source-available licence that is not OSI-approved is refused for every platform by default (PLANNED). Doc 47 owns the allowlist.
3. The licence is recorded in the manifest as an SPDX expression and verified by a licence scan of the shipped files and the SBOM at build time (PLANNED). A manifest claim alone is never enough.
4. The install refuses a dep whose verdict is incompatible. No override exists for bundled deps. A third-party dep that is compatible but unverified installs with a warning, following the marketplace rule (DECIDED, brief).
5. The licence is shown to the user before install and in the module manager (DECIDED, D8).
6. Notices are shipped with the package, generated from the SBOM, and the module manager links them.
7. A copyleft dep that is compatible but still needs counsel review shows "counsel review pending" until counsel closes it. This is the case for pg_search (below).

### 13.2 Expected licences for the named deps (to be verified by the scan, not assumed)

| Dep | Expected licence | Verdict | Note |
|---|---|---|---|
| PostgreSQL | PostgreSQL License (permissive, OSI) | Compatible | Notice shipped |
| pgvector | PostgreSQL License | Compatible | Notice shipped |
| pg_search | AGPL-3.0 | Compatible with AGPL, counsel review pending | doc 04 says bundling needs counsel; Linux only (section 6.5) |
| Valkey | BSD-3-Clause | Compatible | |
| Garnet | MIT | Compatible | |
| .NET runtime | MIT | Compatible | Notice shipped with the self-contained publish |

These are expectations. The licence scan decides, and a mismatch fails the build.

### 13.3 The module exception (OPEN)

The AGPL core carries a written module exception for modules that use the public module API (brief, licensing). Deps do not use the module API. They are separate programs run as child processes, or libraries loaded into a child. Whether the exception covers them, or only ordinary AGPL compatibility applies, is Q7 in section 18. Until the owner decides, the stricter reading applies: ordinary AGPL compatibility.

### 13.4 Contribution terms

Doc 47 and CONTRIBUTING.md govern contributions. CONTRIBUTING.md uses DCO only; the brief adds a CLA for core code. A dep recipe is build tooling, and whether it needs the CLA is part of the open licensing question (section 17).

## 14. Supply chain and trust (PLANNED)

### 14.1 Build and verification

- Each dep line has a recipe in the repo: upstream source pinned by tag and commit SHA, toolchain versions, build flags, and the expected output hashes.
- Upstream verification: source archives are checked against a pinned SHA-256 and against the upstream signature where one is published. For Windows binaries the source of truth is Q1 in section 18.
- The build runs in CI on a clean image. The output gets an SBOM, a provenance record (inputs, recipe hash, toolchain), and a signature by the Bazaar module signing key. Signing keys stay on the build side (brief, releases), and an operator's own build signs with the operator's key.

### 14.2 Signing and trust

- Signed modules plus declared permissions approved at install (D9). For a bundled dep, the approval is the signed release the user installs, and the module manager shows the declared permissions before install.
- Verified tag: the marketplace tag (automated checks, human review, monitored sandbox run, auto-revoked on a failing later version) applies to third-party deps offered through the marketplace. Bazaar's own dep packages get the same automated checks and a human release review. Their tag is shown as "first-party build" (PLANNED).
- A dep never runs inside a dev sandbox. Dev sandboxes (full tier, DECIDED) are for modules under development. Bundled deps run supervised, as described in section 6.

### 14.3 Permissions

Deps declare permissions in `permissions` (section 8.2). The set is deliberately small:

- `fs.dataRoot`: read and write, scoped to the dep's data directory.
- `net.listen`: loopback only, scoped ports. Any broader scope is refused by the validator (security floor).
- `proc.child`: not granted to deps. A dep does not spawn processes outside its own tree.
- `secrets`: not granted to deps. Secrets are injected by the host when needed (doc 14).

## 15. Settings registry rows (PLANNED)

Rows follow the doc 03 registry format. Keys are dotted and prefixed by area. Changes are audited.

| Key | Type and values | Default | Scope | Who may change | Approval needed |
|---|---|---|---|---|---|
| `deps.postgresql.line` | `newest`, `stable`, `pinned:<version>` | `newest` on new installs; existing installs keep their current line | install | operator | Yes, owner tier, for a move to a new major |
| `deps.postgresql.port` | `auto` or integer 1024 to 65535 | `auto` | install | operator | No |
| `deps.postgresql.external` | bool | `false` | install | owner | Yes, owner tier (Q2) |
| `deps.postgresql.listen` | `auto` (loopback TCP on Windows, socket on Linux) | `auto` | install | not settable below the floor | Not a setting: loopback only |
| `deps.extensions.pgSearch` | `auto`, `off` | `auto` on Linux; not offered on Windows | install | operator | No |
| `deps.update.deferWarnDays` | int, 7 to 365 | 60 | install | operator | No |
| `deps.health.selfTestSchedule` | `weekly`, `daily`, `off` | `weekly` | install | operator | No; `off` raises a Kodo amber |

Security floors, not settings: loopback-only listening, data checksums on, generated credentials, no deps with `proc.child` or `secrets`, no non-OSI licence. A setting may tighten these rules and never loosen them.

Default-line rule: an existing install never moves to a new major on upgrade of Bazaar. The move is the owner's decision (section 10.3). A new install takes `newest`.

## 16. Testing (doc 34 extension, PLANNED)

- Matrix: every supported line (newest and stable) runs the full data test suite of doc 04 against real database binaries. Doc 04 says tests run against PostgreSQL 18 with pgvector; under this doc they run against each supported line.
- Beta track: the day-0 job (5.4) runs the same matrix on the upstream beta.
- Fault tests: kill the database child mid-transaction, kill the host mid-turn (doc 03 kill test), occupy the chosen port, fill the data volume, corrupt a page and confirm refusal to start, mismatch the data line and confirm the refusal in section 6.6.
- Tree kill: start the host with a database child, kill the host, confirm no `postgres` process remains (Windows Job Object and Linux parent-death signal).
- Move drill: the procedure in section 10.3 runs in CI against a seeded fixture (GC rows, vector rows) and must end with the consistency checks passing, and a rollback drill must restore the pre-move state exactly.
- Licence scan and manifest validation run on every dep package build and fail the build on a verdict or tag error.
- Contract tests for the health probes (doc 46) run per dep.

## 17. Relations to existing docs: conflicts (flagged, not silently resolved)

These conflict with the 2026-11-13 decisions. The owner decision governs, and each doc needs an amendment. None is changed in this doc.

1. **doc 03, section "NUC acceptance checklist", line 146.** Says PostgreSQL 18 "runs as a service with pgvector and survives reboot". Conflicts with D5 (bundled, hidden, supervised child process). Amend to the child-process model in section 6.2.
2. **doc 03, line 23, and doc 04, lines 10 and 87.** Name PostgreSQL 18 as the only line. Conflicts with D2 and D4 (two lines, newest and stable). Amend to lines and roles (section 5.3).
3. **doc 04, line 111, and doc 03, line 162.** Say pg_search bundling needs counsel. Consistent with section 13.2. This doc adds that the Windows build also excludes it through the OSI rule (C23), which doc 04 does not state.
4. **BAZAAR-COMPONENTS.md, section 2.** Uses "component" and a fixed `kind` list with no `dependency` value. The brief uses "module". Section 8.1 proposes `kind: dependency`. Term and kind need one answer (Q8).
5. **CLAUDE.md, 2026-10-25 licence decision, and NOTICE.** Apache-2.0 for Bazaar.Sdk and Bazaar.Contracts. The 2026-11-13 decision says AGPL across all repos. This is already flagged in the brief. Section 13 does not depend on the answer for deps, but the licence block of the SDK package does. Owner to confirm.
6. **CONTRIBUTING.md, around line 80.** DCO only. The brief adds a CLA for core code. Section 13.4 depends on this.
7. **Owner brief references.** Doc 41 exists only in the main checkout (branch claude/project-thread-b9omz8). Docs 46, 47 and 49 are not found in the main checkout or in this worktree. This doc links them by number and quotes the brief. Their content must be checked when they land, and the terms in sections 10, 11 and 13 may need changes.
8. **doc 37, distribution matrix.** Gives no dep rows. Expected once the install and build documents exist (section 7).
9. **doc 16 and doc 20, runtimes.** Golden image and engine adapter docs do not yet list runtime deps. Section 4 classifies them as `runtime` deps baked into images. Those docs need the same classification.

## 18. Open questions

Each OPEN item has options and the default the design would use until the owner decides, per the configurability rule.

- **Q1. Windows PostgreSQL binaries: source of truth.** (a) Build from source in CI and sign (most control, slowest, needs a Windows toolchain in CI). (b) Verified upstream binary archives pinned by SHA-256 and upstream signature where published (fastest). Default until decided: (b) for the install path, with (a) as the Build-from-source path.
- **Q2. Bring-your-own database.** (a) Never; the database is always bundled. (b) Allowed as a hidden advanced setting (`deps.postgresql.external`), never in the install wizard, never installs anything, same health contract. Default until decided: (a).
- **Q3. .NET runtime as its own record.** (a) A registry record `host-runtime` with `embedded: true`, updated with server releases. (b) No record; the server version carries it. Default: (a), so the module manager can show it.
- **Q4. LTSC channel and the stable line.** The brief says never stay on stable. LTSC intends a long-term pin. (a) LTSC pins the stable line and receives security patches only, the one deliberate stay. (b) LTSC follows the forward rule with longer deferral windows. Default: (b), so the no-parking rule holds everywhere.
- **Q5. Meaning of "stable".** This doc reads "stable" as the previous major line, still supported upstream and fully tested by Bazaar. (a) Confirm. (b) "Stable" means something else (for example an upstream-designated stable release). Default: (a).
- **Q6. Data upgrade method for a major move.** (a) `pg_upgrade` with the snapshot as rollback. (b) Dump and restore. (c) Logical replication to a new line. Default: (a) for speed, with (b) as the fallback when `pg_upgrade` preflight fails, and both recorded in the audit trail.
- **Q7. Module exception for deps.** Does the AGPL module exception cover deps, or does ordinary AGPL compatibility apply? Default: ordinary compatibility (stricter).
- **Q8. Term and kind.** One word for the unit (module or component), and whether `dependency` is a new `kind` value. Default: "module" in product text, `kind: dependency` added to the manifest.
- **Q9. NuGet visibility.** Is the read-only compile-time summary enough, or should compile-time packages become listed items? Default: summary only.
- **Q10. Mnemonic for Deps.** To be set in doc 41's mnemonic map. Default: a short mnemonic such as `deps`, assigned there.
- **Q11. Extension lines when the database line moves.** If pgvector has no artifact for the target line, options are: block the move (default), or keep the old line with a warning and a deadline. Default: block.
- **Q12. Beta availability for extensions.** Should pgvector and pg_search betas follow the same channel rule as the database, or be treated as separate candidates? Default: same rule, a candidate set is only installable when every required extension has a candidate artifact.
- **Q13. Snapshot retention after a major move.** Doc 33 sets retention for backups. Options: the pre-move snapshot kept for the doc 33 window (default), or kept until the owner confirms the new line for N days. Default: doc 33 window, with the owner's confirmation required before deletion.

## 19. Build plan (PLANNED, nothing built)

1. Manifest model: add `kind: dependency`, the dep fields, and the validator rules in section 8.4. Depends on the component manifest work in BAZAAR-COMPONENTS.md.
2. Child-process supervisor for hidden deps: start, heartbeat, restart with backoff, circuit breaker, Job Object and parent-death tree kill. Depends on doc 03 (host), doc 46 (incidents).
3. Database dep package for Windows and Linux, line 18 first, with the health probes of section 11 and the tests of section 16.
4. pgvector package per line, with the artifact rule of section 8.3.
5. Settings rows of section 15 in the registry.
6. Module manager Deps view and compatibility matrix (section 12). Depends on doc 41 and the client architecture (doc 27).
7. Line 19 package and the move procedure of section 10.3, once PostgreSQL 19 is GA and the owner approves the move.
8. Licence scan and notice generation (section 13), with doc 47.
9. Install and Build-from-source documents (section 7), and their rows in doc 37.

Each step ships with its tests, as in every Bazaar work package.
