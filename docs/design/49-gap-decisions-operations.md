# 49. Operations and governance gap decisions

Status: DECIDED (owner, 2026-11-13) for every decision in this doc. The mechanisms under each decision are PLANNED: design intent, nothing built yet. Items tagged OPEN are the only undecided points. Build state: none of this is in code.
Depends on: 01-gc-data-model.md (GC log), 02-wire-protocol-and-sync.md, 03-server-host-and-providers.md, 04-postgres-schema-and-migrations.md, 12-kanban-and-dependencies.md, 18-vault.md, 19-constellation-and-git-solar-system.md, 27-client-architecture.md, 30-linux-client-and-packaging.md, 33-backup-dr-and-export.md, 34-testing-strategy.md, 37-distribution-matrix-and-packaging.md, DECISIONS-LOG.md
Used by: every doc that ships code or runs at a customer site. Companion docs in the owner brief numbering: 39 (Conduit), 41 (client mnemonics and keyboard model), 42 (module API), 46 (Kodo), 47 (licence and pricing), 48 (security and identity). Those files land in their own PRs; links to them are by number and name.

## 0. How to read this doc

- Scope: the operations and governance gaps the owner closed on 2026-11-13. Security and identity gaps (module trust, biometrics, secrets, audit log, owner recovery, AI agent safety, retention) are in doc 48 and are not repeated here.
- Each topic gives: the decision (owner words, normalised), its status, the design consequences, cross-links, and the build state.
- Names follow the owner's brief exactly: Bazaar, Codex Alchemist, Kodo (code name Bazaar.Kodo), Conduit (Bazaar.Conduit), Constellation (Bazaar.Constellation, with Server.Constellation on the server side), RyuseiFuda, Calcinato, Athanor, GraphDRAG, RUBEDO, Orrery, Workday.
- Where a detail was not decided, this doc applies the configurability rule (DECISIONS-LOG round 13): the Bazaar.Server operator gets a setting, with a safe shipped default, a documented floor where a protection exists, and audited changes. Such settings are named "operator setting" below.
- Conflicts with older docs are listed in section 12. They are not silently resolved.

## 1. Scale: one large node first, splittable later

**Decision (DECIDED).** A large company runs on a single node. The design must let the node be split into several nodes later without a rewrite.

**Status.** DECIDED for the target and the splittable shape. Sizing numbers are PLANNED and come from spike S8 (agent VM sizing) and the benchmark runs in doc 35.

**Design consequences.**

- Single node means one Bazaar.Server.Host process group on one machine (Windows first, Linux second, per doc 03), with PostgreSQL, the cache and presence service (Garnet on Windows, Valkey on Linux) and the VM backend on that machine. No component may assume it is the only process in the world, but the default deployment has one of each.
- Split seams are fixed now, so a later split is a deployment change, not a code change. Each seam is a contract the single-node build already uses across a process or provider boundary:
  - the GC service (log writes, sequence assignment, fan-out),
  - the model gateway (doc 05),
  - the Vault writer and indexer (doc 18),
  - the Constellation layout service (Server.Constellation, doc 19),
  - Kodo (doc 46) and Conduit (doc 39),
  - the agent VM host pool (doc 16).
- Shared state lives in PostgreSQL. The GC log is partitioned by hash of GC identifier, with a partition count fixed at setup (doc 04). That count is the main scale knob. It is chosen at setup and is hard to change later, so the setup wizard shows it with a recommended value for the expected company size.
- GC fan-out uses PostgreSQL NOTIFY on the single node. A multi-node split replaces NOTIFY with a relay that keeps the same per-GC sequence order. Clients never see the difference because the wire protocol (doc 02) already replays by sequence number.
- Cache and presence (Valkey or Garnet) are replaceable behind the cache provider interface. Presence is ephemeral and is never a source of truth.
- Agent VMs are the main compute. Their count and size are bounded by the host's capacity. The operator setting `scale.max_active_vms` (default derived from the measured S8 result) keeps a single node from overcommitting. Work-life simulation (doc 09) and quotas (CLAUDE.md, "limits are quotas plus the Work-Life Simulation") shape demand before it reaches the node.
- Capacity is shown on the Kodo dashboard (doc 46) as a headroom signal: CPU, RAM, disk, VM slots, database size and partition growth. Scale-up advice is a Kodo output, not an automatic action.
- Benchmarks: target company sizes (agents, GCs, messages per day) are OPEN until the benchmark run. The operator setting exists now so nothing is hard-coded.

