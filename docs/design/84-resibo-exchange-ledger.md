# 84. Resibo exchange ledger: everything accounted for

Status: PLANNED. Design intent only. No code, migration or module in this document exists yet. The owner direction of 2026-11-19 (below) is DECIDED; every disposition, name and plan below it is PROPOSED until the owner confirms it. Owner questions: 7 (section 8).
Depends on: 40-core-feature-modules-audit.md (module shape, soft dependencies), BAZAAR-COMPONENTS.md (kernel, section 4.2 `Principal`, section 6 boundary rules), DECISIONS-LOG.md (rounds 13, 25, 26), 42-deps-as-modules.md, 46-kodo-health-module.md, 48-gap-decisions-security-identity.md, 49-gap-decisions-operations.md.
Referenced here: 70 to 83 (the sibling docs this ledger routes to). Used by: every doc 70 to 83 (each row below is the single place that says where a Resibo feature went), and by the main session when it applies the amendments in section 5.

## 1. Purpose and inputs

Resibo (Receipt System V3, a sister program) has features Bazaar can use. The owner asked, on 2026-11-19, to incorporate everything from Resibo that can benefit Bazaar, to over-engineer rather than trim, to account for everything, and, where a Resibo feature conflicts with a Bazaar design, to make it a separate module instead of choosing. This document is the ledger that proves that: one row per named Resibo feature, system, API, principle and convention, each with exactly one disposition.

Inputs: the Resibo inventories under `/mnt/project-files/convergence/inventory/` (`resibo-plan-and-top.md`, `resibo-01-09.md` to `resibo-50-56.md`, `resibo-code.md`) and `CONVERGENCE.md` sections 2, 3, 7 and 8. Resibo's repo is read-only reference material.

### 1.1 Owner direction and standing rules (DECIDED)

