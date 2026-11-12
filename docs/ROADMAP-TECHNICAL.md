# Technical roadmap

Status: DRAFT (2026-11-12). Compiled from the merged PR history on `main`, the design set (`docs/design/`, with `DECISIONS-LOG.md` authoritative), the spikes (`docs/spikes/`), `docs/FORWARD-FACING.md` and `docs/FEATURES.md`. Each row cites its source. Nothing here is a date promise. Where a date comes from a vendor or an upstream schedule, the row says so.

The feature view and the Beta stages are in [`ROADMAP.md`](ROADMAP.md). This document lists the technical milestones those features rest on: the database, the wire, modularity, performance, packaging, security and CI.

## How to read the status column

| Status | Meaning |
|---|---|
| **Done** | Merged on `main`. Named PRs are given. Coverage may still be partial. |
| **In progress** | Some merged work exists; a named part is open, or an open PR carries it. |
| **Planned** | Designed or decided, with no code on `main` yet. |
| **Blocked** | Waiting on a named spike, an owner action or counsel. |

Milestones are ordered by dependency, not by date. "Exit check" is the test that closes the milestone. Per the project rule, an agent's own report never closes a milestone; the server's clean build and test run does.

---

## T1. Managed PostgreSQL (the database is inside the app)

The owner's headline milestone: Bazaar packages and manages its own PostgreSQL. The owner never installs or runs it. The server starts it, owns its data directory, and gives the rest of the server a connection.