**Cross-links.** 03 (host and providers), 04 (partitioning and migrations), 16 (VMs), 35 (spikes S8 and benchmarks), 46 (Kodo capacity signals).

**Build state.** PLANNED.

## 2. Accessibility: keyboard, screen reader and internationalisation from day one

**Decision (DECIDED).** Keyboard, screen reader and i18n support are required from the first release. They are not a later phase.

**Status.** DECIDED. The client-wide mnemonic model is DECIDED in doc 41; this section records the accessibility consequences.

**Design consequences.**

Keyboard:

- Every feature is reachable from the keyboard. Each action has a mnemonic (doc 41: ngc1, nrfdrm, topgc, unread, settings and so on) and appears in the searchable command palette.
- The palette is opened by a mnemonic, not by a Ctrl chord. Users can remap any binding. Modules register and remap their own bindings, and a conflict is shown at registration time, never silently resolved.
- Focus order follows the layout: list, then detail, then composer. Focus is always visible. Foldable layouts (half-open at 600 dp, doc 37) keep the same focus order.
- Calcinato (the built-in terminal) is Vim-style and has no Ctrl chords. Local NPU suggestions in it are off by default.
- Athanor (the built-in IDE, Monaco) inherits Monaco's keyboard model and documents its exceptions. Android never uses Monaco (doc 36).

Screen reader:

- Every pane, control and message exposes a name, a role and a state through the platform accessibility layer: UI Automation on Windows, AT-SPI on Linux, and the Android accessibility services on Android.
- Avalonia's automation support is partial and varies by platform. The first build measures it per control type. Where a gap is found, the fix goes in `Bazaar.Client.Core` once, not per pane. OPEN: whether a dedicated spike is needed before the Avalonia decision is final. If so, it is added to doc 35 with its own pass criteria.
- Live regions announce new GC messages, mentions, approval requests and Kodo incidents. Announcements are rate-limited and can be turned off per category.
- Charts and the Constellation (doc 19, Vulkan with Skia fallback) carry a text alternative: a list view of the same nodes with the same actions. The graph is never the only way to reach information.
- Colour is never the only signal. The theme (one theme file drives Avalonia and Monaco) keeps contrast at WCAG 2.2 AA for text and controls, and the theme checker fails the build when it does not.
- Motion can be reduced, and text can be scaled to at least 200 percent without loss of function.

Internationalisation:

- Every user-facing string comes from resource files. No string is built by concatenation. Plurals and gender use the ICU message rules.
- Layout supports right-to-left scripts from the start. Mirror-sensitive icons and directional controls are marked.
- Dates, times and numbers use the viewer's locale. Each agent keeps its own timezone (doc 09). The viewer sees both the agent's local time and their own.
- Text inputs accept any Unicode, and search and the command palette normalise Unicode (NFC) and ignore case and diacritics for matching.

Testing:

- Avalonia.Headless tests (doc 34) check keyboard reachability of every new control and the presence of name, role and state.
- A CI check fails when a control has no accessible name.
- Each release is checked by hand with Narrator on Windows, Orca on Linux and TalkBack on Android before it reaches the early ring (section 6).

**Cross-links.** 27 (client keyboard model), 28, 29, 30, 36 (renderer and Monaco), 34 (testing), 41 (mnemonics, command palette, keyboard model), 19 (Constellation text alternative).

**Build state.** PLANNED. The mnemonic model is DECIDED in doc 41, and its build state is recorded there.

## 3. Supply chain: SBOM and signed releases

**Decision (DECIDED).** Every release ships a software bill of materials (SBOM) and is signed. Reproducible builds come later.

**Status.** DECIDED for SBOM and signed releases. The format choices below are PLANNED: this doc picks one answer for each, and the owner may override them in DECISIONS-LOG. Reproducible builds are DECIDED as a later goal, with no date.

**Design consequences.**