- 2026-11-19 (owner): incorporate what benefits Bazaar; conflicts become separate modules; account for everything.
- 2026-11-19 (owner): providers are plain modules on Core, no selector or role setting (PR #919). Actor attribution is one `Principal` type (BAZAAR-COMPONENTS 4.2).
- 2026-11-19 (owner): shared libraries and modules hold contracts and pure logic only, with no third-party dependencies. Providers are separate modules.
- 2026-11-19 (owner cards, CONVERGENCE decisions log): generic `Principal` and `Scope`; Tabula carries retention window, operation vocabulary and a reason-tagged access-grant event; Aludel has one `IKekProvider`; Resibo-only build choices (licence, database, version scheme) are withdrawn for the shared libraries. Where this ledger offers a "second module" for such a choice, it is a documented slot, not a build instruction.
- Standing DECIDED items that no row may contradict: JSON over source-generated `System.Text.Json` behind `IWireCodec` and no Protobuf tooling (2026-10-25); Postgres on the server; AGPL-3.0 with the module exception; SemVer `0.MM.PP+commit`; Avalonia clients; Resource Economy REJECTED.

### 1.2 Licence caution

Resibo's repository is proprietary, all rights reserved (inventory top-level section 5). This ledger adopts designs and ideas only. No Resibo source may be copied into Bazaar. Anything that needs Resibo code needs a written licence from its owner first (question 1, section 8).

### 1.3 Module names are working names

Docs 70 to 77 and 79 existed when this ledger was finished, and their module names are used below (all are working names in those docs too). Docs 78, 80, 81, 82 and 83 did not exist yet; their module names are this ledger's plan and are marked "(plan)". A name here is a pointer, not a decision. The ledger's short names (`Screening`, `Hardware`, `Access`) stand for the project names in this table.

| Doc | Subject | Modules (names from the doc, or plan) |
|---|---|---|
| 70 | Issue filer | `Bazaar.Server.IssueFiler` plus `.Abstractions`, sinks (`.Sinks.GitHub`) and sources (`.Sources.Kodo`, `.Sources.Dependencies`) |
| 71 | Dependency watcher and proving grounds | `Bazaar.Server.DependencyWatcher` (`.NuGet`, `.GitHub`, `.PyPI`, `.Osv`, `.RepoChangelog`), `Bazaar.Server.ProvingGrounds` (`.ScratchProcess`, `.Vm`, `.Docker`) |
| 72 | Access grants and groups | `Bazaar.Server.AccessGrants`, `Bazaar.Server.Groups` |
| 73 | Content screening | `Bazaar.Server.ContentScreening` (`.ClamAv`, `.VirusTotal`, `.WindowsDefender`, `.Archives.SevenZip`) |
| 74 | Hardware probe and resource ledger | `Bazaar.Server.HardwareProbe` (`.Windows`, `.Linux`, `.ReportImport`, `.Vulkan`), `Bazaar.Server.ResourceLedger` |
| 75 | Watchdog and sleep-wake | `Bazaar.Server.Watchdog`, `Bazaar.Server.SleepWake` (`.Systemd`, `.ActivationProxy`) |
| 76 | Reimport and export framework | `Bazaar.Server.Reimport`, `Bazaar.Server.ExportFramework` (`Bazaar.Server.Export.<Format>`), `Bazaar.Server.Narrative` |
| 77 | Status, incidents and support | `Bazaar.Server.StatusIncidents`, `Bazaar.Server.Support` |
| 78 | Testing kit and repo hygiene | extends `Bazaar.Testing`; repo checks (plan) |
| 79 | Account guardian | `Bazaar.Server.AccountGuardian` |
| 80 | MCP access and tool registry | MCP server module and tool registry (plan) |
| 81 | Program logs, audit vocabulary, retention | logs module (Annales) and Tabula additions (plan) |
| 82 | Pipeline runs and job classes | runs module and job classes over `Bazaar.Server.Jobs` (plan) |
| 83 | Client patterns | Client.Core and Rubedo additions, no new feature module (plan) |

## 2. The ledger

Disposition codes (one per row):

- **AAM** ADOPTED-AS-MODULE: new Bazaar module, in the named doc.
- **AIE** ADOPTED-INTO-EXISTING: enriches an existing Bazaar doc or module with the stated change.
- **AIB** ALREADY-IN-BAZAAR: Bazaar already has it; where.
- **SLC** SHARED-LIB-CANDIDATE: goes into a shared library (contracts and pure logic only; providers stay in Bazaar).
- **ROD** RESIBO-ONLY-DOMAIN: stays out of Bazaar; the extracted pattern, if any, is named.
- **REJ** REJECTED, with a reason.

Source paths are relative to the Resibo repo. `DDnn` means `docs/apis/v3-deepdive-nn-*.md`. `PR` means `docs/PRINCIPLES.md`. Row ids are stable; other docs may cite them (for example "R042").

### 2.1 Principles P0 to P5 and repo-level rules

| Id | Resibo item | Source | Disposition | Target and change |
|---|---|---|---|---|
| R001 | P0 V2 rule (legacy never a source of code) | PR 0 | REJ | No Bazaar predecessor exists. The sibling rule "no Resibo code copied" is section 1.2. |
| R002 | P1.1 package-per-API, one concern per file, soft 300 to 400 lines, CI hard ceiling | PR 1.1 | AIE | 78 `RepoHygiene` file-size ratchet (baseline may only shrink, as in BAZAAR-COMPONENTS 6). Per-module guide file: section 5 item 9. |
| R003 | P1.2 Provider Registry, many providers active at once | PR 1.2 | AIE | Core: an enumerable multi-active provider set (CONVERGENCE A1). Under PR #919 providers are plain modules, so "registry" means "all registered modules of a contract", with no selector. Amends BAZAAR-COMPONENTS 4 (the "one selected implementation" sentence). |
| R004 | P1.3 libraries and services behind small adapters | PR 1.3 | AIB | Abstractions plus provider module rule (doc 42, BAZAAR-COMPONENTS 4 and 6). |
| R005 | P1.4 no hardcoded TUI; menu data plus generic screen; closed list of custom screens | PR 1.4 | AIE | Doc 41 and 41c (Rubedo) plus the open `TODO(component)` in `FirstPartyPanes.cs`. Add the rule "closed, named list of custom panes; everything else is declared data". |
| R006 | P1.5 shared substrate without conflating domains (static hardware vs live ledger) | PR 1.5 | AAM | 74: `Hardware` (static probe) and `ResourceLedger` (live) stay separate modules. |
| R007 | P1.6 install dir: shared state at top, releases as siblings, setup deletes itself | PR 1.6 | AIE | Docs 37 and 45: installer is idempotent, never moves itself, strips dev content. Slot module in section 6 (source-clone updates). |
| R008 | P1.7 one OS process per Core API, gRPC only | PR 1.7 | REJ (default) | Bazaar default is in-process modules over `IWireCodec` JSON. The process-per-module mode is Core's out-of-process mode (section 6.5), not gRPC. |
| R009 | P1.8 sub-API gets its own doc when it has two of four traits | PR 1.8 | AIE | CLAUDE.md "every new feature is its own module" plus doc template `_BRIEF.md`: add the two-of-four test as the trigger for a module doc. |
| R010 | P1.9 backward-carrying: live capability is one function reusable by sweeps | PR 1.9 | AAM | 82 `Runs`: stage functions are pure and callable by both live runs and retroactive sweeps (reindex, re-score, re-embed). |
| R011 | P2.1 frozen contracts, FrozenDict | PR 2.1 | AIE | Section 5 item 3: records with `init`, `FrozenDictionary`, `ImmutableArray`; architecture test for mutable collections in contract types. |
| R012 | P2.1.1 constant tables are frozen too | PR 2.1.1 | AIE | Same convention and test as R011. |
| R013 | P2.2 immutable content-addressed blobs, logical id vs physical hash | PR 2.2, DD13 | AIE | `Bazaar.Server.Blobs` and doc 02 blob surface: section 5 item 4. |
| R014 | P2.3 append-only tracks, structurally no update or delete API | PR 2.3 | AIB | Hash-chained audit (doc 48), append-only GC log (doc 01). 81 adds a database-level deny (revoke plus trigger) test as the analogue of Resibo's authorizer. |
| R015 | P2.4 code and per-user data separate; deploy never touches data | PR 2.4 | AIB | CLAUDE.md repo rules; Vault and instance data outside git; doc 49. |
| R016 | P3.1 PR at most 99 commits (two-digit patch) | PR 3.1 | REJ | Bazaar's `pp` is not a per-commit tick (CLAUDE.md versioning). |
| R017 | P3.2 test tree mirrors packages; walker; real-pipeline bench | PR 3.2 | AAM | 78 `Testing.Kit` plus convention in section 5 item 10. |
| R018 | P3.3 forward-compat: feature detection, one shim per capability, lazy imports | PR 3.3 | AIB | `FORWARD-FACING.md`; add the one-shim-per-capability and drift-check rule (section 5 item 8). |
| R019 | P3.4 typed or learned data only through one registry; no shadow tables | PR 3.4 | AIE | 78 check `no_shadow_registry` (new SQL tables outside the owning component's namespace; already implied by BAZAAR-COMPONENTS 5). |
| R020 | P4.1 errors are data, never raised across the boundary | PR 4.1 | AIE | Section 5 item 1 (doc 02). Exception kept: authentication failures stay protocol-level, as Resibo's Auth. |
| R021 | P4.2 fail closed on security checks | PR 4.2 | AIB | Doc 15; strengthened for screening in 73 (a timeout means unscanned means unsafe). |
| R022 | P4.3 never silently override genuine conflicts | PR 4.3 | AAM | 76 `Reimport` three-way rule; convention in doc 15 and doc 44. |
| R023 | P4.4 graceful degradation (missing engine is unavailable, not failed) | PR 4.4 | AIB | `SoftDependsOn` and Kodo amber states (BAZAAR-COMPONENTS 4.1). |
| R024 | P4.5 structural isolation over permission checks | PR 4.5 | AIB | Per-person encrypted fields and per-GC access (docs 01 and 14); optional per-user store slot in section 6.3. |
| R025 | P5 concurrency bucket declared per API, reasoned then measured | PR 5 | AAM | 82 `Jobs.Classes`: every job declares a class (async I/O, thread, process, idle-only); a bench measures and records it. |
| R026 | No user data in the repo; real data gitignored | CLAUDE.md | AIB | CLAUDE.md rules on secrets and private material. |
| R027 | PR template per addition category, CI-parsed checklist | `docs/templates/`, `pr_checklist_enforcement` | AAM | 78 `RepoHygiene` (section 2.12 maps each template). Joins the existing "Docs section this implements" line. |
| R028 | Docs updated in the same PR; collective-enumeration checklist item | CLAUDE.md, PHASE_1_COMPLETE | AIE | CLAUDE.md "docs first" rule: add "update every doc that enumerates the thing" as a PR line (78). |
| R029 | Per-commit version tick in `PROGRAM_VERSION` | `common/version.py` | REJ | Bazaar version is `VersionPrefix` plus commit suffix; no tick file. |
| R030 | Per-API CLAUDE.md that survives and is self-contained | 53 files | AIE | Section 5 item 9. |
| R031 | Temporary deep-dive corpus deleted at the first release | `docs/apis/` | REJ | Bazaar design docs are permanent and status-tagged. |

### 2.2 Process topology, supervision, lifecycle

| Id | Resibo item | Source | Disposition | Target and change |
|---|---|---|---|---|
| R032 | Three-layer topology (core processes, clients, browser) | PROCESS_TOPOLOGY | AIB | Server plus Avalonia heads (doc 03, 27). Process split is the slot in 6.5. |
| R033 | Supervisor (outside every release, owns arbitration, boot, rollback) | `supervisor/`, DD38 | SLC | Kodo: slot choice, health contract, journal. Hosting stays in Bazaar (`Bazaar.Kodo.Sentinel`). |
| R034 | Supervisor lives outside releases, minimal, own self-update | DD38 | AIB | Kodo Sentinel and signed read-only slot (doc 46 section 1). |
| R035 | Two-phase verified self-update with mandatory owner confirmation | `self_update/reexec.py`, DD38 | AIE | Doc 46 and 49: Sentinel update needs owner approval that cannot be configured off, plus 14-day soak (R058). |
| R036 | Boot sequence: dependency-ordered launch with a readiness gate, fail closed | `boot_sequence.py` | AIB | BAZAAR-COMPONENTS section 3 start order; Kodo readiness gates (doc 46 section 10). |
| R037 | Rollback to prior release on failed health | `rollback.py` | AIB | Kodo A/B slots. |
| R038 | Sleep/wake of idle services, three policies | DD38, `sleep_wake/` | AAM | 75 `SleepWake`: `Never`, `IdleTimeout`, `ScheduledOnly`, with a classification table per module. |
| R039 | Linux systemd socket activation | `socket_activation_linux.py` | AAM | 75 provider module `SleepWake.Systemd` (Resibo's own Linux path is unverified; verify first). |
| R040 | Windows activation proxy (wake on first connection) | `activation_proxy_windows.py` | AAM | 75 provider module `SleepWake.WindowsProxy`. |
| R041 | Scheduled wake table | `sleep_wake/state` | AAM | 75; shared by RyuseiFuda triggers (R126). |
| R042 | Per-service version pins per channel, audit-logged | `version_pins.py` | AIE | Doc 49 and Kodo Fleet pane: pin a module to a release on a channel; pin changes are `Principal`-attributed audit events. |
| R043 | Single-instance services (closed justified list) and staged restart | `single_instance.py` | AIE | 75: heavy shared modules (local inference, doc 31) declare single-instance; restart is confirm target, stop, start, health-gate. |
| R044 | Dynamic start of versions on demand, available-versions store | `dynamic_start.py` | AIE | Doc 49 multi-channel serving; only relevant to multi-channel hosts. |
| R045 | Instance registry, fleet view | `fleet.py` | AIB | Kodo Monitor and fleet panes. |
| R046 | Worker child process per loaded model (crash containment) | DD02 `PresetWorker` | AIE | Doc 31 local co-worker: one worker process per loaded model, crash contained, single lazy-load lock. |
| R047 | Per-service venv, PYTHONPATH, no editable installs | `VENV_AND_IMPORTS.md` | REJ | Python mechanism; .NET has assemblies and the module graph. |
| R048 | Install paths resolver (walk to `releases` ancestor) | `common/install_paths.py` | AIB | Doc 49 and 03 install layout; no change. |
| R049 | Startup registrar providers (Task Scheduler, startup folder, systemd user, launchd) | DD11 | AIE | Doc 03 service host plus doc 28 and 30: add a per-user "start at sign-in" provider family; Windows registers as handler, never claims a silent default. |

### 2.3 Updates, channels, proving grounds, dependencies

| Id | Resibo item | Source | Disposition | Target and change |
|---|---|---|---|---|
| R050 | Fresh clone per release into `<version>_<hash>` | DD24, MAINTENANCE | AAM | Slot module `Updates.SourceClone` (section 6.7); default stays signed slots. |
| R051 | Five channels LTSC, Stable, Beta, Alpha, Latest-Commit | DD24 | AIB | Same names in `VERSIONING.md` and doc 49 (reconcile naming drift per CONVERGENCE section 6). |
| R052 | Multi-channel standing state | DD24 | AIE | Doc 49: allowed for hosts serving several channels; default is one. |
| R053 | Release dir GC keeps current plus one | DD24 | AIB | Two slots always kept. Source-clone slot module keeps current plus one (R050). |
| R054 | Health-gated cutover | DD24 | AIB | Kodo readiness plus rings (doc 46 section 16.6). |
| R055 | Keymaster licence gating, short-lived clone token, fail open | DD24 | ROD | Stays a Resibo plug-in. Pattern kept: Ouroboros contract `IUpdateEntitlement` with fail-open semantics (question 5). |
| R056 | Proving Grounds: test a candidate dependency in isolation, gate promotion | DD36 | AAM | 71 `ProvingGrounds`. Isolation providers `.ScratchProcess`, `.Vm` (docs 16, 50) and `.Docker`, each a separate module. |
| R057 | Promotion bar for dependencies equals code releases | DD36 | AAM | 71: one gate function for both. |
| R058 | Supervisor self-update soak of 14 days | DD36, DD38 | AAM | 71 setting `ProvingGrounds:SentinelSoakDays` (tighten-only floor 14). |
| R059 | Shared downloader (multi-threaded, gated-model token) | DD36 | AAM | 71 `Candidate fetch`, reused by model downloads in doc 31 and the module manager. |
| R060 | Dependencies Warden, six fact kinds | DD28, DD37 | AAM | 71 `DependencyWatcher`. |
| R061 | Poller per fact kind; unreachable source never fails a pass | `pollers/` | AAM | 71: NuGet poller, GitHub-issue poller; one per fact kind. |
| R062 | PyPI release poller incl. pre-releases | `pypi_poller` | AAM | 71 `NuGetPoller` (stable and pre-release). Doc 42 canary stays the consumer. |
| R063 | Free-threading classifier poller | `free_threading_poller` | AAM | 71 adapts the idea to .NET: fact kind `platform_support` (new TFM, NativeAOT and trim compatibility). |
| R064 | Upstream issue state poller | `github_issue_poller` | AAM | 71 `GitHubIssuePoller`. |
| R065 | `model_currency` fact | DD28 | AAM | 71 fact kind for the model catalogue and provider list drift (doc 05, 05b). |
| R066 | `dataset_freshness` fact (AV definitions) | DD28 | AAM | 71 fact kind used by 73 for signature freshness and by the geo/maps data any module ships. |
| R067 | `regulatory_value` fact (thresholds) | DD28, DD31 | ROD | BIR thresholds are domain. Fact kind "external reference value" stays generic in 71 for provider pricing and rate limits. |
| R068 | First observation is not a change; unobserved never reported | `poller` rules | AAM | 71 rules, with tests. |
| R069 | `ChangelogWriter`: one checked-in changelog is the only store | `changelog_surface.py` | AAM | 71 writes `docs/DEPENDENCY-CHANGELOG.md` (agent sessions read checked-in files). |
| R070 | Dependabot limited to actions; the rest via Warden plus Proving Grounds | PHASE_1_COMPLETE | AIE | Doc 42 and Renovate policy: 71 decides which ecosystems each tool owns. |
| R071 | Tracked inventory registered by every new dependency | `new_dependency` template | AAM | 78 check `check_new_dependency` against 71 inventory. |

### 2.4 Telemetry, diagnostics, issue filing

| Id | Resibo item | Source | Disposition | Target and change |
|---|---|---|---|---|
| R072 | Telemetrees (monitoring plus diagnostics) | DD28 | AAM | Split as CONVERGENCE section 4: 70 (issue filing) and 71 (watching). |
| R073 | Diagnostics detector with fingerprint per signal | `diagnostics/detector.py` | AAM | 70: fingerprint contract and detector set; signals from Kodo, rollout failures, watcher findings. |
| R074 | Append-only filed-issue ledger, no duplicate fingerprint | `FiledIssueLedger` | AAM | 70 `IIssueLedger` (Postgres provider in Bazaar). Fix Resibo's known gap: reopen or comment when a closed issue recurs. |
| R075 | GitHub App filing (JWT then installation token), never raises | `github_app_auth.py` | AIB | `Bazaar.GitHub` Apps (org `codex-alchemist-dev`); 70 sink uses it. |
| R076 | Live issue status client, never cached | `GitHubIssueStatusClient` | AAM | 70 status read with `checked_at`. |
| R077 | Opt-in flag, default off, unconfigured installs make no calls | DD28 | AIB | Doc 43 consent off, ask, auto; keep "unconfigured means zero network". |
| R078 | Redaction stated as rule but no code | `telemetrees` | AIE | Doc 43: pluggable redaction rule set (CONVERGENCE A6); Resibo's miss is the lesson, so redaction gets a test that fails the build if a sink is reachable without it. |
| R079 | Filed-issues screen | `filed_issues_screen.py` | AIE | 83 pane over 70 ledger. |
| R080 | Timer to invoke detection (never wired) | `DetectAndFileIssues` | AAM | 70 runs on a Ryusei trigger (R126); never manual-only. |
| R081 | Watchdog as the one wired signal | `detector.py` | AAM | 70 first signal: silent module from 75. |

### 2.5 Identity, access, privacy

| Id | Resibo item | Source | Disposition | Target and change |
|---|---|---|---|---|
| R082 | Passwordless only, lint-enforced | DD05 | AIE | Section 5 item 7; 78 check `no_password_fields`. |
| R083 | Auth method registry (SSO, passkey, email OTP, SMS OTP) | DD05 | AIB | Doc 14 and 32 passkeys and step-up; OTP methods as provider modules if wanted. |
| R084 | Composable 2FA, `TwoFactorPolicy` three levels | DD05 | AIE | Doc 32 and 48: policy levels Optional, ElevatedOnly, All; floors tighten only. |
| R085 | `public_facing` raises sign-in floors, cannot be lowered | DD05, DD43 | AIE | Section 5 item 6 (docs 32, 39). |
| R086 | Server-side sessions, revoke on role change | DD05 | AIE | Doc 48 section on device sessions: role snapshot at creation, so a role change revokes all sessions (section 5 item 20). |
| R087 | Session TTL tighter when public | DD05 | AIE | Doc 32: shorter default TTL when `public_facing`. |
| R088 | Roles owner, staff, client | DD05 | AIB | Custom roles (doc 48). No new fixed roles. |
| R089 | Break-glass: time-boxed, reason-tagged, dual notification | DD05 | AAM | 72 `Access` (grants). Closes the open "request access" flow for DMs and Vault. |
| R090 | Break-glass sweep job | DD12 | AAM | 72 expiry sweep on a job class (82). |
| R091 | Groups: persistent structural access, manager flag | DD41 | AAM | 72 `Groups`. |
| R092 | `get_effective_group` single chokepoint stamp | DD41 | AAM | 72 stamp at the GC write path. |
| R093 | Permission re-checked at query time, never cached | DD21, DD41 | AIE | Section 5 item 16 (docs 15, 48). |
| R094 | Single-target vs whole-group checks kept separate | DD21 | AAM | 72 contract has two methods. |
| R095 | `is_group_manager` toggle is its own audit entry | DD41 | AAM | 72 and 81 vocabulary. |
| R096 | Tenancy mode single or multi | DD05 | ROD | Hosted multi-tenant. Slot in 6.3; the shared pattern is "single code path, multi adds structure". |
| R097 | Account Guardian: sessions, recovery, export, deletion, consent | DD06 | AAM | 79 `AccountGuardian`, over doc 48 erasure. |
| R098 | Staff-mediated recovery queue with checklist | DD06 | AAM | 79; owner approval is a signed approval (doc 48). |
| R099 | Deletion state machine with grace and billing hold | DD06 | AAM | 79 (grace default is an operator setting, not Resibo's 30 days). |
| R100 | Versioned consent records, re-consent flag | DD06 | AAM | 79; links doc 43 telemetry consent. |
| R101 | DSR export through the export framework | DD06, DD31 | AAM | 79 uses 76 `Export` provider. |
| R102 | Structural agent-token ceiling (owner unreachable) | DD55 | AAM | 80 `Mcp`: tokens never above the issuing human's role and never owner. |
| R103 | Content-security file checks | DD27 | AAM | 73 `Screening`. |

### 2.6 Storage, backup, search, export

| Id | Resibo item | Source | Disposition | Target and change |
|---|---|---|---|---|
| R104 | Per-user SQLite, WAL, FTS5 | DD13 | ROD | Resibo storage; Bazaar is Postgres. Slot in 6.3. |
| R105 | Historian data-change track in same transaction | DD29 | AIB | Audit in the same transaction (doc 48); GC `gc_message_version`. |
| R106 | Historian narrative track, quantized and displayable | DD29 | AAM | 76 `Bazaar.Server.Narrative`: one summary line per stage, no raw content. |
| R107 | Narrative emission chokepoint (stage wrapper) | `run_stage` | AAM | 82 runs module emits to 76 Narrative from its stage wrapper. |
| R108 | Historian git mirror | `github_mirror.py` | AIB | Vault is git (doc 18). |
| R109 | Blob store sharded, no embedded metadata | DD13 | AIB | `Bazaar.Server.Blobs`. |
| R110 | Parallel backup targets, local first | DD13 | AIE | Doc 33: allow several offsite targets at once; "one of N is durable, all is synced". |
| R111 | Disaster recovery: blobs first, then database, then verify physical hash | DD33 | AIE | Doc 33 restore wizard order and `VerificationReport` with three separate lists. |
| R112 | Weekly sampled integrity job, proportional response | DD33 | AIE | Doc 33 and Kodo restore tests: sample, log at attention level, never auto-restore. |
| R113 | Archive Sync one-way mirror, cursor, notify-then-pause | DD32 | AIE | Doc 33 and 44: external mirror option; pause and ask when the target vanishes. |
| R114 | Client-side encrypted config backup, passphrase | DD11 | AIB | Sealed recovery bundle (docs 33, 14). |
| R115 | Reimport three-way merge | DD30 | AAM | 76 `Reimport`; extends doc 44. |
| R116 | Snapshot reference embedded in exports | DD30 | AAM | 76: every export carries a base reference; a missing one is rejected, never overwritten. |
| R117 | Export Framework provider set | DD31 | AAM | 76 `Export` (docx, md, Milanote, Miro, Trello providers stay in doc 44). |
| R118 | Excel with live formulas, SLSP, BIR DAT, QuickBooks IIF, Xero CSV | DD31 | ROD | Receipt and accounting formats. Pattern kept: matched export and re-import pair. |
| R119 | Outbound zip gets the same bomb checks as inbound | DD31 | AAM | 73 symmetric check on exports. |
| R120 | Search with bound FTS parameters and a permission gate | DD21 | AIB | Search provider (Postgres FTS, `pg_search`); add injection test (78). |
| R121 | Aggregate queries beside search | DD21 | ROD | Receipt reporting. |
| R122 | Migration API chained N to N+1 | DD23 | AIB | Per-component SQL migrations (doc 04). Settings schema versioning added to the registry (section 5 item 5). |
| R123 | Migrations are ordinary logged writes, no separate rollback | DD23 | AIE | Doc 04: data migrations of Vault and settings record an audit entry. |
| R124 | Additive-only wire fields, compatibility check in CI | DD23, `check_grpc_compat` | AIE | Section 5 item 19 (doc 02, 78 check). |
| R125 | Integer minor units for money | corrections list | AIE | Doc 23 usage costs use integer micro-units. |

### 2.7 Jobs, scheduling, pipeline

| Id | Resibo item | Source | Disposition | Target and change |
|---|---|---|---|---|
| R126 | Task Scheduler: allowlisted actions, cron, trigger providers | DD39 | AIB | RyuseiFuda closed-set triggers (doc 38). Add: missed runs wait for the next occurrence; schedule builder pane (83). |
| R127 | Trigger providers (in-app, wake, OS-native) | DD39 | AAM | 75 `SupervisorWakeTrigger` equivalent for sleeping modules. |
| R128 | Background Workers: job classes, idle gating | DD12 | AAM | 82 `Jobs.Classes` over the `job` table. |
| R129 | Consecutive-failure guard (five fails disable plus attention log) | DD12 | AAM | 82 (the retry-cap gap Resibo left is closed: cap and dead-letter). |
| R130 | Consolidated job registry with completeness test | DD12 | AAM | 82 plus test: every job declared in its module manifest. |
| R131 | Idle detection from the run state | `idle_detection.py` | AAM | 82 idle means no open run. |
| R132 | Execution Core run model (open, closing, paused) | DD10 | AAM | 82 `Runs`: generic run state machine. |
| R133 | Run scheduler: per-user and global semaphores | `scheduler.py` | AAM | 82. |
| R134 | Debounce with max-wait coalescer | DD10 | AAM | 82 (used for GC bursts, reindex, ingest). |
| R135 | Per-stage checkpoint, skip on resume | `run_stage` | AAM | 82 (Resibo's checkpoints are memory only; Bazaar's are durable). |
| R136 | Bounded retry with one latched escalation flag | `retry_policy.py` | AAM | 82; section 5 item 14. |
| R137 | Per-item cancellation check, not configurable | DD10 | AAM | 82. |
| R138 | Config hot reload at each cycle | DD10 | AIE | Section 5 item 5. |
| R139 | Content-hash idempotency | DD04, DD10 | AAM | 82 idempotency key (hash of content, never mtime). |
| R140 | Webhook subscription manager (register new, then retire old) | DD35 | AIE | Doc 21 GitHub webhooks and any future push source: renew before expiry, never stop then start. |
| R141 | Circadian health from delivery rhythm; daily fallback poll | DD35 | AIE | Doc 21: poll safety net beside webhooks; separate from Kodo heartbeat. |
| R142 | Hybrid push/poll triggering | decisions | AIE | Same as R141. |
| R143 | Stage-output purge job | DD10 | AAM | 82 retention for checkpoint payloads. |

### 2.8 Health, watchdog, hardware, logs, audit

| Id | Resibo item | Source | Disposition | Target and change |
|---|---|---|---|---|
| R144 | Health status plus live diagnostic (wall, CPU, RSS classification) | DD20 | SLC | Kodo: soft-degradation classifier against live traffic. |
| R145 | Capability drift check (is the newer runtime actually in use) | DD20 | SLC | Kodo; section 5 item 8. |
| R146 | Resource ledger with TTL and heartbeat refresh | DD20 | AAM | 74 `ResourceLedger` (VRAM, NPU, GPU slots; a rejected reservation means fall back, not fail). |
| R147 | Watchdog: services kick, detector never restarts | DD34 | AAM | 75 `Watchdog`; detector and actor stay separate (section 5 item 21). |
| R148 | Version and commit ride the heartbeat | DD34 | AAM | 75. |
| R149 | Per-service timeout override map | DD34 | AAM | 75 (model load needs a long timeout). |
| R150 | Hardware detection (WMI, /proc, GPU class) | DD11 | AAM | 74 `Hardware` provider modules per OS. |
| R151 | VRAM source precedence, consent-gated report import | DD11 | AAM | 74. |
| R152 | Hardware scoring to recommended settings | `scoring.py` | AAM | 74; settles VM sizing and renderer defaults (OPEN items in doc 16 and 36). |
| R153 | Logs API: day-rotated JSONL, index, tail, full traceback | DD18 | SLC | Annales; `Bazaar.Logs` in 81. |
| R154 | Verbosity tiers and per-service override | DD18 | SLC | Annales. |
| R155 | `ATTENTION` level (below own baseline) | DD18, DD20 | AIE | Section 5 item 12 (doc 46, 81). |
| R156 | Level style hints, single source for rendering | `LEVEL_STYLE_HINT` | AIE | 83: one table in Rubedo themes. |
| R157 | Log retention by tier | DD18 | SLC | Annales. |
| R158 | Audit retention window, closed operation vocabulary | DD08 | SLC | Tabula (owner card). Section 5 item 13. |
| R159 | `set_authorizer` append-only enforcement | `audit/writer.py` | AIE | 81: database-level proof for the Postgres log (R014). |
| R160 | Audit gap: vocabulary missing supervisor operations | DD38 | AIE | 81: vocabulary registered by each module at install; unknown action is rejected in dev and recorded as `unregistered` in production, never silently dropped. |
| R161 | Four-way split: logs, data change, narrative, audit | DD29 | AIE | 81 documents the split. |
| R162 | Notifications inbox, reference not copy | DD09 | AIE | Doc 22 and 29 content-free push: notification carries a reference. |
| R163 | Outbound channels with retry then staff surface | DD09 | AIE | Doc 22: three retries over a day, then a visible Kodo item. |
| R164 | SMS providers (PH carriers) | DD09 | ROD | Carriers are domain; a generic SMS channel provider slot stays open in 22. |

### 2.9 Screening, tools, agents, support, status

| Id | Resibo item | Source | Disposition | Target and change |
|---|---|---|---|---|
| R165 | Real type by magic bytes, never extension | DD27 | AAM | 73. |
| R166 | Polyglot detection | DD27 | AAM | 73. |
| R167 | Two-pass container check, then each member | DD27 | AAM | 73. |
| R168 | Malware provider set; reject only on unanimous | DD27 | AAM | 73 provider modules (ClamAV, VirusTotal); disagreement means human review. |
| R169 | Selective archive remediation (remove one member) | DD27 | AAM | 73 using .NET `ZipArchive` (feature-detected). |
| R170 | Format normalization (codec, raster, HEIC) | DD42 | ROD | Receipt images. Pattern: hash original bytes before re-encode (R013). |
| R171 | Tool registry, categories, per-context enablement | DD07 | AAM | 80 `ToolRegistry`; categories ReadOnly, MutatingStaged, DevObservability, TestExecution. |
| R172 | Mutating tools only self-apply into a review gate | DD07 | AIB | Doc 15 approval tiers; 80 maps categories to tiers. |
| R173 | Agentic loop with `max_rounds` cap | DD07 | AIE | Doc 06 and 17: hitting the cap is a flagged failure, not silent truncation. |
| R174 | MCP server and headless CLI on the same core | DD55 | AAM | 80 `Mcp`; `Bazaar.Sdk` is the client library. |
| R175 | Human-issued capped tokens, audited, separate mutating rate cap | DD55 | AAM | 80 on `Bazaar.RateLimits`. |
| R176 | Test Orchestration runners (bench, crash, pipeline, env reset) | DD56 | AAM | 78 `Testing.Kit` runners exposed by 80 as TestExecution tools. |
| R177 | Synthetic-fixture warning | DD56 | AAM | 78 as a warning in the result, never a block. |
| R178 | Status page: separate minimal process, last-known cache | DD51 | AAM | 77 `Status` host process, independent of the server. |
| R179 | Coarse components, never internal names | DD51 | AAM | 77 mapping table plus a static no-leak test. |
| R180 | Staff-authored incidents only | DD51 | AAM | 77, joined to Kodo incident mode (doc 46). |
| R181 | 90-day uptime and subscriber email | DD51 | AAM | 77. |
| R182 | Support ticketing lifecycle | DD52 | AAM | 77 `Support`; ticket link as an optional reason on a grant (R089). |
| R183 | Auto-close resolved tickets after 14 idle days | DD52 | AAM | 77 operator setting, default 14. |
| R184 | Flag lifecycle (open, assigned, resolved, dismissed) | DD25 | AAM | 77 shared work-queue shape; dismissed counted apart from resolved. |
| R185 | Flag taxonomy and checks | DD25, DD17 | ROD | VAT, TIN, ATP are domain. |

### 2.10 Learning and registries

| Id | Resibo item | Source | Disposition | Target and change |
|---|---|---|---|---|
| R186 | Architect registry and Vendor Directory | DD26 | ROD | Domain registry. Pattern: R019. |
| R187 | Wikidata bootstrap | DD26 | ROD | PH vendors. |
| R188 | temporal_learning: LOCAL and GLOBAL layers, explicit share, moderation | DD40 | AIE | Soul Marketplace, Talent Pool and Genetic Memory Transfer (docs 07, 25): consent-gated promotion with prescreen then owner review; no standing auto-share. |
| R189 | Staff direct-to-global path with prescreen kept | DD40 | AIE | Same docs: owner-authored items skip human review, not the prescreen. |
| R190 | Curate: self-cleaning through the same review gate | DD40 | AIE | Doc 18 Vault curation: stale, duplicate and abandoned items staged, never auto-deleted. |
| R191 | Audit mirror of review decisions in git | DD40 | AIB | Vault git. |
| R192 | Corporation, Branch, Franchiser model | DD40 | ROD | PH franchises. |
| R193 | Category defaults | DD40 | ROD | |

### 2.11 Client, UI, onboarding, edge

| Id | Resibo item | Source | Disposition | Target and change |
|---|---|---|---|---|
| R194 | Menu data (`MenuItemSpec`, `former_paths`) | DD14 | AIE | Doc 41c: add `FormerPaths` so renamed commands and settings still resolve. |
| R195 | `find_setting` fuzzy search | DD14 | AIB | Command palette `FuzzyScorer`. |
| R196 | Enumerated custom screens | DD14 | AIE | R005. |
| R197 | Fleet and Updates screen | DD14 | AIB | Kodo and Modules panes. |
| R198 | Boot sequence screen with skip | DD38 | AIE | 83: Terminal and desktop boot view fed by Kodo start events; skippable. |
| R199 | i18n English plus Tagalog | DD14 | AIB | `Bazaar.Localisation`. |
| R200 | Theme config | `theme.py` | AIB | Single theme file (CLAUDE.md UI rule). |
| R201 | Credits screen for OSS notices | DD14 | AIE | 83 plus doc 47: generated from the dependency inventory (R071). |
| R202 | Codename banner art kept forever | MAINTENANCE | AIE | 83 optional: art per Z-to-A codename for LTSC. |
| R203 | React webapp, Vite, Zustand, TanStack | DD44 | REJ | Avalonia clients (CLAUDE.md). |
| R204 | Design system and tokens (shadcn) | DD45 | REJ | Rubedo tokens already one theme. |
| R205 | One generic DataTable | DD45 | AIE | 83: one generic data grid control reused by every list pane. |
| R206 | One DiffViewer shared by reimport and moderation | DD45, DD49 | AIE | 83 `DiffView`. |
| R207 | Dark mode as a token set | DD45 | AIB | Theme file. |
| R208 | WS reconnect: backoff 1 to 30 s, full resync, visible indicator | DD46 | AIE | Doc 02 sync and 27: visible "Reconnecting" state, full resync on connect (section 5 item 22). |
| R209 | Optimistic updates, never for access control | DD46 | AIE | 83 rule. |
| R210 | Offline mutation queue | DD46 | AIE | 83 cross-checks docs 41 and 49 (the contradiction noted in CONVERGENCE section 6). |
| R211 | Route guards are UX only; server is the boundary | DD47 | AIE | 83 rule: client gating never replaces server checks. |
| R212 | Multi-tab `BroadcastChannel` sign-out | DD47 | REJ | No browser client. Multiple windows share one process. |
| R213 | Receipt detail screen | DD48 | ROD | Pattern: "why did this field change" drill-down maps to GC message version history (83). |
| R214 | Reimport diff UI: bulk group with one confirmation, server-held partial session | DD49 | AIE | 76 and 83. |
| R215 | Guided tour: reduced motion, mute, server-held progress, no nag | DD53 | AIE | Doc 45 onboarding and 83. |
| R216 | Webapp Assistant on third-party hosted LLM | DD54 | REJ | Sends text to a third party; Bazaar's gateway and local runtime serve it. Pattern kept: offer help when fuzzy score is low (83). |
| R217 | Tunnel Exposure provider | DD43 | AIB | `Bazaar.Conduit.Cloudflared`; add: no automatic failover, owner told (section 5 item 6). |
| R218 | Gateway: single origin, no CORS, per-IP and per-session limits | DD19 | AIB | `Bazaar.RateLimits` and doc 51 admin panel. |
| R219 | Runtime config injection into the served bundle | DD19 | AIE | Doc 51: admin panel reads one injected config; no other web client. |

### 2.12 Testing, CI, installer, versioning, domain

| Id | Resibo item | Source | Disposition | Target and change |
|---|---|---|---|---|
| R220 | Bench suite on the real pipeline, per-test isolation, CRASHED outcome | TOOLKIT | AAM | 78 `Testing.Kit.Bench`. |
| R221 | Failure-injection mode | TOOLKIT | AAM | 78. |
| R222 | Interface walker from live menu data | TOOLKIT | AAM | 78 walker over Rubedo declarations, run headless. |
| R223 | Profiling tools (py-spy, Tachyon) | TOOLKIT | AIE | 78 maps to `dotnet-trace` and `dotnet-counters`. |
| R224 | Format-decoder fuzz pass | TOOLKIT | AAM | 78 for 73 decoders. |
| R225 | `pip-audit`, `bandit` | TOOLKIT | AIE | 78: `dotnet list package --vulnerable` and analyzers; CodeQL stays. |
| R226 | Playwright, `textual.testing` | TOOLKIT | AIB | `Avalonia.Headless` (CLAUDE.md). Playwright REJ (no web client). |
| R227 | Load harness for hot paths | TOOLKIT | AAM | 78. |
| R228 | Doubles-conform guards, pin non-findings | HANDOFF | AAM | 78; section 5 item 2. |
| R229 | Real data only for benchmarks, never in CI | PRE_STABLE_BENCH | AIE | Doc 34: gated bench set stays local and private. |
| R230 | Template `new_config_key` + `check_config_schema` | templates | AAM | 78: settings registry key needs default, reason, floor; CI check against registry. |
| R231 | Template `new_core_api` + `check_new_core_api` | templates | AAM | 78 `new_module`. |
| R232 | Template `new_provider` + `check_new_provider` | templates | AAM | 78 `new_provider`. |
| R233 | Template `new_sub_api` + `check_new_sub_api` | templates | AAM | 78 `new_sub_module`. |
| R234 | Template `new_dependency` + `check_new_dependency` | templates | AAM | 78 (R071). |
| R235 | Template `new_grpc_endpoint` + `check_grpc_compatibility` | templates | AAM | 78 `new_wire_message` plus compat test (R124). |
| R236 | Template `new_menu_item` + `check_menu_data_integrity` | templates | AAM | 78 (R222). |
| R237 | Template `new_taxonomy_type` + `check_no_shadow_taxonomy` | templates | AAM | 78 (R019). |
| R238 | `check_no_silent_except` | workflows | AAM | 78 analyzer: no empty `catch`. |
| R239 | `check_source_not_ignored` | workflows | AAM | 78. |
| R240 | `check_stripped_content_completeness` | workflows | AAM | 78 with R241. |
| R241 | Dev-content strip for end-user installs | DD11 | AIE | Doc 37: user packages omit docs, tests, CI. |
| R242 | `pr_checklist_enforcement` ("Verify PR checklist is genuinely complete") | workflows | AAM | 78 job; path-scoped checks are never required. |
| R243 | `release_installer` workflow | workflows | AIB | Doc 37 release pipeline. |
| R244 | `nox -s forward_compat` | `noxfile.py` | AIE | Canary build job (FORWARD-FACING); 78 names it. |
| R245 | Thin per-OS installer; first clone; idempotent; never self-moves | installer/ | AIE | Doc 37 and 45. |
| R246 | Setup wizard, 11 steps, branches by install type | DD11 | AIE | Doc 45: add the hardware step (74) and the exposure step. |
| R247 | `dev_mode` flag and `dev_fixtures` | DD11 | AAM | 78 fixtures; doc 45 headless dev default. |
| R248 | Unattended dev setup seeds a staff-scoped agent token | MAINTENANCE | AIE | 80: dev token is scoped, expiring, never owner. |
| R249 | `LocalConfigStore` (small JSON per API) | `common/` | AIB | Settings registry. |
| R250 | `local_config_store` hot reload | decisions | AIE | R138. |
| R251 | `frozen_dict.py` | `common/` | AIB | .NET `FrozenDictionary` (R011). |
| R252 | `grpc_limits.py` | `common/` | REJ | gRPC only; Bazaar has message limits in doc 02. |
| R253 | `watchdog_client.py` | `common/` | AAM | 75 client helper in `Bazaar.Watchdog.Abstractions`. |
| R254 | `blob_client.py` | `common/` | AIB | `IBlobStore`. |
| R255 | `version.py`, `xMM.mm.pp`, mineral codenames | `common/` | ROD | Scheme is Resibo's. Bazaar keeps SemVer (section 6.1). |
| R256 | API version `aMM.mm.pp` with target and current fields | PHASE_2_STATE | AIE | BAZAAR-COMPONENTS manifests: keep module SemVer; add `ContractVersion` for wire contracts (question 6). |
| R257 | Licence proprietary | LICENSE | ROD | Section 6.2. |
| R258 | Keymaster closed repo, instance id not hardware fingerprint | plan-02 | ROD | R055. |
| R259 | Billing, PSP interface, proration axes | DD22 | ROD | PH PSPs. Pattern: webhook signature verification mandatory (already in `Bazaar.GitHub`). |
| R260 | Accounting Sync, Fernet token store | DD50 | ROD | Pattern: per-user encrypted token store maps to Aludel secret scopes (already doc 14). |
| R261 | OCR engines, corroboration, `AgreementLevel` | DD01 | ROD | Pattern: multi-active corroboration (R003); cloud engines with per-run call budget map to R262. |
| R262 | Per-run call budget on paid providers | DD01, DD16 | AIE | Doc 05 gateway: per-run and per-agent call budgets on metered upstreams. |
| R263 | Preprocessing variants, process pool | DD03 | ROD | Pattern: process-class jobs pass blob references, not bytes (82). |
| R264 | Inference presets, constrained decoding, retry once on length | DD02 | AIE | Doc 31: schema-constrained output and one doubled-budget retry. |
| R265 | Execution-provider fallback chain, device pinning | DD01, DD02 | AAM | 74 plus doc 31. |
| R266 | Ingestion sources, scanner, Drive credentials | DD04 | ROD | Receipt intake. |
| R267 | Matching, gazetteer, corroboration policy | DD15 | ROD | |
| R268 | Geo/Address providers, rate limits, cache | DD16 | ROD | Pattern: per-provider token bucket (R262, `RateLimits`). |
| R269 | Reconciliation checks and propagation | DD17 | ROD | Pattern R010. |
| R270 | Review/Flagging taxonomy | DD25 | ROD | R184 keeps the shape. |
| R271 | Execution Core receipt stages | DD10 | ROD | R132 to R139 keep the generic parts. |
| R272 | Tests named for guarantees; no pytest-asyncio | HANDOFF | AIE | Doc 34 naming rule. |
| R273 | Hosted DOMTRI multi-tenant mode | README | ROD | Section 6.3. |
| R274 | Support box and assistant integration | DD52, DD54 | REJ | R216. |

## 3. How to read the ledger

- Every Resibo item in the inventories maps to exactly one row. Where the inventory lists a feature and a sub-feature, both have rows (for example Dependencies Warden R060 and each poller R061 to R066).
- AAM rows feed the wave plan in section 7. AIE rows feed the amendments in section 5 and the doc owners named in each row. SLC rows are the contracts the shared libraries (Kodo, Annales, Tabula) gain; providers stay in Bazaar, per the owner's library-shape rule.
- ROD rows are not ignored: each names the generic pattern that was extracted, so the domain part leaves and the idea stays.

## 4. Why the new modules are separate

Each AAM row is its own module with an Abstractions project, per CLAUDE.md. None goes in `Bazaar.Core`, `Bazaar.Server` or `Bazaar.Client.Core`. Where one idea has a generic half and a provider half (screening engines, pollers, sleep-wake activators, hardware probes, issue sinks), the provider is a separate plain module, so a deployment installs only what it uses. Modules cooperate through `SoftDependsOn` where one feature merely improves another (for example `Watchdog` softly informs `IssueFiler`), and through hard edges only where one cannot run without the other.

## 5. Small conventions adopted into existing docs

Each item names the exact doc the main session amends. The text is the proposed amendment, tagged PROPOSED until applied.

1. **Errors as data across the wire.** Doc `02-wire-protocol-and-sync.md`. Every response envelope carries `error` (stable code plus detail), and a batch response returns one entry per requested item even on failure, never dropping one. Authentication and session failures stay protocol-level status (Resibo's one exception).
2. **Doubles-conform guards.** Doc `34-testing-strategy.md`. Each fake or stub in `Bazaar.Testing` has a contract test that also runs against the real implementation, so a fake cannot drift. Non-findings that were investigated are pinned by a test with a comment.
3. **Immutable contracts.** Doc `BAZAAR-COMPONENTS.md` section 4 and the code conventions in `CLAUDE.md`. Contract types are records with `init` members; collection members are `FrozenDictionary`, `ImmutableArray` or `IReadOnly*`; constant tables are frozen. An architecture test fails on a public mutable collection in an Abstractions project.
4. **Logical versus physical blob ids.** Doc `02-wire-protocol-and-sync.md` (blob surface) and `Bazaar.Server.Blobs.Abstractions`. The logical id is the SHA-256 of the original bytes and never changes; a mapping row holds the physical hash and codec of the stored copy. Restore verification (doc 33) checks the physical hash and separately checks every logical id is mapped.
5. **Config hot reload through settings events.** Doc `03-server-host-and-providers.md` (settings registry). The registry raises a typed `SettingChanged(Principal, key, old, new)` event; modules that can apply a change live subscribe, others declare "needs restart". Each key carries a schema version and a floor.
6. **`public_facing` raises sign-in floors.** Docs `32-remote-access-auth-and-privacy.md` and `39-conduit-remote-connectivity.md`. A Conduit provider that exposes the server sets `public_facing`; while set, step-up and two-factor floors rise and cannot be lowered, and session TTL shortens. No automatic provider failover: Kodo tells the operator and the operator switches.
7. **Passwordless-only lint.** Docs `48-gap-decisions-security-identity.md` and `14-identity-service-and-passport.md`. Sign-in has no password field or hash; a 78 check fails the build if one appears. Recovery is a signed approval, not a secret question.
8. **Forward-compat shim per capability, with a drift check.** `docs/FORWARD-FACING.md` and doc `46-kodo-health-module.md`. One shim file per capability; each shim registers a capability-drift probe with Kodo, which reports at start and on a timer, logging a clean result as a fact.
9. **Per-module guide file.** `BAZAAR-COMPONENTS.md` section 6 and `CLAUDE.md`. Each `src/Bazaar.<M>/` carries a short `CLAUDE.md`: scope, "does not own" list, depends on, test command. Worktree agents do not load the root file, so this is where they learn module rules. It must be self-contained.
10. **Test tree mirrors the source tree.** Doc `34-testing-strategy.md`. `tests/Bazaar.<M>.Tests` per module, plus named integration and bench projects. Test names state the guarantee.
11. **No hardcoded menus.** Docs `41-client-gui-customization-and-dsl.md` and `41c-rubedo-view-declarations.md`. Commands, settings screens and panes are declarations; a named, closed list of custom panes needs a stated reason; `FormerPaths` redirects renamed items.
12. **Attention log level.** Docs `46-kodo-health-module.md` and `81-program-logs-audit-vocabulary-and-retention.md` (once present). A level between warning and error for "running below its own baseline", rendered persistently.
13. **Closed audit vocabulary.** Doc `48-gap-decisions-security-identity.md`. Each module registers its operation names at install; the log rejects or flags unregistered actions.
14. **Bounded retry with one latched escalation.** Doc `38-ryusei-fuda.md` and `06-agent-runtime.md`. A stage retries up to a cap, then raises one flag; a flag already raised is not raised again.
15. **Fail closed in the contract.** Doc `15-security-taint-and-approvals.md`. An error, timeout or outage in a security check means "not verified", which is treated as unsafe; this is a property of the contract, not of each caller.
16. **Permissions are re-checked at use.** Docs `15-security-taint-and-approvals.md` and `48-gap-decisions-security-identity.md`. Grants and group membership are checked at the moment of access, never cached across a session.
17. **Allowlisted schedulable actions.** Doc `38-ryusei-fuda.md`. Already closed-set; state it as a rule for any user-defined schedule.
18. **Integer minor units for money.** Doc `23-observatory-and-usage.md`. Costs are stored as integer micro-units.
19. **Additive-only wire evolution.** Doc `02-wire-protocol-and-sync.md`. New fields only, never reuse or retype a field; a CI test compares the contract against the previous release (78).
20. **Role change revokes sessions.** Doc `48-gap-decisions-security-identity.md`. A session snapshots role at creation; any role change revokes that person's sessions.
21. **Detector and actor are separate.** Doc `46-kodo-health-module.md`. The code that notices a hung module never also restarts it.
22. **Reconnect behaviour.** Docs `02-wire-protocol-and-sync.md` and `27-client-architecture.md`. Exponential backoff from 1 s to 30 s, a visible non-blocking "Reconnecting" state, and a full-state resync on reconnect.
23. **Notify then pause.** Doc `33-backup-dr-and-export.md`. When an external target vanishes, tell the owner and stop; never silently continue or recreate.
24. **"Does not own" list in every module doc.** `_BRIEF.md`. Add a required section.
25. **Per-run call budget.** Doc `05-model-gateway.md`. A metered upstream has a per-run cap.

## 6. Conflicts and how modularity resolves them

Rule used: the owner's 2026-11-19 direction. Where two designs disagree, either both exist as separate modules, or one wins with a stated reason. Items already settled by DECIDED entries do not reopen; the second module is then a documented slot only.

### 6.1 Versioning

Bazaar: SemVer `0.MM.PP+commit`, Z-to-A codenames (alchemy and stars). Resibo: `xMM.mm.pp`, per-commit tick, mineral codenames, per-API versions.
Resolution: one wins for Bazaar and the shared libraries. Ouroboros's version logic takes an `IVersionScheme` contract; the Bazaar module `Versioning.SemVerCommit` is the default. A Resibo-style scheme would be a separate module owned by Resibo, not built here (owner card withdrew it). Borrowed: a wire `ContractVersion` apart from module version (R256).

### 6.2 Licence

Bazaar: AGPL-3.0 at the root, module exception, CLA planned. Resibo: proprietary.
Resolution: both coexist through the module exception. Bazaar code written from Resibo designs is AGPL; a module may carry any licence if it uses only the public module API. No Resibo code enters the repo (section 1.2).

### 6.3 Database engine and tenancy

Bazaar: Postgres, shared instance, per-person encrypted fields. Resibo: SQLite per user, FTS5, structural isolation, `tenancy_mode`.
Resolution: two modules. Default: Postgres (decided). Slot: `Aludel` multi-database already allows another provider (CONVERGENCE decision card), so `Data.SqlitePerUser` is a possible provider module plus `Tenancy.PerUserStore`, each optional and off by default. Neither is part of Bazaar's first build. What Bazaar takes regardless: the rule "structural isolation over permission checks" (R024).

### 6.4 Wire format

Bazaar: JSON via `IWireCodec` (DECIDED 2026-10-25), no Protobuf tooling. Resibo: gRPC plus protobuf.
Resolution: Bazaar wins and a gRPC module is not added to this repo. A gRPC codec would be one more `IWireCodec` implementation in a separate repository if a program needs it; adding it here needs the owner to lift the "no Protobuf tooling" rule (question 2). Conventions that survive any codec: errors as data (5.1), additive-only evolution (5.19).

### 6.5 Process model

Bazaar: modules in one process by default, with a documented out-of-process mode and Kodo Sentinel. Resibo: one OS process per API, always.
Resolution: both, as a deployment mode, not a fork. Core's out-of-process mode runs a chosen module in its own process behind the same abstractions over a local JSON transport. Kodo supervises either way. Crash containment (R046) is applied where it matters (local inference, decoders, screening scanners) and the rest stays in process. A "fully isolated" profile that sets every module out of process is a setting, PROPOSED, with a measured cost note before any default.

### 6.6 Provider selection

Bazaar's older text: a role has one selected implementation. Resibo: many providers active together. PR #919: providers are plain modules, no selector.
Resolution: Resibo's multi-active behaviour wins, expressed as an enumerable set of registered modules of a contract. Callers pick by capability or ask all. The BAZAAR-COMPONENTS 4 sentence is amended (R003).

### 6.7 Update model

Bazaar: signed A/B slots, rings, Velopack. Resibo: fresh clone per release, current plus one kept, health-gated cutover, licence-gated clone.
Resolution: two modules. Default `Bazaar.Updates` (signed slots). Slot `Updates.SourceClone` for self-hosters who build from source: clone a tag into a versioned directory, keep current plus one, use the same Kodo health gate. Both emit the same release events (channel, slot, release) so Kodo and Ouroboros do not care which one ran. Licence-gating is a plug-in point (`IUpdateEntitlement`, fail open) only (R055).

### 6.8 Other disagreements

| Topic | Bazaar | Resibo | Resolution |
|---|---|---|---|
| Language and clients | .NET, Avalonia | Python, React, Textual | Bazaar wins; Resibo's rebuild is .NET (owner, 2026-11-19). Share the client data-layer contract only (CONVERGENCE row 23). |
| Audit mechanism | hash chain, checkpoints, anchors | append-only table, authorizer | Tabula holds both through sinks; hash chain is default, DB-level denial added (R014, R159). |
| Logs | diagnostics ring buffer, redaction | day-rotated JSONL | Annales sinks: both; rotation and tiers from Resibo, redaction from Bazaar. |
| Sessions | device sessions, tokens, signed approvals | server session cookie | Bazaar for clients; cookie session only on the narrow admin and sign-in pages (round 26). |
| DM access | owner reads all, others request | break-glass grants, groups | Both: standing owner grant is a permanent grant in 72; others use time-boxed grants (R089). |
| Public status | read-only server page | independent process, staff incidents | Separate `Bazaar.Status` host; the server page stays as is (R178). |
| Dependencies | canary, Renovate, forward-facing | Warden plus Proving Grounds, pre-release day 0 | Complementary: 71 watches, canary and Proving Grounds test (R070). |
| Hosted assistant | privacy-first | Cloudflare Workers AI | Rejected (R216). |
| Docs | permanent | temporary | Bazaar wins (R031). |
| Build approach | agent waves, server runs clean builds | per-commit tick, hand sessions | Bazaar wins (R029). |
| Telemetry consent | off, ask, auto | opt-in only | Bazaar's three levels, default off (R077). |

## 7. Build plan: dependency-ordered waves across docs 70 to 83

Every doc 70 to 83 lands as a docs PR first (CLAUDE.md "docs first"). Then code waves below run in worktrees `agent/<doc>-<wp>`, Haiku by default, Sonnet for the marked security or concurrency items, and the main session merges in dependency order and runs the full build after each wave. About 300 small work packages, 20 concurrent at most, sized to the machine.

| Wave | Content | WPs | Needs | Sonnet items |
|---|---|---|---|---|
| W0 Core and conventions | Immutable-contract arch test; multi-active provider enumeration; `SettingChanged` event with `Principal`; settings schema versions; audit vocabulary registration seam; per-module guide template; `IVersionScheme` and `IUpdateEntitlement` contracts; doc amendments of section 5 | 14 | none | multi-active provider kernel change |
| W1 Pure libraries | 70 fingerprint and ledger contracts (6); 71 fact model and classifier (6); 72 grant and group contracts (5); 73 screening contracts and pure checks: magic bytes, polyglot, bomb ratio (7); 74 probe contracts and ledger TTL logic (6); 75 sleep policy and silence detector (5); 76 three-way diff and snapshot ref (6); 77 ticket and incident state machines (4); 78 fakes, doubles guards, bench core, template checks (8); 79 deletion and consent state machines (4); 80 tool categories and token ceiling (5); 81 log record, rotation, tiers, retention, vocabulary (8); 82 job classes, coalescer, retry latch, checkpoint contract (7); 83 data grid and diff view view-models (4) | 81 | W0 | 72 grants, 73 checks, 75 detector, 82 checkpoint |
| W2 Modules and providers | 70 Postgres ledger, GitHub sink, detector wiring (5); 71 NuGet and GitHub-issue pollers, changelog writer, fetch (6); 72 Postgres stores, expiry sweep, GC stamp (6); 73 ClamAV and cloud providers, archive pass, export symmetry (6); 74 Windows and Linux probes, report import, scoring (6); 75 Watchdog service, systemd and Windows activators, wake table (6); 76 export providers and reimport service (5); 77 status host and cache, support store (4); 78 bench runners, walker, fuzz, load, CI checks (8); 79 recovery queue, export and erasure wiring (5); 80 registry service, MCP server, CLI (6); 81 Logs module, Tabula retention and vocabulary (6); 82 Runs service, job registry, scheduler wiring (6); 83 pane modules, reconnect state, boot view (6) | 81 | W1 | 72 stores, 73 providers, 75 activators, 79 recovery, 80 tokens |
| W3 Integration | 70 recurrence handling and Kodo signals (3); 71 Proving Grounds gate with VM isolation and update-policy hookup (4); 72 DM and Vault request flow (4); 73 mail and upload hooks (4); 74 VM sizing and renderer defaults settle (3); 75 Kodo hookup and sleep classification of modules (4); 76 doc 44 importers (5); 77 incident link to Kodo, mail subscribers (6); 78 PR checklist job and baselines (5); 79 deletion with erasure and backup (6); 80 approval tiers and rate caps (5); 81 module vocabulary registration (3); 82 migrate existing jobs to classes (5); 83 tour, credits, schedule builder (6) | 63 | W2 | 79 deletion, 80 approvals, 82 migration |
| W4 Clients | Kodo Monitor additions, Access, Support, Status admin, Hardware, Logs and Issues panes in Windows, Linux, Android, Terminal heads; headless tests per pane | 40 | W3 | none |
| W5 Hardening | Architecture tests for every new module; seeded-violation tests; full `./build.sh`; Proving Grounds dry run on a real dependency bump; doc status updates; Sonnet review of 72, 73, 79, 80 | 20 | W4 | review of 72, 73, 79, 80 |

Dependency spine inside waves: 81 (vocabulary) and 82 (job classes) before everything that registers actions or jobs; 74 and 75 before 71 isolation and 70 signals; 72 before 77 (ticket link) and 79; 73 before 76 (export symmetry) and before mail hooks; 80 after 72 and 81; 83 last in each wave. Work packages that cannot finish alone go back to the main session as a question.

## 8. Owner questions

1. Resibo's repo is proprietary. Is the DOMTRI Foundation the same owner, and is copying code from it allowed, or are designs only?
2. gRPC: keep "no Protobuf tooling" absolute for this repo, or allow a separate gRPC `IWireCodec` module outside it?
3. Build the optional slot modules (`Data.SqlitePerUser`, `Tenancy.PerUserStore`, `Updates.SourceClone`, a Resibo version scheme) now, or only document the slots?
4. Out-of-process mode: which modules ship isolated by default (local inference, screening scanners, decoders), if any?
5. Add `IUpdateEntitlement` (fail-open licence hook) to Ouroboros contracts so Resibo can plug Keymaster in?
6. Add a wire `ContractVersion` beside module SemVer?
7. Are break-glass grants (72) to be the answer to the open DM and Vault "request access" flow?

## 9. Name check after the sibling docs landed

Checked at the end of writing. Present and matched: 70, 71, 72, 73, 74, 75, 76, 77, 79 (names in section 1.3 come from them). Not yet present: 78, 80, 81, 82, 83, so their names are plan names. The rows say "doc 70" and so on; where a doc names a module differently from a short name in a row, the doc's name wins. Doc 76 also holds `Narrative` (R106), and doc 71 also offers a `PyPI` poller and an `Osv` poller, which this ledger did not need but does not conflict with.