| Part | What it covers | Status | Source |
|---|---|---|---|
| P1 design note | Managed PostgreSQL as a Bazaar component: process, bundle, secrets, backup and upgrade | Done | PR #130; `docs/research/notes/Bazaar server packaging/packaging.md` |
| P2 component skeleton | `Bazaar.Components.ManagedPostgres`, `AddBazaarManagedPostgres()`, `Bazaar:Database:Mode` (`managed` / `external`) | Done | PR #130; `src/Bazaar.Components.ManagedPostgres` |
| P3 child-process controller | initdb, start, stop, readiness, adopt-or-refuse, restart backoff | Done in code; Linux integration against a real local PG is part of the test suite | `src/Bazaar.Components.ManagedPostgres/Process` |
| P4 version probe and bundle layout | `pg/<major>/` layout, refusal on major mismatch, `db status` | Done in code | `Bundle/` (`BundleLayout`, `MajorVersionPolicy`, `ServerVersionProbe`) |
| P5 host wiring | Start managed Postgres before migrate and readiness; stop with the host | Done | PR #338 |
| P6 Linux Compose | Compose with PG 18 default and a PG 19 override on its own volume | Done for PG 18 and PG 19 files; CI job for the schema tests is tracked under CI | PR #204; `deploy/compose/compose.yaml`, `compose.pg19.yaml` |
| P7 Windows bundle | PG and pgvector binaries fetched, hashes verified, pgvector built with MSVC, layout `pg/<major>/` | Done for PG 18.7 and pgvector 0.8.2 (PRs #322, #440, #480, #494). PG 19 bundle is not on `main` yet | PR #322; `packaging/postgres/versions.txt`, `build-pgvector-windows.ps1` |
| P8 Windows smoke run | First-run initdb and migrate, host restart, crash (kill postgres), reboot, upgrade refusal, backup and restore, on the owner's PC | In progress. Needs the owner's PC run and the 4 skipped integration tests (`BAZAAR_TEST_PG_BIN`) | Project memory; `packaging/postgres` |
| P9 backup, restore and upgrade | `pg_dump` backup job, restore runner, `pg_upgrade` runner, scheduled dumps, `bazaar db backup` and `bazaar db upgrade` CLI | Done (PRs #372, #394, #460, #482). Restore drill on a real cluster is tracked under T10 | `src/Bazaar.Components.ManagedPostgres/Maintenance`; `docs/runbooks/backup-restore-drill.md` |
| P10 licence and notices | Bundle licence file and counsel question list | Planned | `packaging/postgres` |
| Managed password store | DPAPI on Windows; AES-GCM file with a systemd credential key on Linux; trust-then-set-password bootstrap; the owner never types a password | Done (PRs #321, #482) | `src/Bazaar.Components.ManagedPostgres/Secrets`, `src/Bazaar.Server.Providers.Windows/Postgres` |
| Database connection | Host uses the managed connection through one shared `NpgsqlDataSource` | Done | PR #338; `IManagedPostgresConnectionProvider` |

**Exit check (T1):** a fresh Windows PC and a fresh Linux host both start, migrate, serve, survive a crash and a reboot, back up, and refuse a major-version mismatch, with no manual database step. Windows P8 is the open part.

## T2. PostgreSQL 18 and 19, both supported (dual-version)

The rule is in `docs/FORWARD-FACING.md`: code and tests cover the newest release and the current stable release at the same time. Neither is dropped.

| Item | Status | Source |
|---|---|---|
| PostgreSQL 18 pinned to 18.7 (bundle, Compose, CI) | Done | PR #494 (18.6 in PR #440) |
| pgvector 0.8.2, SHA-256 pinned | Done | PR #480 |
| PostgreSQL 19 report-only CI run on the RC | Done (report-only, not a gate) | PR #497 |
| PostgreSQL 19 Compose override | Done (file exists; image tag is `pgvector/pgvector:pg19`) | `deploy/compose/compose.pg19.yaml` |
| PostgreSQL 19 Windows bundle and pgvector build against the 19 RC | Planned. Build pgvector 0.8.7 against the PG 19 RC, as the Windows bundle does | `docs/FORWARD-FACING.md` section on PostgreSQL |
| `pg_upgrade` 18 to 19 path | Planned as a gated path; the runner exists (T1, P9) | `PgUpgradePlanner` |
| PostgreSQL 19 GA | Scheduled by upstream for 2026-11-19 (vendor schedule, not a Bazaar date). The row closes when the PG 19 bundle passes the same checks as 18 | `docs/FORWARD-FACING.md` |

**Exit check (T2):** the schema and integration suites pass on both 18 and 19 in CI as gates, not reports, and a 18 to 19 upgrade runs on a copy of a real cluster.

## T3. Schema, migrations and the GC log

| Item | Status | Source |
|---|---|---|
| Embedded, versioned SQL migrations; `migrate` subcommand | Done | `src/Bazaar.Server.Host/Program.cs`; `src/Bazaar.Server/Data/Sql` |
| Common schema, migrations `common/0001` to `0020` | Done | `src/Bazaar.Server/Data/Sql/common/` |
| Per-area migration folders (`mail/0021`, `ryusei`, `souls`, `interrupts`, `kanban`, `council`, `roadmap`, `turns_inflight`, `community`, `remote`, `mentions`, `agent_turns`, `company_memory`) | Done | same folder |
| Architecture test freezing the migration namespace (`common` frozen to new migrations) | Done | `tests/Bazaar.Architecture.Tests` |
| GC log in its own tables, hash-partitioned by GC, per-GC sequence numbers | Done (schema and reader/writer abstractions) | `docs/design/04`; PRs #374, #383, #437 |
| Postgres `NOTIFY` fan-out with one `LISTEN` connection, reconnect and resync | Done | `GcLogServiceCollectionExtensions`; `CoalescingGcNotifier` / `GcNotifyListener` |
| Per-person encrypted GC message bodies | Done | PR #437 |
| Append-only enforcement by triggers, roles and column grants; audit role tested | Done | `docs/design/04` and migrations 0001, 0004, 0008 |
| Audit hash chain under an advisory lock | Done | migration `common/0004`, `Security/Audit` |
| Vault (Markdown in git) with Postgres as index | Planned (index tables exist; writer and access layer open) | `docs/design/18`, `docs/FEATURES.md` |
| Full-text search provider (Postgres FTS) on Windows | Done (search document migration `0007`) | `docs/FEATURES.md` section 10 |
| `pg_search` provider for Linux | Blocked on counsel review | `docs/FEATURES.md` section 15 |
| Erasure mechanics (per-person key erasure) | Blocked on OPEN design; the erase function and role exist | `docs/design/14`; migration `0001` |
| Settings registry persisted with audit per change, floors enforced | Planned (`ST1`) | `docs/design/03`, `docs/design/15` |
| Dedicated migrations for every `docs/design` table group still to come (taint store, in-flight calls, GC access requests) | In progress: several PRs open or on branches (#380, #381, #446) | PR list |

**Exit check (T3):** the Stage 3 check (fake agent posts to a GC, client replays after a dropped socket) runs on real Postgres in CI. The spikes S3 (GC log at scale) and S25 (restore drill) gate the schema decisions; S25 is recorded as pass on Linux and Windows.

## T4. Wire, transport and sync

| Item | Status | Source |
|---|---|---|
| Wire codec decision: JSON via source-generated `System.Text.Json` behind `IWireCodec`; Protobuf rejected | Done (decision) | Spike S6; `docs/design/02`; `docs/design/DECISIONS-LOG.md` |
| WebSocket transport, reconnect, replay, idempotent requests | Done per `docs/FEATURES.md` section 11. Protocol-harness test coverage is tracked under T14 | `docs/FEATURES.md` |
| Pairing: device authorization state machine, client device-authorization flow, `/pair/*` HTTP routes | Done | PRs #323, #325, #326; `docs/design/32` |
| Keystore-sealed refresh credential (Android) | Done | PR #324 |
| Read-only status listener (GET only) and `/health/live`, `/health/ready` | Done for the listener; health endpoints are tracked under T12 | `Bazaar.Server.Host`; `docs/design/03` |
| Binary tile format over HTTP for Constellation | Planned. JSON is the default; binary tiles are the one exception the design allows | `docs/design/02`; `constellation-scale-and-lod.md` |
| Typed contracts for ryusei, roadmap, Constellation projections | Done | PRs #419, #364 |

**Exit check (T4):** a client replays through a dropped socket with no loss or duplicate, on the real server, in CI. The protocol test harness is the vehicle.

## T5. Modular core (Bazaar.Core and the module contract)

The target is one module contract for all modules, first-party and third-party alike. The first-party GUI and panes are modules too (owner, 2026-11-12). The design is in `docs/design/BAZAAR-COMPONENTS.md` and `MODULARITY-AUDIT.md`.

| Item | Status | Source |
|---|---|---|
| Kernel component runtime (discovery, graph validation, ordering, lifecycle) | Done (wave 0) | `src/Bazaar.Server/Components`; `docs/design/BAZAAR-COMPONENTS.md` |
| Core-module audit; RyuseiFuda declares its gc and audit dependencies | Done | PR #360 |
| Split RyuseiFuda server code into `Bazaar.RyuseiFuda`; scheduling namespace `Bazaar.RyuseiFuda.Orrery` | Done | PRs #384, #452 |
| GC log abstractions in `Bazaar.GC.Abstractions` | Done | PR #374 |
| Constellation and Vault abstraction projects | Done | PR #385 |
| Client panes registrable as modules; pane modules register their own views | Done | PRs #373, #455 |
| Soft-dependency flag and `common` migration freeze | Done | Project memory; architecture test |
| Rename of the kernel folder to `Bazaar.Core`, and the migration order | Planned (wave 3 in `BAZAAR-COMPONENTS`) | `docs/FEATURES.md` section 12 |
| Parts naming `<Module>.<Part>` (RyuseiFuda.Workday, RyuseiFuda.Orrery, RyuseiFuda.Roadmap) | In progress. Orrery renamed; Workday and Roadmap still use older paths in places | PR #452; `docs/FEATURES.md` |
| Conduit (`IReachabilityProvider`, `ITransportModule`; Conduit.Cloudflared, Conduit.P2P, Conduit.Lan) | Planned. Design doc 39 is in an open PR, not on `main` | Doc 39 (PR #358 per `docs/FEATURES.md`) |
| Client module contract, trust from manifest, layout and theme as typed versioned JSON with the sandboxed DSL | Planned. Design doc 41 is in draft PR #483; the DSL name needs a clearance check | Project memory |
| Per-company client builds with extra modules compiled in | Planned. Install by QR or link is not designed | Project memory |

**Exit check (T5):** an architecture test proves no module depends on a head, and a third-party sample module loads through the same contract a first-party module uses.

## T6. Performance

| Item | Status | Source |
|---|---|---|
| Client startup: ReadyToRun on Windows and Linux heads; preloader module with autostart | Done | PR #420 (doc 40); `Bazaar.Client.Core/Preload`; R2R set in client `.csproj` |
| Linux single-instance forwarding for the warm process | Done | PR #458 |
| PC startup benchmark script (hidden by default) | Done | PR #458; `scripts/perf/startup-bench.ps1` |
| Baseline PC benchmark document | In progress, draft | PR #484 (draft, per project memory) |
| Server GC enabled on the host | Done | `Bazaar.Server.Host.csproj` (`ServerGarbageCollection` true) |
| Server DATAS choice | Planned. Not verified in the project files | Project memory |
| Client GC: workstation, concurrent | Done | Client `.csproj` files |
| Client TieredPGO and low-latency GC evaluation | Planned | Project memory |
| Constellation scale: quadtree tiles, LOD served from the shared graph source | Done (tiles PR #386; LOD PR #312) | `docs/design/constellation-scale-and-lod.md` |
| Constellation renderer core as one spatial grid, generic bulk-node core | Done | PRs #468, #473, #479 |
| Spike S14 (graph layout tick at 5k and 10k nodes) | Blocked on owner hardware for the absolute budget | `docs/spikes/S14.md` |

**Exit check (T6):** the PC benchmark runs on the owner's machine and its numbers are recorded in the baseline doc; each perf claim names its benchmark.

## T7. Packaging and release

| Item | Status | Source |
|---|---|---|
| MSI smoke-install script; unsigned MSIX for the client | Done | PRs #201, #206 |
| Chocolatey package | Done | PR #202 |
| AppImage build script and manifest validation | Done | PRs #199, #200 |
| RPM for the server host | Done | PR #205 |
| OCI image and Compose | Done | PR #204 |
| Flatpak, Snap, Winget, Scoop, MSIX, macOS and Linux deb directories | Present in `packaging/`. Their status is not verified in this roadmap | `packaging/` |
| Release workflow on version tags | Done | `.github/workflows/release.yml` |
| Release channels LTSC, Stable, Beta, Alpha, Latest-Commit, chosen by the operator | Done for the workflow; LTSC is planned | `docs/VERSIONING.md` |
| Code signing (Windows and Linux) | Planned; no signed build yet | `docs/FEATURES.md` |
| Velopack client updates | Planned | `docs/ROADMAP.md` Stage 7 |
| Version format `VersionPrefix` in `Directory.Build.props` | Done | `docs/VERSIONING.md` |

**Exit check (T7):** a signed Windows installer and a Linux package install, update on their channel, and roll back, with the release workflow producing them.

## T8. Security and identity

| Item | Status | Source |
|---|---|---|
| Identity Service: single chokepoint for provider and GitHub App keys | Partial | `docs/design/14`; `src/Bazaar.Server/Identity`, `Secrets` |
| Bootstrap secret resolver (`env:` and `file:`) | Planned (`H2`) | `docs/design/03` |
| Audit hash chain and DB writer role that cannot UPDATE audit rows | Done (chain and role tests, migration `0004`) | `docs/design/15` |
| Taint tracking and approvals; reader sessions for untrusted input | Partial (rules, policy, audit chain, approval service; approval card UI open) | `docs/FEATURES.md` section 3 |
| SoulGuard secrets scan on memory writes | Partial | `docs/FEATURES.md` |
| Remote access, TLS termination and key pinning, rate limits | Partial | `src/Bazaar.Server/Remote`, `Tls`, `RateLimits`; `docs/design/32` |
| Keystore-sealed credentials on Android | Done | PR #324 |
| CodeQL advanced workflow on every PR | Done | PR #287; `.github/workflows/codeql.yml` |
| Secret scanning path allowlist for test fixtures only | Done | Project memory |
| Gateway: per-agent model policy, deny by default | Partial | `docs/design/05`; `src/Bazaar.Server/Gateway` |
| Gateway: floors enforced by settings (tighten only) | Planned (`ST1`) | `docs/design/03`, `docs/design/15` |

**Exit check (T8):** the security review closes with no open blocking finding in the design set, and the taint rules are covered by a rule-table test.

## T9. Model gateway and agent runtime

| Item | Status | Source |
|---|---|---|
| Gateway contracts, fakes, three adapters (`openai_chat`, `anthropic_messages`, `gemini_generate_content`), policy, health, usage | Done | `docs/FEATURES.md` section 8 |
| Owner's Anthropic key as a config-defined gateway source; Haiku route seeded for dev responders | Done | PRs #451, #456 |
| FreeLLMAPI provider: usage, health and expected-set auditor | Partial. Live key check (spike S17) pending | `docs/design/05b`; spike S17 |
| `custom/*` routing methods | Planned (rules OPEN) | `CLAUDE.md` |
| Turn runner in production; model tool calls dispatched through `IToolDispatcher` | Done | PRs #447, #457, #461 |
| Wake loop behind `IAgentScheduler`, off by default | Done | PR #442 |
| Loop guard limits from configuration; loop guard mention parsing on the post path | Done | PRs #404, #438 |
| Psych Ward state machine; loop-guard trips into it | Done | PRs #406, #467, #469 |
| RyuseiFuda agent tools through the dispatcher; per-agent wake cap; chain depth | Done | PRs #349, #353, #359, #463, #370 |
| Crash-safe tool calls (in-flight call store) and session taint store | In progress. Open PRs #380, #381 | PRs #380, #381 |
| Coding-agent harness and edit-format benchmark | Blocked on spike S18 (owner's API key and cap) | `docs/spikes/S18` (not in repo; spike listed in `docs/design/35`) |

**Exit check (T9):** the scripted multi-agent day runs in replay with no human input: shifts, a vote, a blocked task, a loop-guard trip and an approval.

## T10. Testing, verification and CI

| Item | Status | Source |
|---|---|---|
| Solution build and tests on Windows and Ubuntu (`build.sh` / `build.ps1`) | Done | `.github/workflows/ci.yml` |
| `Bazaar.Testing` (fixtures and helpers) | Done | `src/Bazaar.Testing` |
| Architecture tests | Done | `tests/Bazaar.Architecture.Tests` |
| Exit-check test project | Done | `tests/Bazaar.ExitCheck.Tests` |
| PostgreSQL fixture with a template database, `BAZAAR_TEST_PG` | Done (fixture exists; the 4 skipped integration tests need the owner's PC) | Project memory; `tests/Bazaar.Components.ManagedPostgres.Tests` |
| Postgres 18 and 19 CI matrix; PG 19 as report-only | In progress. PG 19 gate pending | PR #497 |
| Canary job for forward-facing dependencies | Done | PR #421; `.github/workflows/canary.yml` |
| Dependency updates: Dependabot and the canary; Renovate config | Done for config (PR #433). Renovate app install was denied on 2026-11-11, so Dependabot and the canary are the fallback | PR #433; project memory |
| Verification gate: clean-clone build, test-count drop, new skips, timeouts recorded as success, UI change without a headless test | Planned (`T4` in the dev plan) | `/mnt/project-files/plans/bazaar-dev-plan.md` |
| Avalonia.Headless test coverage for UI changes | In progress, per pane | PRs #319, #417 |
| Property tests for GC interleavings (seed logged) | Planned | `docs/design/34` |
| Protocol harness (in-process Kestrel, `ClientWebSocket`) | Planned or in progress; not verified | `docs/design/34`, `docs/design/02` |

**Exit check (T10):** every behaviour change has a test, the build and test run pass on both OSes and on PG 18 and 19, and the verification gate blocks a seeded bad change.

## T11. Clients and the GUI

| Item | Status | Source |
|---|---|---|
| `Bazaar.Client.Core` shared Avalonia library: shell, theme model, connection client, key router, panes | Partial | `docs/FEATURES.md` section 6 |
| Windows head with `--panes` window and headless tests | Done | PR #319 |
| Linux head, single-instance forwarding | Done | PR #458 |
| Android head: device pairing in startup, GC pane in the phone pager, API 37 target | Done | PRs #369, #311, #439 |
| Android push, Play closed-test track | Blocked on the owner's tester list and device spike S7 | `docs/FEATURES.md` section 15 |
| Avalonia 12.1.6 and GA Microsoft.Extensions.AI packages | Done | PR #495 |
| MailKit and MimeKit 5.0.0 | Done | PR #496 |
| Avalonia 12.2 preview | Planned: watch, not adopted | `docs/FORWARD-FACING.md` |
| RyuseiFuda panes: schedule, roadmap, Workday, trigger chips, pause and resume | Done | PRs #417, #462, #476, #477, #478 |
| Constellation renderer: node styles, epoch and sequence keys, spatial grid | Done | PRs #471, #472, #473 |
| Monaco editor, diff and PR panes | Blocked on the Monaco gate S12 | `docs/FEATURES.md` section 15 |
| ConPTY terminal pane | Blocked on S11 and the widget choice | `docs/FEATURES.md` section 15 |
| Theme file driving Avalonia and Monaco | Partial (theme model merged) | `docs/design/27` |
| Command palette, mnemonic-driven, no Ctrl chords | Planned | `docs/design/27`, `CLAUDE.md` |
| Start screen dashboard | Planned | `CLAUDE.md` |
| Foldable layouts keyed by device class, posture and display (fold is the main chat; unfold is heavy work) | Planned. Design in draft PR #483 | Project memory |
| Local Co-Worker (NPU) on the client | Partial; NPU probe blocked on owner hardware S21 | `docs/FEATURES.md` |
| Roblox Studio bridge on the Windows head only | Partial | `docs/design/20` |

**Exit check (T11):** the owner runs a full day from the Windows client, and the Android head passes the Play closed-test build.

## T12. Operations, status and observability

| Item | Status | Source |
|---|---|---|
| Read-only server status page and listener | Done for the listener | `Bazaar.Server.Host` |
| `/health/live` and `/health/ready` | Planned (W8). Not verified on `main` | `docs/design/03` |
| Observatory: live agent activity and model lanes near limits | Partial (usage rollup in flight) | `docs/design/23` |
| Usage records labelled by source, `native` or `pooled` | Planned in full (`GW8`) | `docs/design/05` |
| Admin panel inside the apps | Planned | `CLAUDE.md` |
| TUI status client | OPEN | `CLAUDE.md` |

## T13. Virtual machines and environments

| Item | Status | Source |
|---|---|---|
| VM environments, images, lifecycle and reconciliation | Partial | `src/Bazaar.Server.Vms`; migration `components/vms/0001` |
| Hyper-V provider and HvSocket transport | Blocked on owner-hardware spike S8 | `docs/FEATURES.md` section 15 |
| QEMU/KVM backend, Linux as a first-class tested host | Planned (after v1 candidates) | `docs/ROADMAP.md` |
| Guest agent project | Partial (project exists) | `src/Bazaar.Guest.Agent` |
| Browser profiles with credential leases | Planned | `docs/design/16`, `docs/design/14` |
| Godot headless adapter; Roblox Studio bridge | Partial | `src/Bazaar.Engines` |

## T14. Backup, restore and export

| Item | Status | Source |
|---|---|---|
| Backup contracts, archiver, base backup, retention, sidecar | Done with fake engines and a real `pg_basebackup` path run in the drill | `docs/design/33` |
| Scheduled base backups | Done | PR #394 |
| Scheduled `pg_dump` backups of the managed cluster | Done | PR #460 |
| Restore runner with PITR and sequence floor | Done; spike S25 passed on Linux and Windows for PG 18.6 | `docs/spikes/S25.md`; `docs/design/DECISIONS-LOG.md` |
| Offsite backup (restic adapter) | Partial. No live restic run yet | `docs/design/33` |
| `bazaar archive_command` used as a live archive | Planned. The drill used `cp` | `docs/design/33` |
| Export | Planned (part of doc 33) | `docs/FEATURES.md` |
| Restore drill on Windows and Linux as a release gate | Planned (Stage 9) | `docs/ROADMAP.md` |

---

## Blockers that gate technical milestones

From `docs/FEATURES.md` section 15 and project memory. Each one needs the named owner action, not more code.

- Owner-hardware spikes: S2 (Garnet on Windows), S7 (Android device), S8 (Hyper-V), S11 and S12 (terminal and Monaco), S21 (NPU).
- Subscription and key spikes: S17 (FreeLLMAPI live key), S18 (Anthropic cap and key).
- GitHub: S22, which needs the owner's GitHub App and throwaway repositories.
- Counsel: `pg_search` provider, licences, the notices file (P10), the DSL name (RUBEDO) clearance.
- Owner decisions still open: erasure mechanics, Soul Marketplace v1 scope, the Renovate app install (denied 2026-11-11).

## Relationship to other docs

- [`ROADMAP.md`](ROADMAP.md): the Beta stages and the v1.0 readiness gate.
- [`FEATURES.md`](FEATURES.md): the named-feature index with status per feature.
- [`FORWARD-FACING.md`](FORWARD-FACING.md): dated dependency audit and the dual-version rule.
- [`design/DECISIONS-LOG.md`](design/DECISIONS-LOG.md): authoritative for resolved conflicts.