- SBOM: every artifact (server, clients, modules, packages from doc 37, the bundled PostgreSQL and pgvector) gets a CycloneDX 1.6 JSON SBOM. The SBOM lists every NuGet package with version and hash, the bundled native components, and the licence of each component as an SPDX identifier. An SPDX export is an operator option later, not a second store.
- Generation runs in CI from the locked package graph (central package management, lock files). A build that cannot produce its SBOM fails.
- Signing: each artifact gets a detached signature. The release signing key for first-party Bazaar releases is held offline by the owner and used in CI through a short-lived signing step. Per-company builds are signed on the company server with a key the server holds (doc 37, signing keys stay on the server). The signature covers the artifact and its SBOM.
- Provenance: each release carries an in-toto style provenance statement that records the source commit, the build workflow and the builder. The target for public releases is SLSA Build level 3. Level 3 is PLANNED, not a gate for beta.
- Verification: clients verify the signature on install and on update. Kodo verifies the server build at every start (immutability rule, doc 46). A failed check blocks the start and raises a Kodo incident.
- Reproducible builds: the .NET SDK is deterministic by default for the projects here. The later goal is that a third party can rebuild a release from the commit and get the same bytes. Work items: fixed timestamps (SOURCE_DATE_EPOCH), pinned tool versions in `global.json`, and no build-time network access. No date is set.
- Dependency checks: every release runs a vulnerability scan and a licence scan against the SBOM. A known critical vulnerability in a shipped component blocks promotion of the release past the canary ring (section 6). The licence scan feeds the licence check at module install (doc 47).
- Bundled third-party runtimes (PostgreSQL 18 and 19, pgvector) are dependencies in the sense of the brief. Their SBOM entries and signatures are checked the same way, and their versions follow the update policy (section 7).
- Module signatures use the same signing scheme. The module trust and verified-tag rules are in doc 48 and the marketplace doc.

**Cross-links.** 30 (signing and release pipeline), 33 (rollback), 34 (CI gates), 37 (packaging and signing), 46 (Kodo immutability), 47 (licence checks).

**Build state.** PLANNED. The release pipeline in doc 30 is the starting point.

## 4. Offline editing with merge

**Decision (DECIDED).** Clients can edit fully offline and merge their work when they reconnect. Every data class has a merge rule, given below.

**Status.** DECIDED for the direction and for each data class. The mechanisms are PLANNED.

**Design consequences.**

General:

- The client keeps a local encrypted queue of pending writes. Each write carries a client-generated identifier (idempotency key), the device identifier, the base version it was made against, and a hybrid logical clock (HLC) timestamp for display and for last-writer-wins keys.
- Replays are idempotent. Re-sending a queued write after a dropped connection never creates a second entry.
- Offline reads come from a local cache. The cache marks every item as "as of" a time, and the client shows an offline banner while disconnected.
- Server-held values (operator settings, company locks, agent memory, Passport and secrets) cannot be changed offline. Those controls are disabled offline with a reason.
- Merges are logged in the audit log (doc 48) with the device, the base version, the server version and the outcome.

Per data class:

1. **GC log (append-only, sequence-based merge).** The log is append-only, so a merge is an ordered append. Offline messages are queued with their client identifiers. On reconnect the server assigns the authoritative per-GC sequence numbers in arrival order, keeping each device's own order inside its batch. Replays of the same client identifier are dropped. Edits are new `gc_message_version` rows (doc 01), so two offline edits of one message both survive: the one with the higher server sequence is current, and the other is kept and marked as a concurrent edit. Nothing is lost and nothing is overwritten. Clients show "sent offline at" with the device time, while the order is the server order.
2. **Vault (git).** The Vault is a git repository (doc 18). An offline client keeps a local clone of the viewed branch (a ratified client-side exception) and commits locally. On reconnect the client pushes a branch, and the server merges it into the head with a three-way merge using the `diff3` conflict style. Non-overlapping hunks merge automatically. Overlapping hunks are never auto-resolved: the server stores the incoming side on a conflict branch named `conflict/<device>/<timestamp>` and opens a review item for the author and the owner. Wiki-link renames that break links are reported in the same review item.
3. **Kanban (field-level merge with transition checks, doc 12).** Each task field has a version. An offline edit names the base version of each field it changes. If the server value of a field is still the base version, the change applies. If it changed, the change is a conflict and goes to a review card for a human. Status changes are never last-writer-wins: after any merge, the transition rules run again, and derived Blocked markings are recomputed rather than merged. Dependency edges merge as a union, with a cycle check on the result. Removing an edge is a field change and follows the same conflict rule.
4. **Constellation layouts and node positions (last-writer-wins per key, cosmetic).** Positions are versioned per node, and the latest HLC wins. The previous position stays in history. Layouts are per-viewer or shared as the viewer chooses. Presence and cursors are ephemeral and never merged.
5. **User preferences (last-writer-wins per key with HLC).** Each key merges independently. Keys with a conflicting change and a safety effect (for example, notification rules) are flagged for review instead of applied.
6. **Attachments and uploads (content-addressed).** Identical bytes have the same hash, so merges are trivial. Metadata follows the rule for its owning class.
7. **Audit log (server-ordered).** Offline client actions are queued on the device with a device sequence. On sync they are appended on the server in arrival order and hashed into the chain. The audit log never gets a client-side order.
8. **Settings and policies (not offline-writable).** Covered in item "Server-held values" above.

Testing:

- Property tests generate random offline sessions and check that the merged result is the same for any arrival order of the same batches, where the data class says it must be (GC and audit log order is server-defined, so the test checks the server order is stable and gapless).
- Chaos tests drop connections mid-sync and check idempotency (doc 34 and Kodo chaos mode in a sandbox copy, doc 46).

**Cross-links.** 01 (GC log and versions), 02 (replay and sequence), 12 (Kanban fields and transitions), 18 (Vault git), 19 (layouts), 27 (client cache and queue), 33 (recovery), 48 (audit log).

**Build state.** PLANNED.

## 5. Conduit limits: built-in rate limits and authentication lockout

**Decision (DECIDED).** Conduit, the network path module (Cloudflared, P2P and LAN, doc 39), ships with built-in rate limits and authentication lockout. Operators can tighten them but not loosen them below a floor.

**Status.** DECIDED for the existence of the limits and the floor rule. Default numbers below are PLANNED and are operator settings.

**Design consequences.**

Rate limits:

- Limits are token buckets keyed by principal (human, agent, module, device), by client address, and by route class (sign-in, read, write, webhook ingress, streaming, file upload).
- Defaults are set per route class. Each limit returns `429` with a `Retry-After` header and an error code. Clients back off with jitter and show the reason.
- Rate limits are not quotas. Quotas and the Work-Life Simulation (CLAUDE.md) limit how much an agent works. Rate limits protect the server and the network. Outbound calls to model providers are rate-limited by the gateway (doc 05), not by Conduit.
- Webhook ingress (GitHub and mail providers, doc 21 and doc 22) has its own bucket, so one noisy source cannot starve others.
- Every limit event is counted. Repeated limit hits raise a Kodo alert (doc 46), and a sustained flood can trigger the containment step of the security section in doc 48.

Authentication lockout:

- Failed sign-in attempts are counted per account and per client address. After the threshold, the account is locked for a backoff period. The backoff doubles on each new lockout, up to a cap.
- Defaults: 5 failures inside 15 minutes starts a 15-minute lock, doubling per repeat up to 24 hours. Floors: the threshold may not be raised above 10 failures, the backoff must exist, and the cap may not exceed 24 hours.
- A lock never removes the owner's access to recovery. The owner recovery path is in doc 48, and recovery is never rate-limited into a permanent lock.
- Lockouts and unlocks are audited. The owner is notified of every lockout on an owner account, and a lock on any human account shows on the Kodo dashboard.
- Agent identities are not locked by human-style counters. Agent credential misuse is handled by the rules in doc 48.

Operator settings (all with defaults above):

- `conduit.rate.<route>.limit` and `conduit.rate.<route>.window`
- `conduit.lockout.threshold`, `conduit.lockout.window`, `conduit.lockout.base`, `conduit.lockout.cap`
- Changes are audited. A change that goes past a floor is refused.

**Cross-links.** 02 (heartbeats and backpressure), 05 (gateway limits), 21 and 22 (webhook sources), 32 (remote access and sign-in page), 39 (Conduit), 46 (Kodo alerts), 48 (credentials, recovery and containment).

**Build state.** PLANNED.

## 6. Release channels and staged rings

**Decision (DECIDED).** Bazaar ships stable, beta, nightly and LTS releases. Each release goes out in staged rings (canary, early, broad). The rings are tied to Kodo health signals. A bad release halts automatically. Update policies (section 7) can follow any channel.

**Status.** DECIDED for the channels, the rings, the health tie-in, the auto-halt and the policy link. The thresholds are PLANNED operator settings. One naming conflict is listed in section 12.

**Design consequences.**

Channels:

- **Stable.** Releases the owner has promoted from beta after the broad ring has been green for the soak period. Default channel for production installs.
- **Beta.** Pre-release builds in the `0.MM.PP+commit` form defined in `docs/VERSIONING.md`. Beta is where new features and dependency betas (PostgreSQL 19 before GA, day-0 support) are tested.
- **Nightly.** One build per day from the main branch, marked with its commit. Nightly is never a default for production and carries no compatibility promise.
- **LTS.** Long-term support releases on a fixed window. An LTS line receives security and critical fixes only, and it keeps the module API version it shipped with (section 8).
- A channel is a set of builds and a promise. An install is on one channel at a time. Switching channels is an audited decision and follows the same policy rules.

Rings (a rollout stage of one build within one channel):

- **Canary.** A small set of installs chosen by the owner, including the owner's own instance and Bazaar.CDAX (the pilot company, doc 37). Canary is the first place a build runs against real data.
- **Early.** A larger set of installs that opted in, configured per channel.
- **Broad.** Every install on the channel that the update policy allows.

Health tie-in (Kodo, doc 46):

- Each ring has a health gate. The gate reads Kodo signals: health score and status (green, amber, red), crash and error rates against the previous build, SLO burn, module health contract results (liveness, readiness, dependencies, self-test), failed migrations, failed signature checks, and failed self-checks (doc 46 scheduled self-checks).
- The gate's numbers are operator settings with safe defaults. The defaults are PLANNED. Floors: a gate may not be disabled for the broad ring of the stable channel.

Auto-halt:

- When a ring's gate goes red, promotion to the next ring stops at once. The build is marked `halted` in the release record, and no new update offers are made for it.
- Installs that already run the halted build and are in canary or early are rolled back automatically to the previous snapshot (section 7) when the operator setting `release.auto_rollback.rings` is on (default on). Installs in the broad ring are not rolled back automatically. They receive a manual-rollback offer and an alert.
- The owner and the roles with release authority are notified. The halt, the signals that caused it, and any rollback are audited.
- A halted build can be resumed only by an owner or delegated decision (section 7), with the reason recorded.

**Cross-links.** 30 (release pipeline), 33 (rollback), 37 (packaging), 46 (Kodo health signals and incident mode), 34 (verification gate), VERSIONING (`docs/VERSIONING.md`).

**Build state.** PLANNED.

## 7. Updates: policies, rollout, rollback and decisions

**Decision (DECIDED).** Security updates install automatically by default. Every other update is manual by default. Policies can be set per module, per category, per version type and per channel, with conditions and maintenance windows. Each update takes a snapshot first and rolls back automatically on failure. Rollout is canary and staged. Preflight and migration dry-run run before any change. An operator can pin, defer or skip. Approvals are by role. Authority can be delegated. Policies are code. A fleet dashboard shows every decision, and every decision is audited.

**Status.** DECIDED for each element. The policy schema and the defaults for conditions are PLANNED.

**Design consequences.**

Scope of policy:

- Dependencies are modules too. PostgreSQL, pgvector and runtimes are modules tagged "Deps" or "3rd party" (brief, DECIDED). PostgreSQL 18 and 19 are two versions of one module. Their updates follow the same policy as any other module. The bundled install is hidden and supervised, so no user runs an external installer.
- Dual-version support is required: the server supports the newest and the stable dependency versions at once (PostgreSQL 18 and 19). Day-0 support for dependency betas is tested in beta and nightly. The system never stays on stable only.
- The Bazaar server, the clients, Kodo, Conduit, the modules and the dependencies are all covered by one policy model.

Policy fields:

- **Scope.** Module (or "server", "client", "dependency"), category, version type and channel.
- **Category.** Security, critical fix, bug fix, feature, breaking, dependency, platform.
- **Version type.** Major, minor, patch, pre-release (the `0.MM.PP` scheme in `docs/VERSIONING.md`).
- **Action.** Automatic, manual, defer, skip, pin.
- **Conditions.** Kodo green on the install, no incident mode active, the release is past its soak time in the channel, the release has passed its ring gate, dependencies are satisfied, the licence check passed, and the module carries a verified tag or the install has accepted an unverified module with a warning (doc 48 and the marketplace doc).
- **Maintenance window.** A time window in the install's timezone, optionally per module. Automatic updates apply only inside a window unless the policy says a security update may apply at once.

Defaults (operator settings):

- Security category: automatic, inside the next maintenance window, with the safe conditions above.
- All other categories: manual.
- Major versions: manual, always.

Rollout and safety:

- **Snapshot.** Before any update, Kodo takes a snapshot: the server build is kept as an A/B version (doc 46), the database gets a consistent snapshot (doc 33), and the configuration is versioned.
- **Preflight.** Before a change, the system checks dependencies, the module API compatibility matrix (section 8), configuration validation (the dry-run from the onboarding design), disk and memory headroom, and the age of the last good backup.
- **Migration dry-run.** Each migration runs against a restored copy of the database in a scratch schema. The result reports runtime, lock time, rows touched, irreversible steps and any failure. The real migration does not start without a passing dry-run.
- **Apply and verify.** The update applies, then the module health contract and Kodo gates run. A failed gate triggers an automatic rollback to the snapshot and an incident record (doc 46).
- **Canary and staged rollout.** Updates follow the rings in section 6, so a module update can also go canary first.

Decisions:

- **Pin.** Hold a module or the server at the current version. A pin shows its owner and reason and expires only if the policy says so.
- **Defer.** Postpone an update to a date or by a number of days. Deferral cannot cross a security deadline set by policy.
- **Skip.** Decline one version. Later versions remain offered. A skip on a security update needs a role with security authority.
- **Role approvals.** Manual updates need approval from a custom role (roles are custom, gap decision). The approving role and the approval rule are part of the policy.
- **Delegation.** An owner can delegate approval authority to a role or a person for a time window and a scope. Delegations are shown in the dashboard, expire on their own, and are audited.
- **Policy-as-code.** Policies are declarative files, versioned in the company's config repository and validated on load. They use the same file format as the headless setup configuration (onboarding design). Changes to policy files go through the same approval rule as the updates they govern.

Fleet dashboard and audit:

- The fleet dashboard lists every install, module and version, channel, ring, policy in force, pending decisions, and the health gate state. It is a Kodo monitor view (doc 46), so it is in every client.
- Every decision (install, defer, skip, pin, approve, delegate, halt, resume, rollback, policy change) is audited with actor, role, time, policy version, reason and outcome. Audit mechanics are in doc 48.

**Cross-links.** 03 (host upgrade and rollback), 04 (migrations), 30 (server upgrade and rollback), 33 (snapshots and restore), 37 (packaging), 46 (Kodo snapshots, A/B, incidents, fleet view), 48 (roles, delegation and audit).

**Build state.** PLANNED.

## 8. Module API stability

**Decision (DECIDED).** The module API follows a semantic versioning contract. Compatibility shims, contract tests and a compatibility matrix in the module manager back it.

**Status.** DECIDED. The full API is in doc 42. The shim window and the deprecation notice period are PLANNED operator settings, with the defaults below.

**Design consequences.**

- The module API has its own version, separate from the Bazaar build version and from each module's version. The API version follows semantic versioning: a major bump for breaking changes, a minor bump for additive changes, a patch for fixes.
- Every module declares the API version range it supports in its manifest, together with its own module version (SemVer), its dependencies, its licence as an SPDX expression and its permissions.
- Each module carries its own version in metadata. The server build version does not define module versions.
- Deprecation: a removed member is marked deprecated in one minor release at least, and removed no earlier than the next major API version. The notice period is an operator-visible setting with a floor.
- Compatibility shims: when a major API version is released, the previous major is served by shims for one major cycle by default. A shim is tested by the same contract tests as the current major. Shims are removed only in the release that removes the old major.
- Contract tests: the platform ships a contract test suite for the API and for the module health contract (mandatory, doc 46: liveness, readiness, dependencies, self-test). Every module must pass the suite to be marked verified in the marketplace. A module that fails after an update is handled by the update policy (section 7).
- Compatibility matrix: the module manager shows, for every module version, which server builds, API versions and dependency versions (PostgreSQL 18 and 19, pgvector) it has passed. The matrix is generated from CI results and is shown before any update is offered.
- LTS lines (section 6) keep the API version they shipped with and receive only compatible fixes.
- Bazaar.Sdk and Bazaar.Contracts are part of the public contract. Their licence is AGPL-3.0 (owner, 2026-11-13; section 12 explains the older Apache-2.0 decision).

**Cross-links.** 02 (wire contracts and versioning), 20 (engine integration API), 27 (client modules), 42 (module API, owner brief numbering), 46 (health contract), 47 (marketplace licence), 48 (module permissions).

**Build state.** PLANNED.

## 9. Community and governance

**Decision (DECIDED).** Bazaar is owner-led. It has a public roadmap and design RFCs, and the owner keeps the final say. A security programme covers a security policy, private vulnerability reporting, advisories, and a bug bounty later.

**Status.** DECIDED for the model. The bug bounty timing, scope and amounts are OPEN. Succession and bus factor are OPEN.

**Design consequences.**

Governance:

- The owner is the final decision-maker on design, roadmap, releases and merges. Other contributors and agents propose; the owner decides. The owner's decisions are logged in DECISIONS-LOG and mirrored in the design docs' status tags.
- Each design doc records its status. Only DECIDED items are requirements. PROPOSED and OPEN items are not built on without an owner answer (CLAUDE.md).
- Public roadmap: `docs/ROADMAP.md` is the public roadmap. It lists milestones, their status and what is planned, with no private company material.
- Design RFCs: a proposal is a document with a fixed template (problem, proposal, alternatives, consequences, security impact, licence impact, open questions). Lifecycle: Proposed, Discussion, Owner decision (DECIDED, REJECTED or CHANGED). The decision is recorded in DECISIONS-LOG. The owner's decision is final.
- Code of conduct for contributors: PLANNED. The text is drawn from a common public template and approved by the owner before publication.

Contributions:

- DCO sign-off for modules and small fixes (decided).
- CLA for core code (decided). The CLA keeps the owner's option to relicense and to offer commercial licences, and it applies to extracted repositories too (section 10).
- The current `CONTRIBUTING.md` describes DCO only. Adding the CLA is a follow-up change to that file, not part of this doc (section 12).

Trademarks and names:

- A published trademark policy governs the use of Bazaar, CDAX, the key module names and the Codex Alchemist name. Registration of core marks follows clearance searches. "Codex" and "RUBEDO" need clearance first (brief, DECIDED).

Security programme:

- **Security policy.** A `SECURITY.md` file in the public repository explains how to report a vulnerability, which versions are supported, and the response targets. Response targets are operator-independent project settings: PLANNED defaults are acknowledgement within 3 business days and a first assessment within 10 business days.
- **Private vulnerability reporting.** The repository accepts private reports, and reports stay private until a fix or agreed disclosure date.
- **Advisories.** Confirmed vulnerabilities are published as security advisories with CVE identifiers where they apply, and the advisory names the fixed versions. Advisories link to the update policy so operators know which category their update falls in (section 7).
- **Bug bounty.** Later. OPEN: timing, scope, rewards and the platform used. No promise is made to researchers until the owner decides.
- Security design and identity topics are in doc 48.

Continuity:

- OPEN: succession and bus factor. The owner recovery design (doc 48) covers access, not governance succession.

**Cross-links.** CONTRIBUTING.md, SECURITY.md (to be added), `docs/ROADMAP.md`, `docs/pr-flow.md`, 47 (licence and commercial terms), 48 (security and identity), DECISIONS-LOG.md.

**Build state.** PLANNED. The DCO and the roadmap already exist; the CLA, SECURITY.md and the RFC template are PLANNED.

## 10. Extraction plan (DECIDED, not triggered)

**Decision (DECIDED).** Kodo (Bazaar.Kodo) and Constellation.Renderer stay normal first-party modules until the owner asks for extraction. When the owner asks, they are extracted in the steps below. Extraction is not triggered now, and no step is taken without the request.

**Status.** DECIDED as a plan. Not triggered. Nothing in this section is built or scheduled.

**Trigger.** The owner's explicit request, recorded in DECISIONS-LOG. Nothing else triggers it: not a release, not a size threshold, not an agent's suggestion.

**Ordered steps.**

1. **Record the request.** The owner's request is logged, with the module names and the target licence and repository name.
2. **Bazaar-free core with an adapter.** The module's core code is separated from Bazaar types. Bazaar-specific code moves into an adapter project inside Bazaar. The core builds and tests without a reference to any Bazaar project. The adapter implements the module API (section 8). Contract tests run against both.
3. **Split NuGet packages.** The core is split into packages: core, probes (health and metrics sources), storage, API (read API, streams and webhooks) and UI pane. Each package ships with docs and samples. Packages follow semantic versioning and the central package management rules.
4. **Own repository.** The module moves to its own repository with independent versioning, its own CI, its own SBOM and signed releases (section 3). The CLA and DCO rules (section 9) apply. The licence is AGPL-3.0 with the module exception and the commercial option described in doc 47, unless the owner decides otherwise at the time.
5. **Pinned dependency.** Bazaar consumes the extracted module as any dependency: it pins a version in the module manager, and the update policy governs upgrades (section 7). Modules carry their own version in metadata, so Bazaar shows the module's version, not the Bazaar build version.
6. **Verify.** The contract suite and the module health contract (section 8) pass in both repositories. The compatibility matrix shows the pinned version. Kodo continues to monitor the extracted module through its probes package.

**Cross-links.** 19 (Constellation renderer), 20 (engine integration), 27 (client modules), 37 (packaging), 42 (module API), 46 (Kodo), 47 (licence).

**Build state.** Not started. DECIDED as a plan only.

## 11. Summary of statuses

| Topic | Decision | Status | Build state |
|---|---|---|---|
| 1. Scale | One large node first, splittable later | DECIDED (target and seams); sizing OPEN until benchmark | PLANNED |
| 2. Accessibility | Keyboard, screen reader and i18n from day one | DECIDED (mechanisms PLANNED); Avalonia automation spike OPEN | PLANNED |
| 3. Supply chain | SBOM and signed releases; reproducible builds later | DECIDED; format and provenance choices PLANNED | PLANNED |
| 4. Offline merge | Full offline editing with merge, per data class | DECIDED; mechanisms PLANNED | PLANNED |
| 5. Conduit limits | Built-in rate limits and auth lockout | DECIDED; default numbers PLANNED | PLANNED |
| 6. Release channels | Stable, beta, nightly, LTS; canary, early, broad rings; Kodo-gated; auto-halt | DECIDED; naming conflict OPEN; thresholds PLANNED | PLANNED |
| 7. Updates | Security auto, rest manual; policies; snapshot and rollback; decisions audited | DECIDED; policy schema PLANNED | PLANNED |
| 8. Module API stability | SemVer contract, shims, contract tests, compatibility matrix | DECIDED; full design in doc 42 | PLANNED |
| 9. Community and governance | Owner-led, RFCs, security programme | DECIDED; bounty and succession OPEN | PLANNED |
| 10. Extraction plan | Ordered steps, trigger is owner request | DECIDED as plan; not triggered | Not started |

## 12. Conflicts and open points

These are recorded, not resolved here. Each needs an owner answer in DECISIONS-LOG.

1. **Release channel names.** `docs/VERSIONING.md` and CLAUDE.md name channels LTSC, Stable, Beta, Alpha and Latest-Commit. The owner decision of 2026-11-13 names stable, beta, nightly and LTS. OPEN: whether Alpha maps to nightly, whether Latest-Commit is the same as nightly, and whether LTSC and LTS are one channel. Until answered, this doc uses the 2026-11-13 names and `VERSIONING.md` is out of date on this point.
2. **Licence of Bazaar.Sdk and Bazaar.Contracts.** CLAUDE.md (2026-10-25) and the root NOTICE say Apache-2.0. The owner decision of 2026-11-13 says AGPL-3.0 everywhere and retires Apache-2.0. This doc follows the newer decision. The NOTICE file, the per-directory LICENSE files and the csproj licence fields need a follow-up change. That change is not part of this doc.
3. **Contributor licence.** `CONTRIBUTING.md` describes DCO only. The 2026-11-13 decision adds a CLA for core code. The file needs a follow-up change.
4. **Dependency update default for beta.** Whether dependency betas install automatically on beta-channel installs is OPEN. The default in this doc is manual, with day-0 test coverage in beta and nightly.
5. **Bug bounty.** Timing, scope, rewards and platform are OPEN (section 9).
6. **Succession and bus factor.** OPEN (section 9).
7. **Accessibility spike.** Whether Avalonia's automation support needs a dedicated spike before the framework decision is final (section 2). OPEN.

## 13. Cross-doc follow-ups (not edited here)

This doc creates no edits to existing docs. Follow-ups for the owner's next docs pass:

- `docs/design/README.md`: add doc 49 to the index.
- `docs/design/07-soul-system.md` and `docs/design/25-company-systems-knowledge.md`: no change from this doc. The memory and marketplace rules are handled in their own pass.
- `docs/design/30-linux-client-and-packaging.md` and `docs/design/37-distribution-matrix-and-packaging.md`: section 3 (supply chain) and section 6 (rings) should be referenced from their release sections.
- `docs/design/33-backup-dr-and-export.md`: the snapshot and rollback described in section 7 should be referenced from its rollback rule.
- `docs/design/18-vault.md` and `docs/design/12-kanban-and-dependencies.md`: section 4 (offline merge) should be referenced from their conflict sections.
- `docs/VERSIONING.md`: channel names (section 12, item 1).
- DECISIONS-LOG.md: one entry for the 2026-11-13 gap decisions, referencing this doc.
