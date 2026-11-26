# 46. Kodo: the health module (鼓動, heartbeat)

Status: PLANNED. Design intent only. Nothing in this document is built, and no code, migration, probe or pane exists yet. Owner decisions of 2026-11-13 are DECIDED; every detail below them is PROPOSED unless marked otherwise. Owner questions: 6 (section 23).
Depends on: 02-wire-protocol-and-sync.md (authenticated HTTP, `/ws` stream registry, `/status` format), 03-server-host-and-providers.md (`/health/live`, `/health/ready`, provider seam, settings registry, status listener bind floor), 04-postgres-schema-and-migrations.md (conventions, NOTIFY, migrations), 05-model-gateway.md (provider and lane health, quota states), 15-security-taint-and-approvals.md (approval tiers, taint, `audit_log` in its current form), 19-constellation-and-git-solar-system.md (renderer seam and server-side layout for the system map), 23-observatory-and-usage.md (telemetry pipeline, presence, usage), 33-backup-dr-and-export.md (backups, restore wizard, recovery bundle), 39-conduit-remote-connectivity.md (`IConduitProvider.ProbeAsync`, rate limits), 40-core-feature-modules-audit.md (component manifests, `SoftDependsOn`), 41-client-gui-customization-and-dsl.md (pane registry, mnemonics, keyboard-first rule).
Referenced here: 42 (module contract), 48 (audit log), 49 (update system). All three are in the same PR. See section 19.
Used by: 27, 28, 29, 30 (client Monitor panes), 34 (test strategy), 35 (spikes), 37 (packaging and signed releases).

## 1. Purpose

Kodo is the module that answers, at every moment and for every part of a Bazaar installation, "is it alive, is it healthy, what is going to break, and what do we do about it". It watches the server, every first-party and third-party module, the fleet of clients, the physical host, the network paths, the certificates and clocks, the backups, and the AI model endpoints. It scores all of that, predicts the failures it can predict, runs the recovery runbooks, and, when something is seriously wrong, puts the whole installation into incident mode.

Kodo is also meant to be the hardest module to damage. Its runtime is signed and verified on every start, it runs from a read-only slot, it recovers from crashes by design, and it is supervised by a process smaller than itself that does not depend on Bazaar code.

Scope of the name: "Kodo" (鼓動, heartbeat) is the module and the prose name. The module id is `kodo`, the project is `Bazaar.Kodo`, and the brief's form `Bazaar.KODO` is the same module. The owner's words for the goal, recorded as the design brief: "the most immutable app known to man" and "the most impressive named feature on the planet". This document takes the maximal option wherever the brief offers a choice, and says so where a cheaper option was considered and rejected.

## 2. Decided inputs (owner, 2026-11-13, DECIDED)

- Kodo is the Health API module. Its name is Kodo; its module id is `kodo` (brief: Bazaar.KODO).
- Immutability, failure handling, a monitor pane in every client, testing, prediction, scope, scoring, the API (including the mandatory module health contract), recovery and incident mode are all in scope, at maximal depth.
- Kodo is a normal first-party module for now. Extraction (a Bazaar-free core, split packages, its own repository) waits until the owner asks for it. The extraction rule is copied from the brief; doc 49 section 10 now confirms it (section 19).
- Deps are modules. Postgres and pgvector are modules Kodo monitors, not packages Kodo installs. Install is a Bazaar-only action with zero external installs for users.
- Telemetry and crash reports: on-device redaction is never model-dependent, and every server-side model explanation is labelled "unverified diagnosis" (see section 13).
- Configurability rule (DECISIONS-LOG round 13): every open value is an operator setting with a safe default. DECIDED items and security invariants are not settings. Settings may tighten, never loosen.
- Server build is one OS-agnostic .NET build. Anything OS-specific sits behind a provider interface (doc 03). Kodo follows the same rule for SMART, sensors, power, service state and Windows ACLs.
- Clients are Avalonia (`Bazaar.Client.Core` plus thin heads). The only browser-facing surface is the read-only status page, governed by section 15.5.
- Signing keys stay on the server. The server builds and signs packages, and clients update in place.
- Audit: hash chain, signed checkpoints, external anchoring and Merkle proofs. Records admin and security events and all data reads and writes by humans and agents.
- Role model: custom roles. Biometric step-up (face, voice, fingerprint, hardware keys) is required for sign-in, recovery and high-risk actions. Templates never leave the device.
- Scale: one node by default, splittable to several nodes later. Warm standby is optional (section 7.6).
- Accessibility: keyboard, screen reader and i18n from day one. Mnemonic navigation, no Ctrl chords.
- Release channels (stable, beta, nightly, LTS) and staged rings (canary, early, broad) tied to Kodo health signals, with a bad release auto-halting (later addition 1, 2026-11-13). Section 16.6.
- Owner-led governance with a public roadmap and design RFCs, and a security programme (policy, private vulnerability reporting, advisories, bounty later). Not a Kodo concern beyond the security advisory feed in section 16.1 (proposed channel).

## 3. What this document does not decide

- The general module contract (42), the audit log's storage and anchoring details (48) and the update policy engine (49). Kodo states what it needs from each and where its own design depends on them.
- The pane registry and mnemonic table (41). Kodo proposes mnemonics in section 14.4 and marks them PROPOSED.
- Backup formats and the restore wizard (33). Kodo drives scheduled restore tests through 33's interfaces.
- Remote access and step-up mechanics (32). Kodo names the step-up requirement only.

## 4. Identity and naming
> WORKING NAME, pending owner naming decision. The owner's name is Kodo (Bazaar.KODO). The project and component names in the table below (`Bazaar.Kodo.Abstractions`, `Bazaar.Kodo.Testing`, `Bazaar.Kodo.Sentinel`, `Bazaar.Client.Kodo`) are proposals, not owner-named.


| Item | Value |
|---|---|
| Module id | `kodo` (settings section `Bazaar:Kodo`) |
| Projects | `Bazaar.Kodo` (server module), `Bazaar.Kodo.Abstractions` (contracts other modules reference), `Bazaar.Kodo.Testing` (module health contract test base), `Bazaar.Kodo.Sentinel` (watchdog executable, see 5.2), `Bazaar.Client.Kodo` (Monitor pane, once the client module split of doc 40 step 7 lands) |
| Wire namespace | `Kodo` inside `Bazaar.Contracts` |
| Component kind | First-party module, `Deps` kind not used (Kodo is not a dependency) |
| Version | Plain SemVer with the commit appended (`0.1.0+a1b2c3d`), as for every module |
| Licence | AGPL-3.0 as first-party core code, with the module exception in the licence section of DECISIONS-LOG. Counsel review before release (section 19) |

## 5. Architecture

### 5.1 Components

```
                 +-----------------------------------------------+
   clients ----> |  Surfaces: read API, /ws stream, webhooks,    |
   (Monitor)     |  /metrics (Prometheus), OTLP export, status   |
                 +-------------------+---------------------------+
                                     |
 +-----------+   +-------------------v---------------+   +--------------------+
 | Probes    |-->| Evaluator: state machine, rules,  |-->| Responder: alerts, |
 | (bulkhead |   | hysteresis, SLO burn, scores      |   | runbooks, incident |
 |  pools)   |   +-------------------+---------------+   | mode               |
 +-----------+                       |                   +--------------------+
       ^                             v
       |                 +-----------------------+       +--------------------+
       |                 | Journal: hash chain,  |------>| Predictor:         |
       |                 | snapshots, time       |       | thresholds, trend, |
       |                 | series (Postgres +    |       | baselines, drift   |
       |                 | local spill file)     |       +--------------------+
       |                 +-----------------------+
       |                             |
 +-----+------------+        +-------v-----------+
 | Module health    |        | Immutable runtime |  verified on start, read-only,
 | contract (every  |        | (slot A or B)     |  signed manifest, tamper watch
 | module)          |        +-------------------+
 +------------------+                 ^
                                      |
                           +----------+----------+
                           | Sentinel (separate  |  supervises server host and
                           | process, no Bazaar  |  Kodo, verifies and rolls back
                           | dependencies)       |  slots
                           +---------------------+
```

Kodo's responsibilities are split so that no single component can silence the others:

- **Probes** run in bounded pools (section 7.3). They produce observations, never state.
- **Evaluator** turns observations into states, scores and events. It is deterministic given its inputs and its settings.
- **Journal** is the only writer of Kodo's history. Every state change and every observation that changed a value is appended, hash-chained, before any action is taken on it.
- **Predictor** reads the journal and writes predictions and drift events back into it. It cannot change state on its own (section 13).
- **Responder** executes runbooks and incident steps. It acts only through the approval gates of doc 15 and the freeze rules of section 16.
- **Surfaces** are read-mostly. Control actions go through the Responder, never directly.
- **Sentinel** is a second process that does not share code with Kodo beyond the verifier. It is the only component allowed to restart Kodo or the server host, and the only one that can switch an immutable slot.

### 5.2 Process layout
> WORKING NAME, pending owner naming decision: "Sentinel" for the watchdog process below.


| Process | Runs as | Purpose | Shares code with Bazaar |
|---|---|---|---|
| `Bazaar.Server.Host` | service | Company server (doc 03) | yes |
| Kodo module | in-process module of the host, own slot directory | All of section 5.1 | yes (contracts only) |
| `Bazaar.Kodo.Sentinel` | separate service, same account model as the host | Verifies Kodo's slot, restarts Kodo and the host, performs slot rollback, serves a tiny local status file | verifier only |

The sentinel is deliberate overkill. It is small enough to review in one sitting, has no network listener beyond a loopback status file, and is signed and A/B-versioned like Kodo. The host and Kodo watch each other (mutual supervision), and the sentinel watches both. If the sentinel is missing or fails its own check, the service manager's recovery actions (doc 03: Windows service recovery or systemd restart policy) are the last line.

### 5.3 Data directories

Kodo keeps three areas, each with a different ACL and lifetime:

| Area | Contents | Writable by | Lifetime |
|---|---|---|---|
| Runtime slot (A or B) | Kodo binaries, manifests, runbook definitions (signed), probe catalogue | Nothing at runtime (5.5) | Replaced by a signed update or rollback |
| Data (`kodo-data`) | Journal segments, snapshots, rollups, predictions, spill file | Kodo service account only | Retention settings (section 20) |
| Evidence (`kodo-evidence`) | Incident snapshots, log bundles, config and runtime hashes captured in incident mode | Kodo service account, read by role-gated export only | Incident retention, legal hold aware (doc 33) |

Runbook definitions are part of the runtime slot, not of data, so an operator cannot change what a runbook does without a signed update.

### 5.4 Server wiring choices (PROPOSED)

The pure core is wired into the server as `Bazaar.Server.Kodo` (module) and `Bazaar.Server.Kodo.Abstractions` (contracts other modules use). Neither references `Bazaar.Server`; the module references `Bazaar.Core`, `Bazaar.Contracts` and `Bazaar.Kodo`. The shape copies `Bazaar.Server.Health`: a `KodoServerComponent` (`kodo`, feature kind, `AddBazaarKodo` with TryAdd registrations) and `MapBazaarKodo(guard)`, where the host passes its HTTP surface guard (bearer, role, rate bucket). Neither Health nor Kodo is listed in `HostComponents` or mapped in `Program.cs` yet; the host registration is open for both. Every choice below is PROPOSED. Owner to ratify.

- **One engine, one loop.** `KodoMonitor` runs one pass: the probes that are due (by their section 9.1 cadence) through their pool bulkhead and their own breaker, then the journal, the evaluator, the score, the snapshot builder and the stream writer. `KodoMonitorService` calls it at start and on every tick of a `TimeProvider`-driven `PeriodicTimer` (default 5 seconds, floor 1 second). A pass throws only on the caller's cancellation. A probe error is a result, a journal error marks the journal not writable, and a publisher error is logged and dropped.
- **Probes wired:** `host.disk.free.data` (critical, reads the volume of `DataVolumePath`), `host.memory.pressure`, `kodo.self.journal_writable` (critical) and `kodo.self.spill`. The volume is named `data` on the wire, so no path leaves the process. `host.clock.drift` is built but off by default (`IncludeClockDrift`), because no time source is configured and it could only report unknown. Host reads use portable .NET APIs; a host registers its own `IHostReadings` for a better source. All other rows of 9.1 stay PLANNED.
- **Journal.** Records are hash-chained in memory (10000 kept, `JournalMemoryRecords`). With `DataDirectory` set they are also appended to a local spill file that survives restarts and verifies its chain on open. With a durable store registered (7.10) the local chain is replayed into it and `kodo.self.spill` reports the age of the oldest unsent record; with none, the spill holds no unsent backlog and the probe reads no spill (green). A record is written for the start, for each probe status change and for each displayed state change. Payloads are a redacted one-line summary (probe id, wire status, cause code).
- **Passive recovery (open question).** With no Responder, `failing` would stay failing forever because the evaluator only leaves it through `recovering`. When the underlying state is `failing` and a sample is all green, the monitor reports one running recovery to the evaluator, so the state goes `recovering`, then `healthy` after the stability window. A red sample in between returns it to `failing`. The Responder replaces this when it is wired.
- **Grace and unknown.** Section 8.2 gives no grace value: PROPOSED 2 minutes for non-critical red or amber (`NonCriticalGrace`). A non-critical unknown counts as amber. A critical unknown is `CriticalUnknown` immediately (no unknown budget yet). Category scores are the mean of probe scores (green 100, amber 70, unknown 50, red 0; a category with no probe scores 100), then the section 12.2 caps apply. No SLO input is fed yet.
- **Read API.** Under `/api/kodo/v1/`, each route through the host guard: `GET status` (the snapshot, 503 `not_ready` before the first pass), `GET events?limit=` (journal entries newest first, 1 to 200, default 50: sequence, time, kind, hashes, redacted summary, never payload bytes) and `GET journal/proof/{sequence}` (a Merkle inclusion proof over the retained record hashes, 404 `proof_unavailable` when the history no longer starts at record 0). The proof is by sequence number, not by a record id, in this slice. Errors use the section 15.1 shape (`code`, `message`, `retryable`, `correlationId`). Bodies carry probe ids, wire statuses, cause codes and hex hashes only. A switched-off module answers 404 `kodo_disabled`.
- **Stream on `/ws` (PROPOSED).** `IKodoStreamPublisher` in the Abstractions project receives the `state_changed` and `heartbeat` events with gap-free revisions from `KodoStreamWriter` (heartbeat every 10 seconds). The module now feeds the existing wire subscription engine the way the presence stream does: `KodoStreamGate` registers as an `IStreamGate` for the `kodo` stream (so Wire never references this module), and `KodoWireStreamPublisher` is the default publisher, pushing `kodo.event` frames to connections whose session lists `kodo`. No new transport. To send a snapshot on subscribe the engine gained one optional gate hook, `IStreamGate.OpenAsync`, which returns an opening event sent right after the subscribe response and again on every resubscribe (default: nothing, so presence is unchanged). The opening event is the latest snapshot, at the revision the monitor last numbered, so deltas after it either repeat an older revision (clients ignore them) or continue by one. A frame is the UTF-8 JSON of `KodoStreamEventDto` (envelope type `kodo.event`, stream `kodo`, no `n`). Delivery is best effort: a full queue drops the frame, the client sees the revision gap and subscribes again for a fresh snapshot. Access goes through `IKodoStreamAccess`; the default allows the owner only, and a host replaces it with the same `kodo.view.status` role check it gives `MapBazaarKodo` as its guard. With `Bazaar:Kodo:Enabled=false` no gate is registered, so the stream is refused like any unknown one. The client's `KodoStreamCodec` reads the server's own shape (`KodoStatusSnapshotDto` with its nested `status` object, and `KodoStreamEventDto`) and maps it to the flat `KodoSnapshotWire` and `KodoFrameWire` the tracker consumes, so a client can subscribe. Owner to ratify.
- **Settings.** `Bazaar:Kodo:Settings:<kodo.key>` reads the section 20 catalogue through `KodoSettingsValidator`: a bad or loosening value is ignored, the default applies, and the error is logged at start. Module wiring keys (`Enabled`, `DataDirectory`, `TickSeconds`, `HeartbeatSeconds`, `NonCriticalGraceSeconds`, `JournalMemoryRecords`, `DataVolumePath`, `IncludeClockDrift`) sit beside it. `Enabled` defaults to true and, when false, registers no loop, journal or probes.

## 6. Immutability

The brief's requirement is: signed and verified on every start, read-only runtime with separate writable data, atomic A/B versions with instant rollback, and runtime tamper detection. Each part is specified here.

### 6.1 Signed manifest

- Every file in a Kodo slot (binaries, managed assemblies, runbook files, probe catalogue, default settings file, the sentinel's verifier) is listed in `kodo.manifest.json` with its SHA-256 and size.
- The manifest is signed with the Bazaar release signing key (kept on the server build machine, doc 37). The signature is a detached signature over the canonical JSON of the manifest (RFC 8785 canonicalisation). The algorithm is RSASSA-PKCS1-v1_5 with SHA-256, the one doc 37 names for signed release manifests, behind a verifier interface. The canonical form is the Bazaar profile in `src/Bazaar.Kodo/Runtime` (fixed property order, no whitespace, file list sorted by path). It is not RFC 8785 yet, so matching RFC 8785 is an open item. This paragraph is PROPOSED. The manifest also carries the Kodo version, the protocol version of the health contract (section 10), the build commit and the SBOM digest (doc 37).
- The install trust root is a set of public keys pinned at install time in the sentinel's configuration, with key ids and a validity window. A manifest signed by an unknown key is rejected. Rotation adds a new key under an old-key signature, and a revoked key list is itself signed.
- Verification runs at four points: every start (before any Kodo code loads), on a timer (default every 5 minutes), before every control action from the Responder, and before any slot switch.

### 6.2 Read-only runtime, writable data

Status: PROPOSED (2026-11-18). Replaces the first draft's Windows junction design; see the revision note under 6.3.

- **Layout.** The runtime root is one directory, `kodo-runtime`, on both OSes. It holds `slot-a` and `slot-b` (each a full runtime tree with its manifest) and three pointer files: `kodo.pointer.json` (the active pointer), `kodo.pointer.previous.json` (the pointer that was active before the last swap) and `kodo.pointer.json.tmp` (transient, written before a swap). The two pointer files are the only mutable items in the runtime root. The pointer has one location on Windows and Linux.
- **Windows.** The runtime root and each slot have a protected DACL, so no parent ACE reaches them. Slots grant read and execute to the Kodo service identity and to Administrators. The signed update principal can write only the inactive slot while it is being staged, and the pointer files. No ACE grants write to Users, Authenticated Users or Everyone. These ACLs stop the service account and ordinary users. They do not stop a full administrator (6.6). The ACE design is checked on the owner's PC before any of this is DECIDED (spike K1, sections 4.1 and 6).
- **Linux.** Each slot is bind-mounted read-only at `kodo-runtime/slot-a` or `kodo-runtime/slot-b`. A read-only bind is per mount point and leaves the original path writable, so the backing directory is kept out of any path the Kodo service account can write through (spike K2, finding F1). The update path remounts the inactive slot read-write only while it is staged, then remounts it read-only before verification. The pointer files sit in `kodo-runtime` itself, which is never a slot mount, so a pointer swap never needs a remount.
- **Provider probe.** `IImmutableRuntimeProvider` checks at start that read-only is enforced. A provider that cannot enforce it downgrades the module state to degraded and says so in the status. It does not run writable silently.
- **Data and evidence.** Data and evidence directories are the only other places Kodo writes. Kodo never writes into a slot. The verifier treats any file in a slot that is not in the manifest as tampering (6.1, 6.4).
- Config is not in the slot. Operator values are in the settings registry (doc 03) and in `kodo-data`, so changing a value never requires writing the runtime.

### 6.3 Atomic pointer swap and instant rollback

Status: PROPOSED (2026-11-18). Replaces the first draft's junction swap. The Windows and Linux mechanics are researched (spikes K1 and K2) and have not been run on hardware, so they stay PROPOSED until the host runs in those spikes pass.

Revision note. The first draft swapped a directory junction on Windows "with the same rule" as the file pointer, and put the pointer where 6.2 said the runtime was read-only. Spike K1 (`docs/spikes/K1-windows-readonly-slot.md`, section 3.8) found that `MoveFileEx` with `MOVEFILE_REPLACE_EXISTING` reports an error when the destination names an existing directory, and no primary source documents an atomic replacement of a directory link. Spike K2 (`docs/spikes/K2-linux-bind-mount-swap.md`, finding F3 and open risk 1) found that the pointer must live outside both read-only slot mounts. The most robust option is therefore a small signed pointer file, replaced by an atomic file rename on both OSes, in one location (6.2). Junctions and symlinks are not used for the pointer.

- **Pointer.** `kodo.pointer.json` is a signed record with the slot id (`slot-a` or `slot-b`), the SHA-256 of that slot's signed manifest envelope, the key id and the signature. It uses the same signature scheme and trust root as the manifest (6.1). A pointer that fails verification is never followed.
- **Swap.** Write the new pointer bytes to `kodo.pointer.json.tmp` in the same directory, flush them to disk, then replace `kodo.pointer.json` atomically: `ReplaceFile` on Windows, `rename(2)` on Linux. The temp file and the target are in the same directory on the same volume and mount, so the replace cannot fail with a cross-device error.
- **Update.** The new version is written into the inactive slot. Its manifest and files are verified (6.1 and 6.4), preflight runs (doc 49), and the pointer is then swapped to the new slot, which runs its probation window (default 10 minutes, a setting) and is rolled back on failure (PROPOSED, see 7.8; probation runs after the swap, as the sentinel implements it). Before the swap, the current pointer bytes are copied to `kodo.pointer.previous.json` with the same temp-then-replace rule, so the previous slot always has a signed pointer.
- **Rollback.** Rollback swaps the pointer back: `kodo.pointer.json` is replaced with the bytes of `kodo.pointer.previous.json`, and the pointer that was left takes the previous slot's place, so a second rollback goes forward again. Rollback needs no network, no database and no signing key, because both pointers were signed when they were made and are checked against the install trust root. The target slot's manifest and files are re-verified before the flip. A crash between the two writes leaves both pointers naming the older slot, which is a safe state. Rollback never deletes the slot it leaves. It writes an audit event and a Kodo journal event before the flip is announced to clients.
- **Start-up recovery.** Start removes a stray `kodo.pointer.json.tmp`. If `kodo.pointer.json` fails verification, the sentinel tries `kodo.pointer.previous.json`. If neither pointer verifies, Kodo runs in sealed mode (6.4).
- **Durability.** Linux is intended to flush the containing directory after the rename, and Windows flushes the temp file before the replace. Whether a power loss between the two writes leaves a usable state is checked in the host run (spike K2, item 9), not assumed.
- **Downgrade.** A validly signed pointer to an older slot still verifies. An anti-rollback floor (the sentinel refuses any slot version below the highest it has accepted) is OPEN and needs an owner decision. This section does not decide it.
- The sentinel performs rollback on its own when the new slot fails its probation or fails the signature check at start. Rollback is the same swap as above and works when the server is down.

### 6.4 Runtime tamper detection

- **Continuous re-hash.** A background scan re-hashes slot files on a schedule (default every 5 minutes, one file per tick if the slot is large). A mismatch is a critical `kodo.self.signature_verify` failure.
- **File system watch.** Change notifications on the slot raise an immediate verify, not an alert by themselves, because legitimate updates touch the slot.
- **Process image check.** At start and every 5 minutes, the loaded assembly hashes are compared to the manifest. Loaded code that does not match is a critical `kodo.self.image_verify` failure.
- **Response.** A tamper finding moves the Kodo state to `failing` with cause `integrity`, writes an audit security event, stops all control actions, and asks the sentinel to switch to the other slot if that slot verifies. If neither slot verifies, Kodo runs in sealed mode: it keeps the journal, serves read-only status with a visible "integrity failure" banner, and performs no action. Sealed mode needs an owner step-up to leave.
- **Evidence.** On any tamper finding Kodo snapshots the slot directory listing, the manifest, the file metadata and the process list into `kodo-evidence` before anything else changes.

### 6.5 Journal integrity

- Journal records are hash-chained. Each record stores its own SHA-256 over the canonical encoding of its fields and the previous record's hash. A break in the chain is a critical `kodo.self.journal_integrity` failure.
- Every 1,000 records or 15 minutes, whichever is first, Kodo writes a signed checkpoint with the chain head, a Merkle root over the records in the window, and the window's timestamp range. Checkpoints are signed with Kodo's own journal key, which is kept in the Identity Service (doc 14) and is distinct from the release key.
- Checkpoints are anchored externally (the brief's external anchoring). Anchor sinks are operator choices, and the default is the owner's paired devices (each device stores the latest checkpoint and its own copy of the head). Optional sinks: an RFC 3161 timestamp authority, an S3-compatible object store with object lock, and a public transparency log. Each sink is a module behind `IAnchorSink`.
- Merkle proofs: any single record can be proven against a checkpoint with a logarithmic-size proof. The API returns the proof on request (section 15.1). An auditor can verify a proof with the public key alone.

### 6.5.1 Anchoring and Merkle proofs: pure core (PROPOSED)

Built in `src/Bazaar.Kodo/Anchoring` (package 14, pure parts only; no sink, file, witness or HTTP code). These choices are PROPOSED, not DECIDED:

- **Tree.** Binary Merkle tree over journal record hashes, RFC 6962 shape (split at the largest power of two below n). Leaf = SHA-256(0x00 || record hash), node = SHA-256(0x01 || left || right), empty root = SHA-256 of nothing. A lone node is never duplicated, so `[a,b,c]` and `[a,b,c,c]` have different roots, and an inner node cannot pass as a leaf. This is the same prefix convention as the audit checkpoint tree in doc 48, but that tree promotes odd nodes and has no consistency proofs, so the two roots are not interchangeable. `Bazaar.Kodo` must not reference `Bazaar.Server` (section 19), so the code is separate; unifying them is an open follow-up.
- **Proofs.** Inclusion proofs (leaf in a tree of n) and consistency proofs (tree of m is a prefix of the tree of n), with verifiers that need only hashes and sizes (RFC 9162 algorithms). A proof does not bind the tree size by itself; the anchor record does.
- **Anchor record.** `version`, `sinkId`, `treeSize`, `root`, `journalHead`, `anchoredAt` (UTC). Canonical JSON: compact, camelCase, base64 hashes, source-generated, unknown members rejected.
- **When to anchor.** Every 1,000 new records or 15 minutes (the checkpoint cadence of 6.5), whichever comes first, and never when nothing is new. After a failed attempt, wait 1 minute before retrying. Ranges: 1 to 10,000,000 records, 1 second to 24 hours, retry 1 second to 1 hour. Not wired to settings yet.
- **Fail safe.** A sink that throws, declines or hangs (30 second timeout) never blocks the journal or the other sinks. With no accepting sink the journal is simply not anchored; the planner counts an anchor as done only when a sink accepted it.
- **Verifier.** A stored anchor is compared to the current chain: Verified only when the chain is intact and the anchored prefix hashes to the stored root and head. A rewrite with recomputed hashes, a broken chain or lost anchored records is Tampered. A malformed anchor, malformed records or a failed consistency proof between two anchors is Unknown, never Verified, and the status view must not show Unknown as healthy.

### 6.6 Threat model and stated limits

Kodo defends against:

1. Accidental or hostile modification of the runtime by a process running as the Bazaar service account.
2. A compromised module that tries to alter health results, history or alerts.
3. A bad update that changes behaviour silently.
4. Rewriting history after the fact, including by an operator with database access (the chain and external anchors catch it).
5. Silencing alerts by stopping Kodo. The sentinel and the service manager catch it, and clients show "unobserved" (section 8.3).

Kodo does not defend against a full administrator or root user who controls the signing keys, the anchor sinks and the owner's devices at once. That is the same boundary as the rest of Bazaar. The mitigations are split keys (release key on the build machine, journal key in the Identity Service, anchors on devices the operator does not run the server on), multi-party recovery (doc 14 and the owner recovery quorum), and external anchors. Kodo states this limit in its own status so an operator is never told the runtime is "tamper-proof".

### 6.7 Bootstrap trust

The sentinel is the root of Kodo's immutability, so it is the smallest and most reviewed binary. Its verifier code is also checked against the release manifest on install. First install is a signed installer from the server build (doc 37), and the install step writes the trust root. Reproducible builds are a later goal (brief: SBOM and signed releases now, reproducible later).

## 7. Failure model

The brief's requirement: supervised restart, crash-only design, bulkheads, circuit breakers, optional warm standby with automatic failover.

### 7.1 Supervised restart

- The sentinel restarts Kodo with a backoff schedule (`kodo.supervise.restart_backoff_s`, default 1, 2, 5, 15, 60, 300 seconds, then capped at 300). A crash loop (5 starts in 10 minutes) moves Kodo to `failing` with cause `crash_loop`, pins the previous slot if it has not been tried in the window, and alerts. Restart counts and exit codes are journalled.
- The server host is supervised the same way, by the sentinel and by the service manager. Kodo reports the host's restarts; it does not start the host itself except through the sentinel's supervision contract.

### 7.2 Crash-only design

- No component depends on a graceful shutdown. Every durable write is committed before its side effect, and startup is recovery: the journal is replayed to its last valid checkpoint, a partial tail record is truncated after its checksum fails, and in-flight runbook steps are resumed from their recorded step state with their idempotency keys.
- Shutdown is allowed to be abrupt. The only requirement is that the journal's last fsync boundary is consistent, which the recovery pass checks.
- Kill-tests are part of the test suite (section 17), so crash-only is a tested property, not a hope.

### 7.3 Bulkheads

- Every probe runs in one of four bounded pools: `local` (process and host reads), `db` (Postgres and cache), `external` (network, providers, Conduit, email, webhooks) and `slow` (SMART, backups, restore tests, self-tests). A pool has its own concurrency limit, queue depth and memory budget.
- Each probe has a hard timeout and a memory ceiling. A probe that exceeds either is cancelled, its result is recorded as `timeout` (never as green), and its pool slot is released.
- A pool that saturates degrades only the probes in that pool. Kodo marks its own pool saturation as an observation and does not let it block the evaluator or the stream.

### 7.4 Circuit breakers

- Each external probe target and each outbound sink (webhook, email, anchor sink, status page feed, model explanation endpoint) has a breaker: closed, open after N consecutive failures (default 5) or an error ratio over a window, half-open after a cooldown (default 60 seconds) with one trial call.
- An open breaker is an observation, not a silent skip. The probe result is `unknown` with cause `breaker_open`, which the evaluator treats as amber, never green.

### 7.5 Journal spill

- If Postgres is unreachable, Kodo writes its journal to a local append-only spill file in `kodo-data` with the same record format and hash chain. When Postgres returns, the spill is replayed into the database idempotently by record hash, then the spill is sealed and kept for the retention window.
- The spill is bounded (default 512 MB); at the bound Kodo keeps the newest records and marks the gap as a journal observation. A gap is never filled with invented data.
- **PROPOSED (package 5 review).** The `kodo.self.spill` probe reports the age of the oldest unsent spill record. A sealed or unreadable spill counts as Red, never Green. Owner to ratify.

### 7.6 Warm standby with automatic failover (optional)

- **Off by default.** The single-node default matches the scale decision. The standby is a second host with its own Kodo, an identical slot and a replicated journal.
- **Replication.** The journal streams to the standby over the authenticated wire (doc 02), and Postgres replication is used when the operator configures it (doc 04 and 33). The standby's Kodo holds a warm copy of state and verifies its replicated checkpoints.
- **Failover.** A lease with a monotonically increasing fencing token is held by the primary. The lease is granted by a witness quorum (default 2 of 3 witnesses: paired owner devices plus one optional third node). The standby takes the lease when the primary's lease expires and two witnesses agree the primary is unreachable from them. The fencing token is written into every journal record and every control action, so a stale primary is rejected by the database and by the standby.
- **Failback** is manual by default, and needs a role with `kodo.failover.approve` plus step-up. Automatic failback is an operator setting that is off.
- **Split brain** is treated as the top incident. If fencing fails, Kodo enters incident mode with cause `split_brain` and both nodes go sealed until a human resolves it.
- Standby does not change the single-node scale default. It is an availability option, not a scaling design. Scaling is in section 7.7.

### 7.7 Scale

- The brief's scale decision is one large company on a single node, splittable to several nodes later. Kodo's probes, journal and API run on that one node by default. Kodo's data volume is bounded by the retention settings (section 20), not by the number of agents.
- Multi-node splitting is not designed here. When it is, Kodo's journal needs a single writer per install (already true: the journal has one writer, section 5.1) and its probes need a routing table that says which node runs which probe. That design belongs with doc 03's host split and is not started.

### 7.8 Sentinel implementation choices (package 8, PROPOSED 2026-11-18)

The pure parts live in `src/Bazaar.Kodo/Sentinel`, with the OS behind `ISentinelFileStore`, `IKodoProcessLauncher`, `ISentinelJournal` and `ISentinelStatusFile`, and the clock as `TimeProvider`. Every choice below is PROPOSED because the doc leaves it open. Owner to ratify.

- **Stable period.** A run of at least 10 minutes clears the restart backoff. Section 7.1 names no stable period. The schedule's last value is the cap (300 s).
- **Probation runs after the swap.** The candidate pointer becomes active, then the slot runs its probation window (`kodo.integrity.probation_min`, default 10 minutes). A failed probation rolls back. Section 6.3 now matches this choice (probation after the swap, with rollback on failure). An earlier draft had the new Kodo run in shadow mode before the swap; the coordinator kept probation-after-swap because rollback needs no extra mechanism. Still PROPOSED for owner ratification.
- **Probation pass.** The process survives the window, and the active pointer and slot verify again at the end.
- **Probation cannot be skipped (PROPOSED 2026-11-19).** The probation marker `kodo.probation.pending` (runtime root, holds the candidate slot id) is written after the previous pointer and before the swap, and removed only after probation survives. A launcher exception, a cancelled token (sentinel shutdown), a status-file failure when announcing probation, or a marker that cannot be cleared counts as a failed probation and rolls back; cancellation rolls back first and then rethrows. A start check that finds the marker with the active pointer naming the marked slot (or an unreadable or malformed marker) treats the active pointer as unprobated: it rolls back to the previous pointer if that verifies, otherwise it seals (cause `probation_interrupted`). A marker naming another slot is stale and removed. If the previous pointer equals the active pointer, the swap never happened and the marker is just cleared.
- **Rollback target.** Promotion re-reads and verifies the active pointer from disk. With no verified active pointer it is rejected (`active_pointer_not_verified`), so a stale previous pointer from an older generation is never kept as the rollback target.
- **Locking.** The gate serialises start and promotion. A second lock guards the restart backoff, the status and the status-file write, so `RecordKodoExit` is safe from any thread, also during probation, and is never held across the probation run. A status-file write that fails after a decision is dropped (the in-memory status carries the truth), except the probation announcement, which is strict.
- **Rollback order.** Journal event first (`sentinel.rollback`), then the active pointer swap, then the previous pointer is set to the left pointer, then the status file is replaced. A journal write failure does not block the rollback; the status detail records it. The audit event (doc 48) is not written in this slice.
- **Blocked rollback.** If the previous pointer is missing or does not verify, no flip happens. The candidate stays active with phase `failing` and cause `previous_slot_missing` or `previous_not_verified`.
- **Startup fallback.** A failed active pointer falls back to the verified previous pointer. The active file is rewritten, and the previous file is left unchanged. Neither pointer verifying gives phase `sealed` with no active slot.
- **Status file.** `kodo.sentinel.status.json` in the data directory, never in the runtime root. Schema version 1, camelCase, every field always written. Phases: `starting`, `running`, `probation`, `failing`, `sealed`.
- **Cause codes.** Stable strings in `SentinelCauses`. The verifier's status name goes in `detail`.
- **Not in this slice.** Crash-loop detection (7.1), the OS launcher and file store, the loopback server, the audit event.

### 7.9 Standby core choices (PROPOSED 2026-11-22)

The pure parts of 7.6 live in `src/Bazaar.Kodo/Standby`. Witnesses, journal and state store are interfaces (`IStandbyWitness`, `IStandbyJournal`, `IStandbyStateStore`) and time comes from `TimeProvider`. No concrete witness, no replication and no database fencing are built. Every choice below is PROPOSED because 7.6 leaves it open. Owner to ratify.

- **Roles.** `Primary`, `Standby`, `Fenced`. A split brain is a fenced node with cause `split_brain` (7.6 calls it sealed). Cause codes are stable strings in `StandbyCauses`.
- **Fencing token.** A non-negative epoch that only goes up. Epoch 0 means no token. A token below the current epoch, or epoch 0, is always stale. A token above the node's own epoch means this node is the stale one, so it fences itself (cause `superseded`). A node that is not primary admits nothing, and a primary whose lease has run out fences itself rather than admit a write.
- **Lease.** Holder, epoch, granted and expiry times (UTC). Version 1. A new epoch is one above the highest epoch ever seen, never above only the lease the node knows.
- **Quorum floor.** The witness set has at least 2 witnesses and the required count is at least a strict majority (n/2 + 1), at most all of them. The default is the strict majority: 2 of 3, or 2 of 2 when only paired devices exist. A setting may raise it, never lower it below the floor. Each witness counts once, unknown ids are ignored, and a witness that contradicts itself counts as no answer.
- **Failover rule.** The standby promotes only when all hold: it is a standby, a lease is known, that lease is expired past the clock skew margin, no witness reports the primary reachable (conservative: one witness that still sees it blocks the failover), and a quorum reports it unreachable. With no lease ever seen it never promotes; the first lease comes from an explicit bootstrap that needs a grant quorum and refuses when any witness knows a lease. Promotion also needs a grant quorum for the new epoch, then journal first, state saved second. If either fails the standby stays and the attempt is refused (a failed save writes `standby.promotion_aborted`).
- **Demotion.** The primary stops at its lease expiry with no margin, and the standby waits for expiry plus the margin (default 3 s), so they do not overlap while clocks differ by less than the margin. Fencing is never blocked: the in-memory role changes first, and journal and save are best effort. A restart that cannot read its saved state, or finds a state for another node or an expired primary lease, fences the node.
- **Timings (settings to be named, defaults PROPOSED).** Lease 15 s, renew when 5 s or less is left, skew margin 3 s, witness call timeout 3 s. A witness that throws or hangs counts as no answer.
- **Failback.** Manual only in this slice. A person returns a fenced node to standby (`RejoinAsStandbyAsync`), which needs a journal write. After a split brain it also needs the caller to state that a human resolved it. The `kodo.failover.approve` role and step-up check belong to the caller.
- **Records and journal.** Lease and state records are pure, signed-nothing, and have a canonical compact camelCase JSON codec with a SHA-256 digest. Every standby journal entry (`standby.promoted`, `standby.promotion_aborted`, `standby.fenced`, `standby.rejoined`, `standby.stale_rejected`) carries the canonical state, so the epoch is in every record, and the entry is the kind and payload that the journal chains into a `JournalRecord`.
- **Not in this slice.** Concrete witnesses, journal and store implementations, journal replication, database-side rejection of stale epochs, incident entry on `split_brain` (the tick result only exposes `SplitBrain`), automatic failback, settings wiring, and the fencing spike on the chosen database setup.

### 7.10 Postgres journal store choices (PROPOSED 2026-11-22)

Built as `Bazaar.Server.Kodo.Postgres` (raw Npgsql on the shared data source, embedded migrations in the `kodo` namespace, registered by `AddBazaarKodoPostgres`) behind `IKodoJournalStore` in `Bazaar.Server.Kodo.Abstractions`. It follows `Bazaar.Server.Presence.Postgres`. Every choice below is PROPOSED. Owner to ratify.

- **The local chain stays the writer.** The memory and spill journal appends first and never waits on the database. Each monitor pass then replays what the store lacks (`KodoJournalSync`), in batches of 500, with a 15 second cap so a hung database cannot hold the loop. With no store registered, `Bazaar:Kodo:UseJournalStore` false, or the database down, the journal is just the memory and spill journal of 5.4, and the records are sent when the database returns.
- **Idempotent by hash.** `kodo_journal` is keyed by sequence and unique by record hash. An append runs in one transaction under an advisory lock, skips records whose hash is stored (`JournalReplay`), and writes only the new tail. The decision is pure logic (`KodoJournalAppendPlanner`) so it is tested without a database.
- **Never merged.** A first new record that does not link to the stored head (fork), a hole after the head (gap), a different hash at a stored sequence, or a run that does not verify stops the sync for the rest of the run. `kodo.self.spill` then reads sealed, which is red (7.5). Nothing is rewritten and no gap is filled. The one exception is an empty store, which accepts a run that starts later, because the memory window may have dropped older records.
- **Restart.** Before the first record of a run, the monitor reads the stored head. With no local records it continues the stored chain from the store's newest records (and leaves the spill file closed for that run, because a file that does not start at record 0 does not verify on reopen). A local chain that is ahead is replayed. One that disagrees with the stored head is a fork. A memory-only install that restarts while the database is down and holds history there will fork; the operator should set `DataDirectory`.
- **Exact time.** Time is stored as UTC ticks (`at_ticks`), because `timestamptz` rounds to microseconds and the hash covers the ticks. Rows are never updated (a trigger refuses it); only retention deletes.
- **Verified on read.** A range read re-checks every link and hash, and a break throws a conflict instead of returning the records.
- **Retention.** The store prunes by age once an hour, never the head. `kodo.retention.journal_days` (730) is counsel-gated and not in the settings registry yet, so the wiring key `JournalRetentionDays` stands in. Pruned history no longer starts at record 0, so proofs stop (6.5.1; checkpoint-based proofs are the follow-up).
- **State history and snapshot.** `kodo_state_change` is derived from `state` records in the same transaction as the append. `kodo_snapshot` holds one row, the latest wire snapshot, written at most every 30 seconds and replaced whatever its revision, because stream revisions restart with the process. Nothing reads them back into the monitor yet.
- **Proof route.** `GET journal/proof/{sequence}` answers from memory first. When the record is older than the memory window the store builds the proof over the whole stored history (up to 1,000,000 records) with the same Merkle tree as 6.5.1, and answers 404 when the history has holes or does not start at 0.
- **Host.** `HostApplicationExtensions` registers the store and `HostMigrations` applies the `kodo` namespace. Database tests need Docker or `BAZAAR_TEST_PG`; the rest run without.
- **Not in this slice.** Checkpoints and anchors on the stored chain, the spill seal and size bound (7.5 512 MB, `kodo.journal.spill_max_mb`), reading the snapshot or state history through the API (`GET /history`), and an operator action to resolve a fork.

## 8. State model

### 8.1 States

Kodo's health state is one of five values. Each has a colour token from the theme (doc 27) and a text word for accessibility, so colour is never the only signal. The tokens are `color.health.<state>.background` and `color.health.<state>.text`, one pair for each of the five states and for the client-derived `unobserved` display value (section 8.3). A view paints a state with a `badge` whose `variant` is the state name (doc 41c, section 3).

| State | Word | Meaning | Automatic actions allowed |
|---|---|---|---|
| `healthy` | Healthy (green) | All critical probes green, no SLO burn above the warning threshold, no open amber rule for more than the grace period | Yes: scheduled self-checks, low-risk runbooks marked auto |
| `degraded` | Degraded (amber) | A non-critical probe is red or amber beyond grace, or an SLO is burning above warning, or a critical probe is `unknown` for less than its unknown budget | Yes, but no new automatic changes; runbooks need approval |
| `failing` | Failing (red) | A critical probe is red, an integrity or contract failure is open, or a crash loop is active | Yes: runbooks with approval, failover candidates, incident entry |
| `recovering` | Recovering (amber with a spinner word) | A recovery action is running (restart, slot switch, runbook, failover, restore) and has not yet passed its stability window | Only the running action continues; nothing new starts |
| `frozen` | Frozen (incident colour) | Incident mode is active. The underlying health state is still computed and shown beside `frozen` | No automatic changes; risky actions blocked (section 16.3) |

`frozen` is the incident-mode state. It is not an alternative health reading: the Monitor pane shows `frozen` together with the underlying health (for example "frozen, underlying failing").

### 8.2 Transitions

| From | To | Condition |
|---|---|---|
| healthy | degraded | Non-critical red or sustained amber past grace, or SLO warning burn |
| healthy | failing | Critical red, integrity or contract failure, or crash loop |
| degraded | failing | Any critical red, or a second non-critical red that breaches an SLO |
| degraded | healthy | All rules green for the stability window (default 5 minutes) |
| failing | recovering | A recovery action starts |
| failing | healthy | Not used directly; a recovery must pass the stability window first |
| recovering | healthy | Stability window passed with all rules green |
| recovering | failing | Action failed, timed out, or rules still red after the action's verify step |
| any | frozen | Incident mode entered (manual or automatic trigger, section 16.2) |
| frozen | previous or re-evaluated state | Incident mode exited with `kodo.incident.exit` and step-up |

Hysteresis is required everywhere. A rule with a grace period cannot flap a state. Flap detection (more than 6 transitions in 30 minutes) forces `degraded` and raises a single grouped alert rather than a stream of them.

### 8.3 Unobserved

Kodo cannot report its own death. Clients derive a sixth display value, `unobserved`, when the Kodo stream has been silent for longer than its heartbeat budget (default 30 seconds); any event, including a heartbeat, counts as proof of freshness (15.6). `unobserved` is not a server state, is never written to the journal by Kodo, and is rendered as its own word and colour. The client writes it to its own local timeline.

## 9. Scope: what Kodo watches

Kodo's scope is the server, its modules, the fleet of clients and the whole environment (brief: hardware SMART, temperature, power; Conduit network paths; certificates; clock drift; backups; AI model endpoints). Each category is a probe family. Every probe declares its category, its criticality, its cadence, its pool and its provider interface.

Probe criticality: **critical** probes can make the state `failing`. **Non-critical** probes can make it `degraded` only. A probe may be `unknown` (timeout, breaker open, provider missing) and `unknown` never counts as green.

### 9.1 Built-in probes (PROPOSED catalogue)

Thresholds for `host.disk.free`, `host.memory.pressure` and `host.clock.drift` are PROPOSED defaults, passed as constructor options and overridable in config. Owner to ratify the values.

| Probe id | Category | Critical | Cadence | Pool | Method and provider |
|---|---|---|---|---|---|
| `host.live` | Server | yes | 10 s | local | `/health/live` (doc 03) |
| `host.ready` | Server | yes | 10 s | local | `/health/ready` (doc 03), readiness roles from setting |
| `host.service_state` | Server | yes | 30 s | local | Service manager state via `IServiceHostProvider` |
| `host.restart_count` | Server | no | 1 min | local | Restarts in the last hour from the sentinel |
| `host.disk.free.<volume>` | Host | yes for data volume | 1 min | local | Volume free space, thresholds from doc 03 settings |
| `host.memory.pressure` | Host | no | 1 min | local | Available memory and growth trend (doc 03 floor for growth) |
| `host.cpu.load` | Host | no | 1 min | local | Load average or processor time, per provider |
| `host.clock.drift` | Host | yes past hard limit | 5 min | external | NTP offset against the configured sources (default: the platform time service) |
| `host.smart.<device>` | Host | yes on failed attribute | 1 h | slow | `ISmartProvider` (smartctl on Linux, storage management APIs on Windows) |
| `host.temperature.<sensor>` | Host | no | 1 min | local | `ISensorProvider` |
| `host.power.<ups>` | Host | yes on low battery on battery power | 30 s | local | `IPowerProvider` (NUT on Linux, UPS service on Windows, none is `unknown` only if configured) |
| `host.log.rotation` | Host | no | 1 h | local | Log file sizes and rotation state (doc 03 caps) |
| `db.reachable` | Data | yes | 10 s | db | `SELECT 1` with 2 s timeout |
| `db.schema_version` | Data | yes | 60 s | db | Schema version against the build (doc 04) |
| `db.connections` | Data | no | 30 s | db | Pool saturation and waits |
| `db.replication_lag` | Data | yes past limit, when standby configured | 30 s | db | Replication status (7.6) |
| `db.backup_freshness` | Data | yes when the last backup is past its window | 15 min | slow | Last successful backup age (doc 33) |
| `db.archive_lag` | Data | no | 5 min | db | WAL archive status where archiving is configured |
| `cache.reachable` | Data | no (presence degrades, doc 23) | 10 s | db | Cache provider ping |
| `search.available` | Data | no | 60 s | db | Search provider (pg_search or Postgres FTS) |
| `secrets.store` | Security | yes | 30 s | local | `ISecretStore` ping and key-id listing (doc 14) |
| `secrets.custody` | Security | no | 1 h | local | Required provider keys present, none expired |
| `certs.server` | Security | yes within 7 days of expiry, no at 30 | daily | external | Server certificate expiry and the pinned key hash (doc 32) |
| `certs.conduit` | Security | no | daily | external | Conduit certificate expiry (doc 39) |
| `audit.chain` | Security | yes | 15 min | db | Sampled audit hash-chain verification (doc 48 when written) |
| `gc.notify_listener` | Messaging | yes | 10 s | db | GC NOTIFY listener alive (doc 04, doc 01) |
| `gc.append_latency` | Messaging | no | 30 s | db | p95 append latency from the GC log writer |
| `gc.event_gaps` | Messaging | yes | 5 min | db | Per-GC sequence gap check (doc 01) |
| `vault.git_integrity` | Knowledge | no | 1 h | slow | Sampled `git fsck` on the Vault repo (doc 18) |
| `vault.index_drift` | Knowledge | no | 1 h | db | Postgres index against git heads (doc 18) |
| `kanban.blocked_invariant` | Work | no | 5 min | db | Derived Blocked consistent with dependencies (doc 12) |
| `agents.queue_depth` | Agents | no | 30 s | db | Job and turn queue depth against the setting |
| `agents.turn_stalls` | Agents | no | 1 min | db | Turns with no event past the loop-guard window (doc 06) |
| `agents.vm.<vm>.reachable` | Agents | no | 60 s | external | VM provider reachability (doc 16) |
| `agents.vm.<vm>.disk` | Agents | no | 5 min | external | Guest disk through its provider |
| `gateway.provider.<id>.health` | Models | no | 60 s | external | Provider health (doc 05) |
| `gateway.lane.<id>.quota` | Models | no | 60 s | external | Lane quota state (doc 05, doc 23 near-limit rule) |
| `gateway.fallback_trail` | Models | no | 5 min | local | Fallback-trail anomalies from served-model checks (doc 05) |
| `conduit.<provider>.probe` | Network | no, critical if it is the only path | 60 s | external | `IConduitProvider.ProbeAsync` (doc 39). Kodo never calls `OpenAsync` |
| `conduit.ratelimit.lockouts` | Network | no | 1 min | local | Count of rate-limit and auth-lockout events (doc 39, doc 32) |
| `webhook.<id>.delivery` | Integrations | no | 5 min | local | Dead-letter depth and last delivery result |
| `email.outbound.queue` | Integrations | no | 5 min | external | Outbound queue depth and the last send result (doc 22) |
| `clients.<device>.seen` | Fleet | no | 60 s | local | Last seen and stream state |
| `clients.version_skew` | Fleet | no | daily | local | Versions in the fleet against the current release channel |
| `clients.crash_rate.<version>` | Fleet | no | 5 min | local | Crash reports per version (doc 41 deterministic capture, redacted) |
| `module.<id>.health` | Modules | per the module's manifest | 30 s | local | Module health contract, section 10 |
| `module.<id>.self_test` | Modules | per the module's manifest | daily or on demand | slow | Module self-test, section 10 |
| `update.security_pending` | Updates | no | 1 h | local | Pending security updates per policy (doc 49) |
| `selfcheck.checksums` | Testing | no | daily | slow | Data and evidence checksum scan (section 17.2) |
| `selfcheck.restore` | Testing | no, yes if the last restore failed past its window | weekly | slow | Restore a backup into a scratch database (doc 33) |
| `selfcheck.key_recovery` | Testing | no | monthly | slow | Decrypt a recovery bundle in the sandbox (doc 33, doc 14) |
| `kodo.self.signature_verify` | Kodo | yes | 5 min | local | Section 6.4 |
| `kodo.self.image_verify` | Kodo | yes | 5 min | local | Section 6.4 |
| `kodo.self.journal_integrity` | Kodo | yes | 15 min | local | Section 6.5 |
| `kodo.self.slot_consistency` | Kodo | yes | 1 min | local | Pointer, slots and probation state agree |
| `kodo.self.sentinel` | Kodo | yes | 10 s | local | Sentinel heartbeat and verifier check |
| `kodo.self.pools` | Kodo | no | 30 s | local | Bulkhead saturation (7.3) |
| `kodo.self.spill` | Kodo | no | 1 min | local | Spill file size and replay state (7.5) |
| `kodo.self.journal_writable` | Kodo | yes | 30 s | local | Journal writer can append and fsync; added in package 5 (PROPOSED) |

Adding a probe is a change to the catalogue, which lives in the signed runtime slot, so a new probe is an update, not a setting.

## 10. Module health contract (MANDATORY)

Every module, first-party or third-party, must implement the health contract. A module that does not implement it fails to load. The contract is enforced by contract tests that run in the module's CI, at install (a dry run), and on every Kodo start for every loaded module. The brief's four parts are liveness, readiness, dependencies and self-test.

### 10.1 Contract

```csharp
// Bazaar.Kodo.Abstractions. Doc 42 owns the general module contract; these members are the health part.
public interface IModuleHealth
{
    string ModuleId { get; }

    /// Is the module's execution alive? Must not perform I/O. Budget 50 ms.
    ValueTask<HealthResult> LivenessAsync(CancellationToken ct);

    /// Can the module serve right now? May read cached dependency state. Budget 500 ms.
    ValueTask<HealthResult> ReadinessAsync(CancellationToken ct);

    /// Every dependency the module uses, declared, with its kind. Readiness of a hard
    /// dependency that is red makes this module not ready. Soft dependencies make it degraded.
    IReadOnlyList<DependencyDeclaration> Dependencies { get; }

    /// A bounded functional check. Side-effect free, or run inside a sandbox scope that is
    /// rolled back. Budget declared in the manifest, never more than 60 s.
    ValueTask<SelfTestResult> SelfTestAsync(SelfTestScope scope, CancellationToken ct);
}

public enum DependencyKind { Hard, Soft }   // Soft matches doc 40's SoftDependsOn

public sealed record DependencyDeclaration(string Id, DependencyKind Kind, string? MinVersion);

public sealed record HealthResult(HealthStatus Status, string? Reason, IReadOnlyDictionary<string, string> Facts);
public enum HealthStatus { Pass, Warn, Fail, Unknown }

public sealed record SelfTestResult(HealthStatus Status, TimeSpan Duration, bool LeftNoResidue, string? Reason);
public enum SelfTestScope { Quick, Full }   // Quick: seconds, read-only. Full: sandbox scope, scheduled or on demand
```

### 10.2 Semantics

- **Liveness** answers "is the code running". A module with a stuck event loop fails liveness. Liveness has no I/O by rule, so the contract test replaces the module's I/O services with throwing fakes and requires a pass.
- **Readiness** answers "may I take work". Its inputs are its own state and its declared dependencies' last readiness, read from Kodo's cache, so readiness cannot hang on a dead dependency.
- **Dependencies** are declared, not inferred. The loader checks that every hard dependency is loaded and ready before the module is started, and Kodo checks the same edges at runtime. Undeclared use of another module's internals is caught by the component-boundary rule of doc 40 (section 3 item 3 there).
- **Self-test** exercises real behaviour (for example, write and read a record in a sandbox scope, verify a checksum, round-trip a serialisation). `Quick` runs read-only. `Full` runs inside a sandbox scope (a transaction that is rolled back, or a temporary schema) and must report `LeftNoResidue` true, or the module is marked `Fail` with reason `residue`.

### 10.3 Contract tests

`Bazaar.Kodo.Testing` provides an abstract base class, `ModuleHealthContractTests<TModule>`, that every module's test project inherits. It asserts:

1. The manifest contains a `health` block and every declared dependency is one of the module's loaded dependencies (no orphan declarations).
2. Liveness performs no I/O (fakes that throw are installed for every I/O abstraction the module registers).
3. Readiness returns `Fail` or `Unknown` when a hard dependency fake returns `Fail`, and does not return `Pass` for a module whose hard dependency is `Unknown`.
4. Readiness and liveness honour their budgets under a slow fake (a fake that waits longer than the budget must produce `Unknown` with reason `timeout`, not a hang).
5. Self-test `Full` leaves no residue (row counts and file manifests are compared before and after).
6. Self-test respects its declared budget.
7. A module with no health block, or with a self-test that returns `Pass` without running anything (checked by a mutation probe), fails the suite.
8. Negative test: a module whose `ReadinessAsync` throws is reported as `Unknown` by Kodo, not as `Pass`, and is not silently removed from the state.

Contract tests run in CI on every PR that touches a module, and in the install dry run, where a failing contract blocks enablement and is reported with its test name. Third-party modules run the same suite from the SDK (`Bazaar.Sdk`).

### 10.4 Contract versioning

The health contract has its own protocol version (`kodo.contract = 1` at launch). It is a semver contract under doc 49's and the module API stability rules (brief: semver contract, compatibility shims, contract tests, compatibility matrix in the module manager). A module declares the contract version it targets. Kodo keeps the previous major as a shim for one release.

### 10.5 First slice (package 6): PROPOSED

Package 6 builds the abstractions, the loader gate and the contract test base only. No real module is wired. The rest of section 10 (liveness, readiness, dependencies, self-test, the manifest `health` block) comes in later slices. The choices below are PROPOSED where section 10 is silent.

- **Narrowed interface (PROPOSED).** `IModuleHealthProbe` in `Bazaar.Kodo` (not `Bazaar.Kodo.Abstractions`; the project is still one assembly) reports `ModuleId`, `ProbeId` and `ValueTask<ProbeResult> CheckAsync(ct)`. The section 10.1 `IModuleHealth` (liveness, readiness, dependencies, self-test, `HealthResult`) is a later slice and is not reused under the same name.
- **Probe id rule (PROPOSED).** A contract's `ProbeId` must be `module.<moduleId>.health`, the section 9.1 catalogue id. A contract that reports another module id or probe id is `unknown` with cause `contract_error`.
- **Loader gate (PROPOSED).** A missing contract is `unknown` with cause `contract_missing`. A contract that throws while loading, checking or returning no result is `unknown` with cause `contract_error`, and keeps the exception in `Error`. Cancellation propagates to the caller and is not a contract failure. Section 9 already requires that `unknown` never counts as green.
- **Contract test base (PROPOSED location).** `ModuleHealthContractTests<TModule>` is in the `Bazaar.Kodo.Testing` project (the name in section 10.3), which references xUnit v3 and `Bazaar.Kodo`. `Bazaar.Kodo` stays free of xUnit. The checks are pure (`ModuleHealthContractCheck`) and return violations, so they are testable without xUnit. This slice runs these section 10.3 checks: contract presence, expected `ModuleId`, catalogue `ProbeId`, a stable `ModuleId` across reads, and `CheckAsync` completing without throwing. The I/O fakes, budgets, self-test and manifest checks (10.3 items 2 to 8) come later.
- **Contract causes.** `contract_missing` and `contract_error` are added to `ProbeCauses` beside the other causes.
- **Dependency propagation (built, in `Bazaar.Server.Modules`).** `ModuleHealthPropagator` applies the section 10.1 rule to probe reports: a Failing hard target makes its dependents Failing, an Unknown hard target leaves them Unknown, a Degraded hard target or any soft target that is not Healthy makes them at most Degraded, and a module's own worse status stands. It orders by hard edges only and reads soft targets by their own status, so mutual soft links stay stable (components section 4.1). Not wired to the Monitor pane yet.

## 11. Module manifest (sketch)

Each module ships a manifest that Kodo reads from the module's signed package. Shape only; field names are PROPOSED and align with doc 40's component manifest.

```json
{
  "id": "gc",
  "name": "GC",
  "kind": "first-party",
  "version": "0.1.0+a1b2c3d",
  "kodo.contract": 1,
  "dependencies": {
    "hard": ["audit"],
    "soft": ["kanban"]
  },
  "health": {
    "liveness": "builtin",
    "readiness": "builtin",
    "selfTest": { "quickBudgetMs": 2000, "fullBudgetMs": 30000, "scheduled": "daily" }
  },
  "probes": ["gc.notify_listener", "gc.append_latency", "gc.event_gaps"],
  "runbooks": ["runbooks/gc-notify-restart.json"],
  "permissions": ["db.read:gc", "db.write:gc", "kodo.observe"],
  "signature": { "keyId": "release-2026", "alg": "Ed25519", "value": "<base64>" }
}
```

Rules: `permissions` are declared at install and approved by the owner (module trust rule in section 19); a module with no `health` block does not load; a `probes` entry must exist in the signed catalogue; `runbooks` entries are signed with the module package.

### 11.1 Manifest core choices (PROPOSED)

Built as a pure core in `src/Bazaar.Kodo/Manifest` (no file IO, no clock reads, no stores). All of the choices below are PROPOSED; nothing here is DECIDED.

- **Decoder.** Source-generated `System.Text.Json`, unknown fields rejected. A pre-pass over the tokens rejects duplicate field names at any level, nesting deeper than 6, and input over 64 KiB. Any other parse failure, including an unknown field or a wrong value type, is `manifest_malformed`. The member `kodo.contract` keeps its dotted name.
- **Canonical bytes.** The manifest re-serialised in declaration order without whitespace and without the `signature` block, the same profile as the runtime manifest (section 6.1). The signature covers those bytes, so reordering or re-spacing the stored file does not change what is signed. The SHA-256 of the canonical bytes is the manifest digest, so an owner approval can be bound to one exact manifest.
- **Signature.** The existing `RuntimeSignatureChecker` and `IRuntimeSignatureVerifier` check it against a `RuntimeTrustRoot`; no crypto is duplicated. The `alg` field must equal the pinned key's algorithm (`signature_alg_mismatch` otherwise), so a manifest cannot choose its own. The sketch's `Ed25519` is illustrative: the shipped verifier is RSA PKCS#1 SHA-256, as for the runtime manifest, and a further verifier plugs in without a change here.
- **Rules.** No `health` block: `health_missing`. `health.liveness` and `readiness` are `builtin` or `module`; self-test budgets are 1 to 60000 ms with quick not above full; `scheduled` is `off`, `hourly`, `daily` or `weekly`. `kodo.contract` must be 1. `id` and dependency ids use the module id shape, no self dependency, no repeats, and a module is not both hard and soft. `kind` is `first-party` or `third-party`; `version` is SemVer.
- **Probes.** Each id must have the component id shape, no instance-like hex run, no repeat, and exist in the probe catalogue the caller passes (`ModuleProbeCatalogue`; an entry ending in a dot is a family prefix). The caller builds it from the signed package catalogue after checking that file's own signature. No module-id prefix rule is applied.
- **Runbooks.** Each path must be `runbooks/<name>.json`, relative, with `/` separators only, segments of letters, digits, `.`, `_` and `-`, starting with a letter or digit, no `..`, no backslash or colon, no Windows device names, no repeats ignoring case. The manifest names runbooks; their definitions are still verified one by one by the runbook catalogue (section 16.2).
- **Permissions.** Each is a dotted name with an optional scope (`db.read:gc`, `kodo.observe`), lowercase, no wildcard, no repeat, at most 64. A valid manifest never grants one: the decision lists every declared permission the owner has not approved, and a module with any pending permission is `NeedsOwnerApproval`, not `Loadable`. `kodo.observe` is not special-cased.
- **Decision.** `ModuleLoadDecision` has an outcome (`Loadable`, `NeedsOwnerApproval`, `Rejected`), all findings in a fixed order with stable cause codes (`ModuleManifestCauses`) and the field, the pending permissions, and the digest. The caller passes the time and the permissions already approved.
- **Limits.** Per list: dependencies 64, probes 128, runbooks 32, permissions 64, each entry at most 128 characters.
- **Not built.** Reading the package from disk, the signed catalogue file, the approval store and owner prompt, the install dry run, the `health` block's link to `IModuleHealth`, the module trust rule of section 19, and the module manager UI.

## 12. Score, SLOs and the status view

### 12.1 Colour and words

Green, amber and red are derived from the state (section 8), never from the score. The score cannot make a red system green. The status word always appears with the colour.

### 12.2 Health score (0 to 100)

- Each category in section 9 has a component score from 0 to 100, computed from its probes with the probe weights in the catalogue.
- The overall score is the weighted mean of the category scores, with weights set in the settings registry (defaults: server 25, data 20, security 15, modules 15, host 10, network 5, fleet 5, models 5).
- **Caps.** A failing critical probe caps the overall score at 49. An integrity failure caps it at 10. An `unknown` critical probe caps it at 70. Caps are a safety floor so a broken dashboard cannot read as healthy.
- The score is published with its inputs, so anyone can recompute it from the journal (section 15.1 returns the breakdown).

### 12.3 SLOs and error budgets

- SLOs are declared per service in the module manifest or in the install's Kodo settings, with a window (default 30-day rolling) and a target. Defaults: company server availability (readiness true) 99.5 percent; GC append success 99.9 percent; GC append p95 under 250 ms; client stream freshness under 5 s for 99 percent of time; model lane availability 95 percent (per provider, since providers are outside the owner's control).
- The error budget is `(1 - target) * window`. Its remaining fraction is shown on the dashboard, and the budget is a time budget for reliability, not a money budget (Resource Economy stays REJECTED).
- Burn-rate alerts use two windows (default 1 hour and 6 hours), so a fast burn alerts quickly and a slow one alerts without noise.
- Uptime percentages are computed from the journal, so they can be verified by replay.

### 12.4 Public status page (optional, OFF by default)

- A read-only page with component statuses, the uptime figures for the public SLOs, the open incident list and the post-incident summaries (approved ones only).
- It is off by default. Its contents are a setting: a list of public components, and a switch that shows incident titles or hides them. No agent names, GC or Vault content, customer data, key ids, host names or internal probe ids are ever shown.
- It is served by the status listener of doc 02 and doc 03, and it follows the bind floor in section 15.5.

### 12.5 SLO core choices (PROPOSED)

The pure core lives in `src/Bazaar.Kodo/Slo` (namespace `Bazaar.Kodo.Slo`). It reads no clock (every call takes `now`), does no I/O and has no concrete store beyond an in-memory one. It builds on 12.3 and 8.3; the choices below are where those sections are silent. Nothing here is DECIDED.

- **Definition.** An SLO has an identifier (lowercase letters, digits and underscores, up to 64), a target in parts per million (no floating point), a rolling window and an indicator kind. The target has a floor of 90 percent and a ceiling of 99.9999 percent (100 percent has no budget). The window runs from 1 hour to 31 days (default 30 days). Settings may raise the floor but never lower it. The defaults of 12.3 that fit the two kinds are built (server availability, group chat append latency, stream freshness, model lane per provider). "Group chat append success 99.9 percent" is a count of events, not state samples, and is not built.
- **Indicators.** `Availability` is time based: a store slot (default 1 minute) gets one verdict, bad if any sample in it was bad, else unobserved if any was, else good. `LatencyThreshold` is count based: the ratio of samples at or under a threshold (up to 1 minute). Raw readings are classified by `SloSamples` (readiness, health state, duration against a limit); any doubtful input becomes `unobserved`.
- **Store.** `ISloSampleStore` keeps counts per series and slot, never raw samples, so size does not grow with the sample rate. The in-memory store is bounded (256 series, 44640 slots per series, oldest dropped), refuses a bad identifier or an undefined outcome, and saturates its counts. A durable store is not built.
- **Unobserved.** `unobserved` is never good. It consumes budget like bad time and is counted apart. For availability, a slot with no sample after the first one ever recorded is unobserved (a sampler that dies burns budget); time before the first sample is not counted. For the latency kind an empty slot adds nothing (no traffic is not an outage), so a blind meter must record `unobserved` samples. A series with no sample in the window is `NoData`, which is never healthy and reports no budget left.
- **Window end.** The window ends at the last complete slot; the slot in progress counts for nothing until it closes.
- **Budget.** Consumed share is not-good units over `(1 - target)` times the units of the window: for availability the full window in slots (so a young series has used only what it has seen), for latency the samples it has. Remaining is `1 - consumed`, never below zero. All integer maths (128-bit intermediates).
- **Burn rate and alerts.** The burn rate over a window is its not-good share divided by the allowed share, in thousandths (1000 is exactly on budget). A rule fires only when the burn is at or above its threshold over both its short and its long window (a window with no sample never fires). Defaults, both over 1 hour and 6 hours: `slow_burn` warning at 2 times, `fast_burn` critical at 6 times. Settings may only tighten: the same two rule names and severities, a threshold no higher than the default and not below 1 times, a short window no longer than the default and at least a minute, a long window above the short one and no longer than the default.
- **Status.** `SloStatus` (identifiers and numbers only) has health `NoData`, `Ok`, `Warning` (a rule is firing) or `Breached` (budget used up, or a critical rule firing). `SloSnapshot` sorts by id, refuses duplicate ids and raises the evaluator's `SloWarningBurn` and `SloBreach` flags without ever lowering one. `SloReportAdapter` fills the existing `ReportSlo` input of 16.10, rounding down to permille so a report never overstates.
- **Not built.** Uptime figures recomputed from the journal (12.3, last point), a durable store, settings wiring (the `kodo.slo.*` keys exist, nothing reads them), alert routing of burn rules through the Responder, per-service SLOs from module manifests, and the public status page figures.

## 13. Prediction

The brief's requirement: threshold alerts, trend forecasting, learned per-install baselines and drift; deterministic rules first; optional server-side model explanations.

### 13.1 Rule order

1. **Deterministic rules** decide every state change. They are the only input that can make a critical state.
2. **Threshold alerts** are rules with a hysteresis band and a grace period (section 8.2).
3. **Trend forecasts** and **baselines and drift** are advisory. They can raise a non-critical `predicted` alert, which the Responder may show or use as a precondition for a runbook suggestion. A learned signal can raise a probe to amber at most. It never produces `failing` on its own.
4. **Model explanations** (13.5) are text only and cannot change any state.

### 13.2 Trend forecasting

- For each numeric series (disk free, memory, DB size, queue depth, p95 latency, error rate, journal growth), Kodo fits a damped trend (Holt linear with damping) over a sliding window (default 14 days, at least 72 hours of data before any forecast is shown).
- A forecast reports the time-to-threshold with a band (10th and 90th percentile from the residual distribution). The alert text states the band, not a point.
- Forecasts are recomputed hourly and stored with the model parameters, so they can be replayed.

### 13.3 Learned per-install baselines and drift

- A baseline is a robust location and scale per series, per hour-of-week bucket (median and MAD over the trailing 4 weeks). Buckets with fewer than 3 weeks of data are not used.
- Drift is detected with a two-sided CUSUM and a Page-Hinkley test on residuals against the baseline. A drift alert records the start time and the magnitude.
- Baselines are per install. Nothing is shared between installs, and Kodo does not learn from other companies' data, which keeps the Constellation and privacy rules intact.
- Baselines are reset by an explicit owner action, and every reset is audited.

### 13.4 Alert quality

- Alerts are grouped into incidents, deduplicated by fingerprint (probe id plus scope), and rate-limited per channel.
- Each alert carries its rule, its inputs, its grace and hysteresis state, and the forecast band where it is a prediction, so the reason is always visible.
- Alert fatigue is measured: the Monitor pane shows the count of alerts per week, the fraction acknowledged within 15 minutes and the fraction that were false positives (marked by the owner). The predictor's thresholds are tuned against this, by setting, never silently.

### 13.5 Model explanations (optional, OFF by default; ASK per company)

- When the operator enables it, the Predictor can ask the company's configured model, through the gateway (doc 05) and under the agent model policy, to explain an incident or a drift finding in plain language. Settings: `kodo.explain.mode` (`off`, `ask`, `auto`) and `kodo.explain.allow_auto` (false). Default `off`; `ask` shows a button in the Monitor pane.
- The input is the journal extract and the log lines for the window. Logs and traces contain untrusted content, so the explanation request taints its session under doc 15, and no tool access is given to the model. The request is read-only: no tools, no writes, no actions.
- The output is always labelled "unverified diagnosis" in the UI and in the webhook and API payloads. It is never used as the sole reason for a runbook step, and it cannot change a state.
- Redaction runs on the input before the gateway call, and it is deterministic, never model-based (brief: redaction is never model-dependent).
- A later on-device explanation path through the NPU (doc 31) is optional and off by default. It follows the same labelling and the same no-action rule.

### 13.6 Predictor implementation choices (package 11, PROPOSED 2026-11-19)

The pure core lives in `src/Bazaar.Kodo/Prediction`. It has no I/O, takes a `TimeProvider` or a time argument, and is advisory only (13.1). Doc 13.2 and 13.3 stay the target; these are the choices where the doc is silent or the first build is simpler. Nothing here is DECIDED.

- **Trend fit.** A robust Theil-Sen line (median of pairwise slopes) over at most 256 samples in the window, not damped Holt. The 10th and 90th percentile slopes give the time-to-threshold band (`earliest`, `latest`); a null `latest` means the shallow end never reaches the threshold. Holt with damping and residual-based bands stays the longer-term target of 13.2.
- **Confidence class.** `Unknown` (no forecast), `Low`, `Medium`, `High`, from the band width relative to the estimate (at most 0.5 is High, at most 1.5 is Medium, otherwise or unbounded Low). Only Medium and High can raise amber.
- **Defaults.** Window 14 days and minimum span 72 hours (13.2). Minimum samples 24, sample cap 256, stale after 24 hours, future skew 5 minutes, amber horizon 7 days, forecast cap 365 days, value magnitude cap 1e15. All PROPOSED, all operator settings once the registry carries them; shipped values are the conservative ones.
- **Insufficient or bad data is Unknown, never green.** NaN, infinity, out-of-range, out-of-order, equal-timestamp, future-stamped and stale samples all produce an Unknown result with a cause (`insufficient_data`, `invalid_sample`, `out_of_order_sample`, `future_sample`, `stale_data`). A rejected sample changes no stored state.
- **Already past the threshold** is reported as `Breached` and mapped to amber `forecast_threshold_soon`; the deterministic rules own the real state.
- **Baseline.** One entry per hour-of-week bucket (168, UTC) and week, latest wins, `TrailingWeeks` 4 and `MinWeeks` 3 (13.3). Median and MAD (scaled 1.4826) of earlier weeks only. Memory is at most 168 x 5 values. The scale has a floor of 5 percent of the median and an absolute floor of 1.0 in the series' unit (PROPOSED; set it per series for fractional series). A bucket below `MinWeeks` is warming up: Unknown with `baseline_warming_up`.
- **Drift.** A two-sided CUSUM on residuals in scale units, slack 0.5, alarm level 5, residual clamped to 4 so one outlier never alarms, statistic capped at 10 times the alarm level. The alarm records the start of the excursion and the magnitude, and clears by itself when the series returns. The Page-Hinkley test from 13.3 is not built yet. Drift is amber with `drift_up` or `drift_down`, never red and never a bare number (13.1). The owner's request for an amber or red drift result is held to amber because 13.1 says a learned signal raises amber at most.
- **Reset.** `BaselineDriftProbe.Reset` forgets everything learned. The caller writes the audit entry (13.3).
- **Memory.** `PredictionMemory` (version 1) is source-generated camelCase JSON with unknown members rejected and a 1 MiB parse cap. Restore validates sizes, ranges, order and duplicates, and on any failure the caller keeps or starts fresh state. Times are UTC ticks.

## 14. Monitor pane in every client

The brief's requirement: every client has a Monitor pane with status, live metrics, logs and traces, history time-travel with audit and update events, a Constellation-style system map, and role-gated controls (restart, isolate, roll back). Each client renders what the server computes (CLAUDE.md); no client computes health.

### 14.1 Pane contract

- The Monitor pane is a first-party pane module, `Bazaar.Client.Kodo`, registered through the pane registry (doc 41) like every other pane. Third-party modules can register sub-views of Monitor (for example a module's own metric page) through the same registry.
- Data comes from the Kodo read API and the `kodo` stream (section 15). The pane uses the snapshot-plus-revision pattern of doc 23, so reconnects re-fetch a snapshot and then apply deltas.
- The pane works offline: it shows the last snapshot with its age, marks the state `unobserved` after the budget (8.3), and never shows stale green.

### 14.2 Views

1. **Status.** Overall state word and colour, the 0 to 100 score with its caps called out, uptime and SLO remaining, open alerts, open incident (if any), the next scheduled self-check.
2. **Live metrics.** Series tiles (disk, memory, DB, queues, latency, error rate, gateway lanes, client counts) with sparklines, forecasts with bands and drift markers. Updates arrive on the stream at no more than 1 Hz per tile.
3. **Logs and traces.** Filterable log view and trace view, OpenTelemetry-backed (doc 23 pipeline). Filters by module, probe, severity, time and trace id. Log text is escaped for display and passed through the doc 23 scrubber on the server, never rendered as markup.
4. **History time-travel.** Scrub to any time in the retention window. The pane reconstructs the state at that time from the nearest snapshot and the journal, overlays audit events (admin and security, from doc 48 when written) and update events (doc 49), and shows what was live. Reconstruction is done by the server; the client only renders the result.
5. **System map.** A Constellation-style map of the installation: nodes are modules, environment parts (host, volumes, network paths, certificates, providers, lanes) and fleet clients; edges are declared dependencies and observed traffic. It uses the same renderer seam as the Constellation (`IConstellationRenderer`, doc 19), so Vulkan is primary and Skia through Avalonia is the fallback. Layout is computed by the server. Nodes carry the state colour and word, and selecting a node opens its probes.
6. **Controls.** Restart, isolate, roll back, run runbook, enter and exit incident mode, pause new automatic actions, acknowledge alerts. Each control is shown only when the viewer's role grants it (section 14.5) and each one routes through the Responder.
7. **Fleet.** Every client device, its version against the channel, its last seen time, its crash rate and its stream state. Crash reports are the deterministic captures of doc 41 (error codes, stack trace with the flagged line, logs, screenshot, device info, module versions), after on-device redaction.
8. **Audit and updates.** A filtered list of audit and update events, each with its hash-chain position and a Merkle proof button.

### 14.3 Per-client notes

| Client | Monitor pane behaviour |
|---|---|
| Windows (doc 28) | Full pane. Map, time-travel and charts at full density. Controls behind step-up |
| Linux (doc 30) | Full pane, same as Windows |
| Android (doc 29) | Full pane in a compact layout. Map is a list view with a node detail sheet. Controls require step-up. The doc 23 Android rule limits the Observatory to a read-only subset; Kodo's controls are a deliberate exception that the owner should confirm (section 23) |
| Terminal (Calcinato, doc 41) | Vim-style navigation. Status, logs, traces and controls are available. The system map is a text tree with the same node states. Keys follow section 14.4 |

Every client honours the same accessibility rules: keyboard reachable views, a screen reader label for every state word and chart summary, a text alternative for the map, and i18n strings for all words.

### 14.4 Keyboard and mnemonics (PROPOSED, subject to doc 41's registry)

- The pane is opened by a mnemonic from the client-wide command palette, with no Ctrl chords (brief). Proposed mnemonics: `kodo` opens the Monitor pane, `kstat` opens Status, `kmap` the system map, `klog` logs and traces, `ktime` time-travel, `kctl` controls, `kinc` incident mode, `krun` runbooks, `kfleet` the fleet view.
- Modules can register and remap their own mnemonics. Users can remap any Kodo mnemonic. The palette lists every Kodo action with its mnemonic and its role requirement.
- Terminal (Calcinato) uses the same mnemonics as commands and Vim-style motions inside panes.

### 14.5 Roles and controls

- Permissions are custom-role grants (brief). Kodo's permissions: `kodo.view.status`, `kodo.view.logs`, `kodo.view.traces`, `kodo.view.fleet`, `kodo.view.audit`, `kodo.control.restart`, `kodo.control.isolate`, `kodo.control.rollback`, `kodo.runbook.run`, `kodo.runbook.approve`, `kodo.incident.enter`, `kodo.incident.exit`, `kodo.failover.approve`, `kodo.chaos.enable`, `kodo.webhook.manage`, `kodo.export`, `kodo.settings.kodo`.
- Each control is a high-impact action under doc 15: approval tier, audit entry, idempotency key, and a dry-run preview where the action has one. Restart, isolate and roll back are step-up required by default. Owner decision (2026-11-19): approval stays the default, and a per-server admin setting, `kodo.recover.unattended` (section 20), may allow only the unattended host restart and slot switch. Isolate and rollback always need step-up; the setting cannot list them.
- Viewing logs and traces is separately granted because logs can contain personal data. Defaults grant the owner all of these and grant no other role any of them.

### 14.6 Monitor pane core choices (package 16, first slice, PROPOSED 2026-11-19)

Built in `src/Bazaar.Client.Kodo`: the Status and Fleet views. Choices not settled elsewhere, all PROPOSED:

- **One stream tracker (reducer removed).** The pane uses the shared `Live.KodoStreamTracker` and no other reducer. It applies a snapshot, then deltas by revision (the next revision only); a skipped revision is never applied and asks for a snapshot; `unobserved` is derived from the heartbeat (30 s) and snapshot-freshness (60 s) budgets. `KodoStreamSession` is a thin wrapper in `Bazaar.Client.Kodo` that adds what the panes need around the tracker: `NeedsSnapshot`, `SnapshotRequested` (raised once), `Changed`, `Reconnected()`, the last known score, and holding deltas back until a snapshot arrives after a gap or a reconnect. A snapshot older than the held revision is ignored. The wire snapshot carries no controls, so the Status view copies them from the read API status (`KodoStatusFeed.LastGood.Controls`) into `KodoStatusPaneViewModel.Controls`.
- **Unobserved.** Derived with `KodoUnobserved.Derive` from the client's own receive time (any event counts). While a snapshot is needed the display is `unobserved` too, so a stale green is never shown. Offline age is the silence since the last event.
- **Status view.** State word and a theme token per state (`Bazaar.brush.kodo.state.<state>`, falling back to the primary text colour; the theme file should define them). Score is clamped to 0 to 100 and shown as "last known" when unobserved. Open alerts and score caps are not in the stream snapshot, so they arrive through `KodoStatusExtras` from the read API `GET /status`; absent means "not reported", never zero. An open incident is `frozen` with the underlying state in the headline. Component counts come from the snapshot.
- **Fleet view.** There is no fleet DTO in `Bazaar.Contracts` yet, so `KodoFleetDeviceInput` is a client-side seam that moves to Contracts with the server `GET /fleet`. Version standing ignores build metadata and ranks a release above a pre-release of the same core. Default order is "needs attention first" (stream state, crash rate, behind on version, oldest last seen), ending in device name then id so the order is stable.
- **Controls.** Shown only when the injected `IKodoPermissions` grants the matching permission (14.5) and the server listed the control; the server's enabled flag and reason are copied. Clicking raises `ControlRequested`; nothing is sent from the client yet. Roles are not implemented here.
- **Mnemonics.** `KodoMnemonics.Proposed` lists `kodo`, `kstat` and `kfleet` as rows; heads turn them into `Bazaar.Ex` assignments. This project does not reference the registry.
- **Pane registry.** The views are plain controls built from view models; they are not yet registered with `PaneRegistry` (the pane model there is server-descriptor driven, and Monitor is stream driven). A head hosts them and calls `Refresh()` on `KodoStreamSession.Changed` and on a timer.

### 14.7 Fleet snapshot wire shape (PROPOSED)

- **Fleet wire shape.** `KodoFleetSnapshotDto` (schema version, revision, `asOf`, one `KodoServerStatusDto` per server: id, display name, state, underlying state, score, `asOf`, version, open alerts, incident flag) in `Bazaar.Contracts/Kodo`, source-generated JSON, camelCase, nulls omitted, unknown members skipped. Each row carries its own `asOf`, so the client marks a single stale server `unobserved` without hiding the rest.

## 15. API

### 15.1 Read API

Authenticated JSON over the doc 02 HTTP surface, under `/api/kodo/v1/`. Responses carry `schema`, `revision` and `asOf`. Lists are paged with a cursor.

| Method and path | Purpose |
|---|---|
| `GET /status` | Current state, score with caps and inputs, SLO remaining, open incidents |
| `GET /probes` and `GET /probes/{id}` | Probe catalogue and last results with timestamps and reasons |
| `GET /modules` and `GET /modules/{id}/health` | Module health contract results, dependencies and self-test history |
| `GET /metrics/series/{id}?from&to&step` | Series data and forecasts with bands |
| `GET /history?at=...` | Reconstructed state at a time (time-travel) |
| `GET /events?from&to&kind` | Journal events, filtered |
| `GET /journal/proof/{recordId}` | Merkle proof of a record against its checkpoint |
| `GET /journal/checkpoints` | Signed checkpoints and their anchors |
| `GET /alerts`, `POST /alerts/{id}/ack` | Alerts and acknowledgement |
| `GET /incidents`, `GET /incidents/{id}` | Incident records, timelines and summaries |
| `GET /runbooks`, `POST /runbooks/{id}/runs` | Runbooks and runs (approval-gated) |
| `POST /controls/{action}` | Restart, isolate, roll back, pause, incident enter and exit. Requires an idempotency key and, for high-impact actions, a step-up token |
| `GET /fleet`, `GET /fleet/{device}` | Clients, versions, crash rates |
| `GET /webhooks`, `POST /webhooks`, `DELETE /webhooks/{id}` | Webhook management |
| `GET /export?format=...` | Health reports and evidence exports, role-gated, audited |
| `GET /settings/kodo` | Kodo settings (the registry rules of doc 03 apply) |

Errors use one shape: `code`, `message`, `retryable` and `correlationId`. No endpoint returns secrets, key material, raw prompts or unredacted logs.

### 15.2 Live streams

- A `kodo` stream on the existing `/ws` connection, registered in doc 02's stream registry (built in `Bazaar.Server.Kodo`, PROPOSED, see 5.4; only the `status` topic). It sends a snapshot on subscribe and then revisioned deltas. Any revision gap triggers a snapshot re-fetch, as in doc 23's presence stream.
- Topics: `status`, `alerts`, `probes.<category>`, `incidents`, `fleet`, `logs` (filtered server-side, subscription-scoped, rate-limited), `traces` (sampled) and `controls` (results only).
- Delivery budget: a state change reaches connected clients within 1 second at p95 on a healthy server.

### 15.3 Webhooks

- Signed with HMAC-SHA256 over the timestamp and body, with a replay window of 5 minutes. Headers carry the key id, so keys rotate without downtime (two keys valid during rotation).
- Retries with exponential backoff and jitter (up to 24 hours), then dead-letter with a visible queue and a replay action. Each webhook has its own circuit breaker (7.4).
- Event types: `kodo.state_changed`, `kodo.alert`, `kodo.incident.opened`, `kodo.incident.updated`, `kodo.incident.closed`, `kodo.runbook.finished`, `kodo.report.ready`, `kodo.tamper`. Payloads are small and identifiers only; the consumer calls the read API for detail.
- Destinations must be on the operator's allow list. Webhook URLs are secrets (doc 14) and are never logged in full.

### 15.4 Metrics and tracing export

- **Prometheus** exposition at `/metrics` on a separate listener, bound to loopback by default. Non-loopback binds need the same owner approval as the status listener (15.5) and a bearer token. Cardinality limits apply: probe ids and module ids are labels, agent ids and GC ids never are (doc 23's allow-list rule).
- **OpenTelemetry** export of Kodo's derived health as metrics and logs over OTLP, using the doc 23 pipeline and its loopback guard. Kodo's own spans (probe runs, evaluator passes, control actions) follow the same allow-list.

### 15.5 Status page listener and the bind floor

- The status listener is doc 03's, bound to loopback by default. The `/status` route format is doc 02's. The public status page (12.4) and any non-loopback exposure need owner approval recorded through the settings registry and the Tier 2 approval of doc 15. This follows doc 03's floor: a non-loopback bind is refused unless `Bazaar:Host:Status:OwnerApprovedWideBind` is true.
- A browser view of the status page is allowed by CLAUDE.md as a read-only server status page. Kodo adds no interactive browser surface. The narrow passkey sign-in page of doc 32 is unrelated and stays separate.

### 15.6 Read API core choices (package 9, PROPOSED 2026-11-18)

The pure core lives in `src/Bazaar.Kodo/Api`: the snapshot builder and the `kodo` stream writer. The wire types (`KodoStatusSnapshotDto`, `KodoComponentDto`, `KodoStreamEventDto`, `KodoStateChangeDto`), their JSON context and codec, and the client-side `unobserved` derivation (`KodoUnobserved`, `KodoDisplayState`) live in `Bazaar.Contracts` under `Kodo`, as section 4 requires, so `Bazaar.Client.Kodo` can use them without the server module. The status payload is the existing `KodoStatusDto` (no second status type); the snapshot wraps it with `revision`, `schemaVersion`, the journal head and the components. JSON is source-generated `System.Text.Json` with nulls omitted, so `underlyingState` is present only while `frozen`. It has no HTTP route, socket or `/ws` registration. Every choice below is PROPOSED because this section or section 15 leaves it open. Owner to ratify.

- **Stream numbering is `revision`, not a sequence.** The package brief said sequence numbers. Doc 02's registry says state streams carry a `revision`, and this section already speaks of "revisioned deltas". The `kodo` stream is state, so each delta (`state_changed`, `heartbeat`) takes the next revision: one counter per stream, rising by exactly one, gap-free. A snapshot is not a delta. The writer builds it under its lock with the current revision and does not raise the counter, so a snapshot sent to one subscriber on connect leaves no gap for the others, and a concurrent publish cannot make the snapshot fail or carry a stale number. Deltas after it apply on top.
- **Event types** on the stream are `state_changed`, `snapshot` and `heartbeat`. The webhook names of 15.3 keep the `kodo.` prefix. The stream does not need it.
- **Only displayed-state changes emit `state_changed`.** A change of the underlying state while the display stays `frozen` writes no event. The underlying state is still carried in the snapshot and in each state-change payload.
- **Revisions are not persisted by the writer.** A restarted Kodo starts from the revision its caller supplies. Clients treat a revision lower than the one they hold as a reset and re-fetch the snapshot. Whether the counter is persisted is open.
- **One silence budget.** Section 8.3 names a freshness budget but gives no value. A snapshot arrives only on subscribe or after a gap, so measuring freshness from the last snapshot would turn a healthy connected client `unobserved` after a minute. PROPOSED: every event (snapshot, state change or heartbeat) refreshes freshness, and the only budget is the 30-second silence budget. It is a constructor value in this slice and becomes a settings key (for example `kodo.stream.heartbeat_budget_s`) when added to section 20.
- **The client clock decides `unobserved`.** The budget is measured from the time the client received the last event, so server clock skew does not matter. A receive time later than the client's `now` counts as age zero.
- **Edges.** Exactly at a budget is still observed. One tick past it is `unobserved`. A client that has never received a state or an event shows `unobserved`.
- **Heartbeat cadence** is not set in section 8.3 or 15.2. PROPOSED: the server sends a heartbeat every 10 seconds, a third of the budget, so one lost frame does not trip it. Not built in this slice.
- **Snapshot shape.** `schemaVersion` is the schema field and the status's `asOf` is the `asOf` of section 15.1. Components are sorted by id. A green component carries no cause. A component that is not green needs a cause. The journal head is the lowercase hex of its 32-byte hash, and null while the journal has no records.
- **Score caps are enforced at the builder.** A score above the section 12.2 caps is rejected, not clamped: critical red 49, integrity or contract failure 10, critical unknown 70, and an underlying `failing` state 49. The builder takes the observation the score was computed from, and `KodoScore.CapFor` is the one cap rule shared with `KodoScore.Compute`.
- **Wire strings.** The wire carries strings from `KodoWireNames`, never C# member names. Probe statuses are `green`, `amber`, `red` and `unknown`. `unobserved` is a display value only.

## 16. Recovery: alerts, runbooks and incident mode

### 16.1 Alerts

- **Channels.** In-client (every client), content-free push wake-ups (doc 29's push provider interface; content is fetched over the authenticated connection), outbound email to the owner's external address (doc 22, external only), webhooks (15.3), an optional GC post in a GC the owner chooses (events serve GCs, they do not replace them), and the scheduled health report (16.7).
- **Routing.** Alerts route by severity and by the role that owns the category. Routing is a setting. Every channel has a rate limit and a breaker.
- **Acknowledgement and escalation.** An alert that is not acknowledged within its escalation window (default 15 minutes for critical) escalates to the next role in its route, then to the owner's devices. Escalation is recorded in the journal.

### 16.2 Runbooks

- A runbook is a signed, declarative file: id, title, preconditions, steps, verify step, rollback step, required permissions, approval tier and estimated duration. Runbooks are part of the signed runtime slot for Kodo's own and part of the module package for module runbooks.
- Each step has a kind (`check`, `action`, `verify`, `notify`, `wait`), an idempotency key and a timeout. Steps run in order and their results are journalled before the next step starts.
- **One-click fixes** are runbooks with a single action step, a dry-run preview and an approval tier. The preview shows exactly what changes.
- High-impact steps go through doc 15's approval gates. Taint rules apply: if the runbook's inputs came from untrusted content (for example a log line used as a parameter), the run needs owner approval, even when the runbook is otherwise auto.
- Runbook runs are replayable: the journal holds the inputs, the step results and the approvals, so a run can be re-displayed and audited, though not re-executed.

### 16.3 Incident mode

Incident mode is the brief's INCIDENT MODE. Entering it does six things, all audited.

1. **Incident record.** A record with id, severity, trigger, opener, opening time, the affected categories, linked alerts, linked runbook runs and a running timeline. Incident ids are sequential per install and never reused.
2. **Freeze risky changes.** Automatic updates (doc 49) are held. Configuration changes outside the incident allowlist are refused. Agent high-impact actions are paused through doc 15's kill switch scope "incident" (agents keep working in read-only and planning modes unless the owner also stops them). Runbooks other than the incident's own are blocked. Module installs are blocked. Chaos mode (17.3) cannot be enabled.
3. **Snapshot evidence.** Into `kodo-evidence`: the journal segment from the start of the trigger window, the current and previous 24 hours of logs, configuration with secrets removed, runtime slot hashes, database state summary (counts and versions, no content), probe results and the fleet list. Evidence is hashed and chained into the journal and is subject to legal hold (doc 33).
4. **Notify roles.** The incident's notification plan (per role channels, 16.1) is sent. The notification content is the incident's title, severity and link. No secrets, no content.
5. **Guided recovery.** The Monitor pane shows an ordered checklist from the incident's runbooks, with the verify step for each. Recovery steps are suggestions until a role with `kodo.runbook.run` runs them.
6. **Audit.** Every step above is an audit event with its chain position, and every control taken during the incident is linked to the incident.

Exit: the incident is closed when all probes that opened it are green for the stability window and a role with `kodo.incident.exit` confirms, with step-up. The freeze lifts only at exit. A post-incident summary is drafted from the timeline (16.4).

**Entry.** Manual entry needs `kodo.incident.enter`. Automatic entry happens for: a critical probe red for longer than its incident grace (default 10 minutes), any integrity failure (6.4), a split-brain signal (7.6), or a replication break on a configured standby. Automatic entry is the default for those four, and can be tightened by setting, never loosened below the floor.

**Exit cannot be automatic.** A stuck incident can be exited only by a human with step-up, so the freeze cannot be defeated by the failure it is holding back.

### 16.4 Post-incident summaries

- Drafted from the incident timeline, the alerts, the runbook runs and the score history. The draft says what is known and what is inferred, and it is labelled "draft".
- A role with `kodo.incident.exit` publishes it. Published summaries are immutable; a correction is a new revision with its own audit entry.
- Summaries can use a model for prose (section 13.5 rules apply: labelled, read-only, no tools). The facts in the summary come from the timeline, and a fact-check pass compares each stated number with the journal before the draft can be published.

### 16.5 Recovery actions by category (examples)

| Trigger | Runbook | Approval | Verify |
|---|---|---|---|
| Host not ready, service stopped | Restart host through the sentinel | Approval by default (14.5); unattended only when `kodo.recover.unattended` lists `host_restart` | `host.ready` green within 2 minutes |
| Disk near full on data volume | Rotate logs, prune spill past its window, offer Vault archive | Approval | Free space above threshold |
| Replication broken (7.6) | Re-seed standby from the latest base backup | Approval, step-up | Replication status green |
| Slot signature failure | Switch to the other verified slot | Approval by default (14.5); unattended only when `kodo.recover.unattended` lists `slot_switch` | `kodo.self.signature_verify` green on the new slot |
| New module fails health contract | Disable module, keep data, open an incident if it was in use | Approval | Module state and dependents |
| Certificate within 7 days of expiry | Renew through the configured method | Approval | `certs.server` green |
| Backup stale | Run backup now | Auto if the window is set to auto | `db.backup_freshness` green |
| Model lane quota exhausted | Route to the configured fallback lane (doc 05 policy) | Auto under the agent policy | Lane state |

### 16.6 Release rings and auto-halt

The brief's later addition 1 (2026-11-13, DECIDED): release channels are stable, beta, nightly and LTS, plus staged rings (canary, early, broad) tied to Kodo health signals. A bad release auto-halts. Doc 49 owns the channel and ring policy. Kodo owns the signals and the halt.

- **Rings.** Each release reaches its rings in order: canary (a small set of devices and, where the operator has one, a standby or a sandbox copy), early, then broad. Ring membership and sizes are settings, with defaults of canary 1 to 5 percent of the fleet and early 25 percent. A device is in exactly one ring for a given release.
- **Cohort signals.** For each ring, Kodo compares the new release's cohort with the baseline cohort (the same channel on the previous release, or the previous ring at the same time). Signals: crash rate per 1,000 sessions, error-code rate from the client capture (doc 41), stream failure rate, module health contract failures on the new version, server SLO burn while the ring is live, and the new version's self-test results.
- **Halt rule.** A ring halts automatically when a signal exceeds its threshold relative to baseline for a window (defaults: crash rate 2 times baseline with at least 20 events, any new module contract failure in a critical module, or a critical probe red on a server running the new version). The threshold and the window are settings with floors. The halt is recorded as a journal event and an audit event, and an alert goes to the owner's devices and the release route.
- **Halting is always automatic and safe.** Halting stops further rollout; it never changes an installed release. Resuming a halted ring needs a role with `kodo.runbook.approve` and step-up. A halt that is resumed is audited with the signals that were shown.
- **Rollback is a separate choice.** Clients on a halted ring may be rolled back to the previous slot (6.3) only by a runbook, which is approval-gated, or automatically if the operator sets `kodo.release.auto_rollback_canary` (default false, because a canary rollback without review can hide the cause).
- **Server releases.** A server on the new version that goes `failing` in its first probation window triggers the sentinel's rollback of the server slot where the operator has configured a previous release to keep (doc 49 owns the keep policy).
- **Channels.** Nightly and beta follow the same rings and halts, with lower thresholds for halts (more sensitive) and a shorter probation. LTS rings are slower by policy and never skip canary.
- **No halt by silence.** A cohort with too few devices or sessions for the threshold to be meaningful is reported as `insufficient data`, not as healthy. The ring stays where it is until the owner acts.

### 16.7 Scheduled health reports

- Daily, weekly and monthly reports: score, state history, SLO remaining, incidents, alerts per week and false-positive rate, forecasts with bands, changes in the fleet and the updates applied.
- Delivered by the chosen channels (16.1). A report is a generated, dated, signed document, and its numbers come from the journal.

### 16.8 Responder implementation choices (package 12, PROPOSED 2026-11-19)

The pure core lives in `src/Bazaar.Kodo/Responder`. It does no I/O, reads time only from a `TimeProvider`, starts no threads and makes no network calls. Channels, step runners, the journal, the state store and the approval gate are interfaces (`IAlertChannel`, `IRunbookActionRunner`, `IRunbookJournal`, `IRunbookStateStore`, `IApprovalGate`). Doc 46 sections 13.4, 16.1 and 16.2 stay the target; these are the choices where the doc is silent or two parts disagree. Nothing here is DECIDED.

- **Severity.** Taken from the state moved into: failing and frozen are critical, degraded is a warning, healthy and recovering are informational. Moving into healthy is a resolution. A flap raises one warning (8.2).
- **Dedupe, grouping, rate limit.** Same fingerprint and same severity inside 30 minutes is a duplicate; a changed severity is news and is delivered. Alerts of one category inside 60 seconds of the first are grouped, with one summary alert when the window ends (the first alert and any higher-severity alert still go out at once). Each channel takes 6 deliveries per 10 minutes, and critical alerts count apart with a budget of 20. Every window end is exclusive: an event exactly at the boundary starts a new window.
- **Quiet rule.** A daily UTC window, off by default. It keeps info and warning alerts in the client and the report only. Critical alerts are never quieted.
- **Breakers.** One breaker per channel (7.4's `CircuitBreaker`, 5 failures, 60 seconds). An open breaker skips the channel and the skip is returned as a suppression, never silent.
- **Content-free payloads.** A push wake-up carries only an alert id. Email carries the id, severity and rule. Webhook, GC post, in-client and report carry identifiers (category, fingerprint, up to 8 more, all matching the 15.3 identifier shape). A webhook body is built by the existing `KodoWebhookEnvelopeBuilder`; signing, allow list and retry stay in `Export`.
- **Escalation.** Each category's first segment maps to an owning role (default `owner`). The chain is that role, then `owner`, then `owner-devices`. A critical alert not acknowledged within 15 minutes (setting `kodo.alert.escalation_min.critical`; floor 1 minute, ceiling 24 hours) moves one step up the chain. The caller journals each escalation.
- **Tracking limit.** At most 1,024 fingerprints and groups are tracked. When the table is full the router delivers instead of suppressing.
- **Runbooks.** A runbook enters the catalogue only with a valid signature over its canonical bytes (fixed key order, sorted parameters) and a valid definition. An id and version never holds two bodies. A start is refused for an unknown runbook, a digest the caller did not expect, a missing control name, a repeated run id, tainted or malformed input, or a frozen or recovering state (8.1). The step idempotency key is derived as `run id / step id`, not stored in the file.
- **Approval rule.** A run is unattended only when it is low risk, accepts no approval, is named in `RunbookOptions.AutoRunbookIds` (empty by default), has untainted inputs and starts while the state is healthy. Every other run needs an explicit `Approved` result for each action step, including rollback steps. A pending result pauses the run, a denial ends it (after rolling back finished actions), and a timeout, an error, a mismatched request digest or a missing step-up stops it as needs-human. The executor never approves on timeout; it stops waiting after 4 hours.
- **Fail-safe.** Journal first, then persist, then act. Any journal or state write failure stops the run as needs-human. An action that was started but has no recorded result is never run again after a restart. A timeout, an exception or a malformed answer from an action is an unknown outcome and stops the run; the same from a read-only step is retried within bounds (3 attempts at most, 5 second delay doubling to 5 minutes). Verify failure and action failure roll back when a rollback is defined; with none defined the run stops for a human. A failed notify step is recorded and does not undo a good fix.
- **Conflict: restart.** 16.5 lists "restart host through the sentinel" and "switch slot" as auto with no step-up, and 14.5 makes restart, isolate and roll back step-up by default. The shipped fixes follow 14.5. The owner decides whether either may be unattended.
- **One-click fixes.** The eight actions of 16.5 are runbook definitions in `OneClickFixes` with a single action, a verify step, a precheck and, where reversible, a rollback. They carry no signature, so none can run until the release pipeline signs it. The control is a name only (14.5); nothing here builds roles or approvals. The probe ids of the triggers follow the doc's names and some probes do not exist yet.
- **State record.** `RunbookRunState` (version 1) is source-generated camelCase JSON with enums as words, unknown members rejected and a 256 KiB parse cap. On any parse or range failure the caller keeps the stored file and treats the run as needs-human.

### 16.9 Incident mode implementation choices (package 13, PROPOSED 2026-11-19)

The pure core lives in `src/Bazaar.Kodo/Incidents`. Like the Responder it does no I/O and reads time only from a `TimeProvider`. The store, journal, freeze sink, evidence sources, redactor and sink, approval gate and alert channels are interfaces. Section 16.3 and 16.4 stay the target; these are the choices where the doc is silent. Nothing here is DECIDED.

- **Reuse.** Notifications go through the Responder's `AlertRouter` and `AlertDispatcher` (one `KodoAlert` per event, category `kodo.incident`, so dedupe, rate limits, breakers and escalation are the Responder's). Guided recovery starts runbooks through `RunbookExecutor` and maps causes with `OneClickFixes` and the signed `RunbookCatalogue`. Enter and exit use `IApprovalGate` and the `KodoControls` names. Evidence entries chain with `JournalChain.ComputeHash`, and the summary verifies the journal with `JournalChain.Verify`. The evaluator already shows `Frozen` when its observation says an incident is active; `IncidentFreeze.Observe` sets that from the incident record.
- **Phases.** `opened`, `frozen`, `investigating`, `recovering`, `resolved`, `closed`. Moves: opened to frozen; frozen to investigating; investigating to recovering or resolved; recovering to investigating or resolved; resolved to investigating (the probes went red again) or closed. Nothing leaves closed. The freeze holds in every phase except closed, so resolving does not lift it (16.3: the freeze lifts only at exit). Each transition records the actor, the time and a reason code (an identifier, never free text) and, for exit, the approval id.
- **Entry.** Manual entry needs `kodo.incident.enter` and an `Approved` gate answer (step-up is an option, default off; 16.3 asks for step-up on exit). The four automatic triggers (critical probe red past its grace, integrity failure, split-brain, replication break) need neither, because entering only tightens; they are always critical and recorded as actor `kodo`. The grace has a floor of 1 minute and a ceiling of the doc's 10 minutes: a setting can tighten it, never loosen it. One incident may be active at a time; a second request returns the first. Incident ids are `inc-` and a six-digit number from the store's counter, taken before the record is saved, so a number is never reused even when a save fails.
- **Exit.** Needs `kodo.incident.exit` and an `Approved` gate answer with a matching digest and step-up. A pending, denied, timed-out, mismatched or failed answer leaves the incident open and frozen, and time alone never closes it. Resolving needs the probes green for the stability window (default 5 minutes, floor 1 minute). A reopened investigation is allowed from resolved. The timeline holds 128 transitions; the last two are kept for resolving and closing, so a full timeline can still be closed.
- **Freeze.** `IncidentFreeze.Check` refuses automatic updates, agent high-impact actions (doc 15 kill switch scope `incident`), module installs, chaos mode and automatic runbooks; config changes pass only for keys on an operator allowlist (empty by default); a person may start a runbook only when it is in the incident's plan. The sink that holds updates and flips the kill switch is `IIncidentFreezeSink`; its apply and release are idempotent. Opening journals and saves the record first, then applies the freeze, then seals the evidence, then moves to frozen. If the sink fails the incident stays in `opened` (still shown as frozen), and `RecoverAsync` finishes it after a restart. A close whose release failed is finished the same way.
- **Evidence.** One source per kind, collected one at a time in a fixed kind order (journal segment, logs, configuration, slot hashes, database summary, probe results, fleet list), each with a 30 second timeout. Every part goes through a required `IEvidenceRedactor` (no pass-through default; null drops the part), is refused if it holds a PEM private key block, is cut to 1 MiB, stored, and hashed with SHA-256. At most 32 entries and 16 MiB in all; entries past the caps are skipped or counted as omitted, never silently stored. Each manifest entry chains to the one before and the root also covers the omitted count, so a changed, removed, reordered or hidden entry changes the root. The journal records the root. Logs cover the trigger window (default 1 hour before the opening time); the "current and previous 24 hours" of 16.3 is the log source's own choice. Anchoring the root externally and the legal hold (doc 33) are not built.
- **Guided recovery.** The plan is made at opening and saved: the trigger floor (integrity to `fix.slot_switch`, replication break to `fix.replication_reseed`, split-brain to none because fencing is a human decision), then the shipped fixes for the cause's probe id, then the operator's own list. A runbook without a signed copy in the catalogue is listed as not available and cannot run. Starting one needs the runbook's own control and the Responder's checks. The Responder refuses starts while the displayed state is frozen (16.8), so the driver passes the underlying state, with healthy and degraded counted as failing, so a run during an incident is never unattended. Starting a run moves investigating to recovering; a recovering incident whose runs all ended without success goes back to investigating. The run is linked after the Responder starts it; if linking fails the run id is returned for the caller.
- **Notifications.** Opened (the incident's severity), resolved (a resolution) and closed (informational) go through the router. The body is identifiers only: the incident id is the link, and the title, severity word and phase are fetched over the authenticated connection (16.3 item 4 says title, severity and link; a free-text title never rides in an alert, to keep 15.3's rule). A notification failure never undoes or blocks a change.
- **Summary.** `IncidentSummaryBuilder` makes a deterministic draft from the incident record, the journal records in the window (from 1 hour before opening to the close or the draft time) and the runbook journal entries. Entries sort by time, then source, then input order; at most 500, with transitions and runbook entries always kept. Journal payloads are never shown, only their length and a hash prefix. Facts (counts, chain validity, evidence root) are counted from the records, durations are labelled derived, and the inferred section is empty because the builder uses no model text. `FactCheck` recounts every number. The plain-text rendering escapes backslashes, line breaks, tabs, control characters, bidirectional controls and separators, cuts values at 160 characters and ends lines with LF. Publishing, immutability and revisions (16.4) are not built; a summary here is always a draft with a revision number, and a model-written prose pass (13.5) is left to the gateway.
- **State record.** `IncidentRecord` (version 1) is source-generated camelCase JSON with enums as words, unknown members rejected, a 256 KiB parse cap, and checks that the id matches the number and the transitions follow the lifecycle table and end in the current phase. On any failure the caller keeps the stored file and treats the incident as unreadable.

### 16.10 Scheduled report core choices (PROPOSED)

The pure core lives in `src/Bazaar.Kodo/Reports` (namespace `Bazaar.Kodo.Reports`). It reads no clock (a `TimeProvider` or an explicit `now`), does no I/O and has no concrete sink. It builds on 16.7 and 12; the choices below are where those sections are silent. Nothing here is DECIDED.

- **Cadence.** Daily and weekly only; monthly is not built. A schedule is a local time of day (whole minutes), a day of the week for weekly, and the zone as explicit UTC offset rules (`ReportOffsetRule`, strictly increasing, at most 64, each from -14 to +14 hours). There is no zone database in the core; the host supplies the rules. A local time skipped by a forward clock change is due at the moment the clock jumps; a local time that happens twice is due at its first occurrence.
- **Due and catch-up.** Next and previous due are pure functions of the schedule and an instant. The state saved between runs is the last handled due instant. A new schedule is armed at the current time, so no old report is built. Missed slots are caught up oldest first, bounded: at most 3 per run (1 to 14) and only inside a 31-day window (1 to 92 days); older slots are skipped and counted, never silent.
- **Report content.** A `HealthReport` is built from the status snapshot, score samples, state changes, SLO standings (permille, no floating point), incident facts and alert counts. It holds identifiers, numbers and times only: no model text and no free text. An identifier that does not match the identifier shape is left out and counted in `Omitted`, a cause that does not match becomes `redacted`, and each list is capped at 256. The same inputs and generation time give the same bytes (source-generated camelCase JSON, unknown members rejected) and the same SHA-256 digest. The report id is the definition id and the period end.
- **Not in the first slice.** Forecasts with bands, false-positive rate, fleet changes, applied updates and the monthly cadence need inputs that do not exist yet. Signing is not built: the digest is what a signer would sign, so "signed document" (16.7) stays open until the Identity Service key use is settled.
- **Run and journal.** For each due slot the runner gathers inputs through `IReportInputSource`, builds the report, offers it to every `IReportSink` (30 seconds each, a throw, a decline or a timeout is recorded) and appends one `report.run` record built with `JournalChain.Create` through `IReportJournal`. A failed append leaves the chain where it was. The record holds the definition id, slot, outcome word, digest and per-sink outcomes, never report content.
- **Fail-safe.** `RunDueAsync` never throws into the host except for the caller's own cancellation. An input failure keeps the slot for the next run and stops later slots, so order holds; a build failure (inputs that cannot form a report) closes the slot, since a retry would fail the same way; a slot that no sink accepted is closed and not retried by the runner, and the sinks own their retries. An overlapping call returns a `Busy` result instead of running twice.

### 16.11 Release ring core choices (PROPOSED 2026-11-22)

The pure core lives in `src/Bazaar.Kodo/Rings`. It does no I/O, reads time only from a `TimeProvider`, starts no threads and stores nothing. Signal collection, the journal, alert routing, the doc 49 channel policy and the doc 15 approval gate are the caller's. Section 16.6 stays the target; these are the choices where it is silent. Nothing here is DECIDED.

- **Membership.** A device's ring is `SHA-256(release id, newline, device id)`, first 8 bytes as a number, modulo 10,000. Buckets below the canary share are canary, the next early-share buckets are early, the rest broad. Nothing is stored, a device is in exactly one ring, and a new release reshuffles the fleet. Sizes are in basis points: canary 300 (3 percent, inside the doc's 1 to 5), early 2,500. A setting may not take canary above 10 percent or canary plus early above 60 percent. When a non-empty fleet has no canary by chance, the lowest-hash device joins it.
- **Signals.** A cohort record carries counts for one window: devices, sessions, crashes, error-code events, stream attempts and failures, module contract failures (module id, code, critical flag), the server SLO burn rate, self-test counts and the critical probes red on servers running the version. Rates are per 1,000 sessions (stream failures per 1,000 attempts).
- **Halt rules.** Immediate, on one observation: a critical-module contract failure not present in the baseline, a critical probe red on a server running the new version, any failed self-test, an SLO burn rate of 6 or more (PROPOSED; 6 is the fast-burn level of 12.3). Rate rules, over a full observation window and against the baseline: crash rate at 2 times the baseline with at least 20 crashes (the documented defaults), error-code rate at 2 times with at least 50 events, stream failure rate at 2 times with at least 20 failures (the last two PROPOSED). The baseline never counts as lower than a floor (crash 1, error 5, stream 10 per 1,000), so a spotless baseline does not make one crash a halt. A rate at or above the threshold trips it.
- **Floors.** The documented defaults are the loosest values. A setting may lower a multiple, a minimum event count (not below 3, or 5 for errors), the SLO burn level, or the observation window (default 60 minutes, not below 10), and may raise the minimum devices (3) and sessions (500). A looser value is refused at construction.
- **Insufficient data.** A cohort whose window is short, with too few devices or sessions, or without a usable baseline (missing, from the same release, or too small) is `insufficient data` unless a rule tripped, and then it halts. Malformed counts, a window that began before the ring went live or was resumed, and a ring that is not live are also insufficient. Insufficient data never clears a ring and never halts it; the ring stays until the owner acts. Small fleets therefore may stay insufficient for a long time, which is intended.
- **Halt decision.** Identifier-only: release id, channel, ring, decision time, tripped rules with numbers, at most 8 module ids and 8 probe ids, counts, and a redaction count. A value that does not match the identifier shapes of 15.3 is replaced or dropped and counted, so a halt can always be written. The digest is SHA-256 of canonical bytes (fixed key order, no id, no digest); the decision id is derived from it. A decision names no rollback: halting changes the halted ring's status and nothing else, and later rings cannot start while any ring is halted.
- **Progression.** A ring starts only when each earlier ring is cleared. A ring clears when it has been live for the channel's probation and the latest verdict is healthy. Canary may be skipped only where a channel profile allows it, never for LTS, and no shipped profile allows it.
- **Resume.** Reuses the responder's `IApprovalGate`: the request names the control `kodo.runbook.approve`, asks for step-up, is bound to the decision digest and counts only as an explicit approval with a matching digest and satisfied step-up. A resumed ring goes live again with probation restarting, and data from before the resume does not count. The caller journals the answer with the decision, so a resume is audited with the signals that were shown.
- **Channel profiles.** Stable keeps the floors and a 24 hour probation. LTS keeps the floors, probation 72 hours and no canary skip. Beta (12 hours) and nightly (6 hours) halt at 1.5 times baseline with 10 crashes, 25 error events, 10 stream failures, an SLO burn level of 4 and a 30 minute window. All values PROPOSED; doc 49 owns the policy.
- **Not built.** Cohort collectors, the baseline selector (previous release or previous ring), storage of rollout state, the journal and audit writes, alert wiring, the doc 49 rollout driver, `kodo.release.auto_rollback_canary` and the server-slot probation rollback of 16.6, and the settings registry keys.

## 17. Testing

The brief's requirement: CI fault tests, scheduled self-checks (restore backups, checksums, key recovery), owner-enabled chaos mode in a sandbox copy with a resilience score.

### 17.1 CI fault tests

- A fault harness injects: process kill (at each journal write boundary), Postgres down and slow, disk full, read-only data volume, clock skew (forward and backward), network partition between host and Conduit, a corrupt slot file, a bad signature, a missing probe catalogue entry, a probe that hangs, a probe that returns garbage, a module that fails its health contract, and a split-brain attempt (when standby is configured).
- Each fault has an expected result: the state transition, the alert, the audit entry, and the absence of data loss. The tests assert all of them.
- Runs on every PR that touches Kodo, the sentinel, or any module manifest. The full matrix runs nightly and before each release.
- Kill tests run at each journal boundary, 1,000 times in the nightly run, to prove crash-only recovery (7.2).

### 17.2 Scheduled self-checks

- **Checksums.** Daily scan of `kodo-data` and `kodo-evidence` against recorded hashes; a mismatch is a data integrity finding, not a silent repair.
- **Restore.** Weekly restore of the latest backup (doc 33) into a scratch database, followed by `db.schema_version`, a sampled GC integrity check and a Vault index check. The result is journalled. A failed restore past its window escalates.
- **Key recovery.** Monthly decryption of a recovery bundle in the sandbox, using a test-only key path that cannot touch production keys. The check proves the bundle is usable without exposing the production key (doc 14, doc 33).
- **Failover drill.** When standby is configured, a quarterly drill with a controlled primary stop and a scripted failover, followed by failback. The drill runs in a sandbox copy.

### 17.3 Chaos mode (owner-enabled, sandbox only)

- Enabled only by a role with `kodo.chaos.enable` and step-up, and never while an incident is open. It runs against a sandbox copy of the company: a clone on separate storage with a fake-data test company (the DEV SANDBOX full tier of the brief, process isolation, OS confinement, audited). Chaos never touches production data or keys.
- Experiments are declared in a signed catalogue: kill a module, slow a dependency, drop a percentage of wire frames, fill a disk to a threshold, skew the clock, pause the gateway, corrupt one slot copy (in the sandbox only).
- Each experiment has a hypothesis (the expected state transition and recovery time), a blast-radius limit and an abort condition that stops it within 10 seconds.
- **Resilience score (0 to 100)** combines: fraction of experiments whose hypothesis held, mean time to recovery against the target, data-loss invariant pass rate (zero is required), alert correctness (the expected alert fired, and no unexpected critical alert), and runbook success. A single data-loss invariant failure caps the score at 40 and is reported as a finding.
- Chaos results are a report, not a claim about production. The report says which parts of production the sandbox does and does not resemble.

### 17.4 Testing Kodo itself

- Property tests for the state machine (no transition outside 8.2, no `healthy` with an open critical rule, hysteresis respected).
- Mutation tests on the verifier and on the chain, to prove a single-byte change is always detected.
- Fuzzing of the journal decoder and the manifest parser (time-boxed nightly).
- Clock-injection tests for every timer (no test waits on wall time).

### 17.5 Client tests

Avalonia.Headless tests for the Monitor pane: each state word and colour, the `unobserved` display, offline snapshot age, time-travel reconstruction from fixtures, control visibility by role, keyboard-only navigation, and screen reader labels.

### 17.6 Self-check core choices (PROPOSED)

The pure core lives in `src/Bazaar.Kodo/SelfChecks` (namespace `Bazaar.Kodo.SelfChecks`). It reads no clock (a `TimeProvider` or an explicit `now`), does no I/O and has no concrete runner. It builds on 17.2; the choices below are where that section is silent. Nothing here is DECIDED.

- **Checks and schedule.** Four kinds (`ChecksumSweep`, `RestoreBackup`, `KeyRecoveryRehearsal`, `FailoverDrill`) with the cadences of 17.2. The report schedule of 16.10 is not reused: it is daily and weekly with local-time rules, and monthly is not built. A self-check schedule is a UTC anchor plus a fixed interval (1 hour to 400 days); "monthly" is 30 days and "quarterly" is 90 days. The failover drill is a definition with `Enabled` false unless the host sees a standby configured.
- **Runners.** `ISelfCheckRunner` is interface only (one per kind). The concrete runners need the doc 17 sandbox and doc 33 restore. A runner touches scratch or sandbox copies only and never a production key.
- **Never green on doubt.** A result is green only for a pass that looked at something. A pass with zero items checked becomes a skip with cause `nothing_checked`; a missing runner, a precondition skip, a timeout and a runner exception are amber; only a reported failure is red. A cause that is not a lower-case code is replaced by `invalid_cause`, so free text and paths never reach a result.
- **Scheduler.** `SelfCheckScheduler.RunDueAsync` runs each due check once per call and closes the newest due slot; older missed slots are not made up. A check seen for the first time runs once if a slot is already past, so an enabled check is never silently left unrun. Each run has its own timeout, measured on the `TimeProvider`. It never throws into the host except for the caller's own cancellation, and an overlapping call returns `Busy`.
- **History.** In memory, the newest 50 results per check (1 to 1000) and at most 64 checks. The journal copy is the host's job.
- **Mapping.** `SelfCheckObservationMapper` fills the evaluator's `KodoObservation`. A checksum mismatch sets the integrity flag at once (a finding, not a silent repair). Other failures are within grace, then red beyond grace once the non-green streak outlasts the definition's grace (the "failed restore past its window escalates" rule). Skips, timeouts and faults are within grace, then amber beyond it. A green result older than one interval plus grace is stale amber, and a check with no result after its anchor plus grace is amber. `Merge` only raises flags and adds counts, so self-checks can never make an observation greener.
- **Not built.** Runners, the journal record for a run, the Monitor pane line, settings wiring and the host timer.

### 17.7 Chaos gate core choices (PROPOSED)

The pure core lives in `src/Bazaar.Kodo/Chaos` (namespace `Bazaar.Kodo.Chaos`). It injects nothing: there is no concrete fault code, only `IChaosInjector`. Nothing here is DECIDED.

- **Gate.** `ChaosGate.Evaluate` is fail-closed and collects every refusal code in a fixed order. Off by default (`ChaosSettings.Enabled`). Required: the `kodo.chaos.enable` permission, a step-up no older than 5 minutes (at most 15), no open incident (any phase but closed, matching `FrozenAction.ChaosEnable`), a readable observation with no critical state (critical red or unknown, integrity or contract failure, crash loop, active incident), a sandbox target, and a valid plan.
- **Sandbox only, by identifier class.** A target is a sandbox only if it matches `sandbox:` plus a lower-case token. Identifiers starting with `prod`, `production` or `live`, identifiers on the operator's known-production list, and anything malformed or unknown are refused. The class is read from the identifier alone, never from a lookup that could fail open. This is a first barrier; the real isolation (separate storage, process isolation, OS confinement) belongs to doc 17.
- **Fault plan.** A plan holds experiments (fault from the 17.3 catalogue, hypothesis as expected state and recovery time, duration, blast radius percent, abort deadline). Defaults: 15 minutes per experiment, 1 hour per plan, 25 percent blast radius, 10 experiments. Settings may tighten these but never pass the ceilings (1 hour, 4 hours, 50 percent, 50 experiments); a setting above a ceiling makes the gate refuse everything. The abort deadline may not exceed 10 seconds (17.3). The signed catalogue itself is not built.
- **Abort.** `ChaosAbortMonitor.Check` aborts, in this order, on an open incident, an unreadable state, any critical state, a failed data-loss invariant, a clock that went backwards, an owner request and an expired duration. The first four are always on, whatever the plan lists. An abort carries the revert deadline (at most 10 seconds on). The host must call it on a tick well inside that deadline.
- **Audit.** `ChaosAuditRecord` holds actor, plan and target identifiers (anything not identifier-shaped is stored as `redacted`), the target class, the outcome, the codes and a SHA-256 digest of a fixed text form. Refusals are recorded as well as allows. A `ChaosInjection` carries the digest of the record that allowed it.
- **Not built.** The resilience score, hypothesis checking, the signed catalogue, the runner that drives experiments, concrete injectors, the sandbox clone, and the journal and audit-log write.

## 18. Relation to other docs and the audit log

### 18.1 Ownership table

| Concern | Owner | Kodo's part |
|---|---|---|
| Agent presence, activity, usage, cost, lanes near limit, telemetry pipeline (OTel bootstrap, allow-list, scrubber) | 23 | Kodo reads the pipeline and the lane quota states. Kodo does not own presence or cost. See 18.2 |
| `/health/live` and `/health/ready` probes, provider seam, settings registry, status listener bind floor | 03 | Kodo reads them. Kodo adds no probe endpoint of its own on the status listener |
| `/status` format and protocol version | 02 | Kodo adds a `health` section to `/status` (18.2) |
| Backups, restore wizard, recovery bundle | 33 | Kodo schedules and verifies. It does not define formats |
| Keys and secrets custody | 14 | Kodo holds only its journal key reference. Signing keys stay on the server build machine (doc 37) |
| Approvals, taint, kill switch, `audit_log` | 15 (and doc 48 for the audit log when written) | Every Kodo control and runbook step uses these |
| Conduit providers and probes | 39 | Kodo calls `ProbeAsync` only. Route choice and `OpenAsync` stay with Conduit |
| Constellation renderer and layout | 19 | Kodo's system map is a graph projection rendered by the Constellation renderer |
| Component manifests, `SoftDependsOn`, boundary rules | 40 | Kodo's manifest and dependency rules follow 40 |
| Pane registry, mnemonics | 41 | Kodo registers panes and proposes mnemonics |
| General module contract | 42 (not written) | Kodo defines the health part (section 10) |
| Update policies, snapshot and rollback policy, channels (stable, beta, nightly, LTS), staged rings, pins and deferrals | 49 (not written) | Kodo provides the A/B mechanism, the probation check, the ring cohort signals and the auto-halt (16.6). Policy stays in 49 |
| Test strategy | 34 | Kodo adds the fault, self-check and chaos suites (section 17) |
| Packaging and signed releases | 37 | Kodo's manifest signing follows 37 |
| Remote access, step-up, device pairing | 32 | Kodo requires step-up and does not define it |
| Client Monitor panes | 27, 28, 29, 30 | Each client gets a Monitor pane (section 14) |

### 18.2 Observatory (doc 23): where the line is

Doc 23 and Kodo both look at the running system, so the line is drawn by question:

- **Doc 23 answers "what is each agent doing and what does it cost".** Presence, activity, usage, price tables, cost chips, lane quota near-limit flags, the telemetry pipeline and its scrubber, and the agent counts on the status page.
- **Kodo answers "is the system working, what is failing, what will fail and what do we do".** Probes, state, scores, SLOs, alerts, predictions, runbooks, incidents, fleet health and the environment.
- **Shared inputs.** Kodo reads doc 23's lane quota states and telemetry pipeline. Doc 23 reads Kodo's `health` section for its build-health and system-health tiles. Neither writes the other's tables.
- **Status page.** Doc 23 owns the contents of `GET /status` for agent and usage sections (C40 says so). Kodo proposes a `health` section for the same endpoint, composed by module sections. This changes the C40 split and needs the owner's ratification (section 19, conflict 5).
- **Overlap in the Monitor pane and Observatory.** Doc 23's Observatory remains the agent view. Kodo's Monitor pane is the system view. The dashboard start screen (doc 27) shows one health tile from Kodo and one agents tile from doc 23.
- **Audience.** Doc 23 is owner-only for agent details in v1. Kodo's fleet and environment views are owner-visible by default, and role-gated for any other role.

### 18.3 Audit log (doc 48)

- Kodo writes to the audit log for every admin and security event it causes or observes: settings changes, control actions, runbook approvals, incident entry and exit, slot switches, rollbacks, tamper findings, webhook changes, exports, and step-up results.
- Kodo's own journal is separate from the audit log. The journal holds high-volume health history (state changes, observations that changed a value, probe results at their cadence), and it is kept per the Kodo retention settings. The audit log holds the admin and security record with all data reads and writes by humans and agents, kept by its own policy.
- Each journal checkpoint references the audit log's head at that time, and each audit event for a Kodo action references the journal record it followed. Two chains, cross-referenced, so neither can be rewritten without breaking the other.
- Merkle proofs (15.1) work for both. External anchoring (6.5) covers both with the same sinks.

### 18.4 Constellation (doc 19)

The system map is a graph view, not a new renderer. Node positions come from the server's layout, the same way Constellation's knowledge graph layout does (doc 19: layout on the server, clients render). Kodo supplies nodes and edges; Constellation supplies the layout and renderer seam. A node for a Vault or memory object never appears in Kodo's map, so the Vault access rules of doc 18 are not bypassed.

### 18.5 Conduit (doc 39)

- Kodo's Conduit probes call `ProbeAsync` on each Conduit provider. They count as network observations, and their results feed the `conduit.<provider>` probe family.
- Conduit's built-in rate limits and auth lockouts (brief) are counted by Kodo and shown in the fleet and security views. Kodo does not change the lockout policy.
- The isolate control, when applied to a Conduit path, calls Conduit's own disable method through its provider. Kodo never opens a connection, never picks a route and never stores Conduit credentials.

### 18.6 Module contract (doc 42)

Section 10 is the health part of the contract. When doc 42 is written, its module lifecycle, dependency rules and compatibility shims must include section 10 unchanged, or this document changes with it.

### 18.7 Extraction (doc 49)

Kodo and Constellation.Renderer stay first-party modules until the owner asks for extraction. When asked, the plan (copied from the brief) is:

1. A Bazaar-free core with an adapter for the host.
2. Split NuGet packages: core, probes, storage, API, UI pane.
3. Docs and samples per package.
4. Its own repository with independent versioning, CI, SBOM and signed releases.
5. Bazaar pins a version like any dependency. The module carries its own version in its metadata.

Until then, nothing in this document depends on being outside Bazaar. The contract in section 10 is written so that extraction only moves code.

## 19. Conflicts and gaps (not silently resolved)

1. **Update 2026-11-13: docs 42, 48 and 49 are in the same PR.** Earlier drafts of this document said they were missing. This document still marks each dependency and does not invent their content. Owner or the doc owners should confirm the section 10 and section 18 interfaces.
2. **Audit log owner.** Doc 03 and doc 15 name the audit log `audit_log` and place it in doc 15. The brief names it as doc 48. Section 18.3 keeps Kodo's journal separate either way; the owner should say which doc owns the audit log's storage.
3. **Licence for Bazaar.Sdk and Bazaar.Contracts.** Update 2026-11-13: the owner chose AGPL-3.0 everywhere (option b below). NOTICE and the csproj files now match. Earlier text follows. CLAUDE.md (2026-10-25) said Apache-2.0. The 2026-11-13 brief says AGPL-3.0 everywhere, with a module exception and a dual licence. The repository's NOTICE still says Apache-2.0 for `src/Bazaar.Sdk` and `src/Bazaar.Contracts`, pending counsel review. Kodo's manifest signing and its contract base class live in `Bazaar.Kodo.Abstractions` and `Bazaar.Kodo.Testing`, so the owner must choose a licence for those two packages before they ship. Options: (a) Apache-2.0 as before, which lets third-party modules use them without AGPL obligations; (b) AGPL-3.0 with the module exception, as the brief describes. Not decided here.
4. **Contributions.** CONTRIBUTING.md is DCO only. The brief adds a CLA for core code. Kodo's core is core code, so its contributions need the CLA once it is adopted. Not decided here.
5. **Status page contents (DECIDED C40 versus this proposal).** DECISIONS-LOG C40 gives doc 23 the contents of the status page. Section 12.4 and section 18.2 propose a `health` section from Kodo, composed into the same endpoint. This is a change to a DECIDED item and needs owner ratification.
6. **Public status page versus the bind floor.** Doc 03 keeps the status listener on loopback unless the owner approves a wide bind, and CLAUDE.md allows a read-only status page. Section 12.4 proposes a public page, which needs that owner approval. Off by default in this document.
7. **Android controls versus the Observatory read-only rule.** Doc 23 limits Android to a read-only subset. The brief asks for a Monitor pane in every client with role-gated controls. Section 14.3 proposes step-up controls on Android. The owner should confirm the exception.
8. **Time partitioning versus the metric store.** Doc 04 and doc 23 decided no time-based table partitioning in v1 (DECISIONS-LOG round 1, C10). Kodo's raw series and journal grow faster than rollups. This document proposes row-based retention (deletion in batches with rollups kept longer) and no partitioning in v1, measured before any change. If volume proves the rule wrong, it is revisited with a DECIDED change to C10.
9. **README entry.** The brief asks for Kodo to get an entry in docs/design/README.md as a named module. This task edits only this file, so the README entry is not added here. It is a follow-up.
10. **Scale tension.** The brief makes single-node the default and splittable to several nodes later. Warm standby (7.6) is optional and off by default. It is an availability feature with its own witness requirement, so it does not change the scale decision, but it does add a second host to the support matrix. The owner should confirm it stays optional in the first release.
11. **Doc 41 mnemonics.** Section 14.4 proposes mnemonics. Doc 41 owns the registry, and the examples in the brief (ngc1, nrfdrm, topgc, unread, settings) are in its namespace. Kodo's proposed names need checking for collisions when doc 41 is read in full. Doc 41 was not re-read in this task beyond its existence and its place in the design index.
12. **Model names are not stated in this document, as the brief requires.** Model explanations and summaries use "the company's configured model through the gateway", and they follow doc 05 policy.

## 20. Settings

Every value below is a setting in the registry (doc 03), with the default shown. Settings may tighten floors, never loosen them. Each change writes an audit entry.

| Key | Type | Default | Scope | Who may change | Approval |
|---|---|---|---|---|---|
| `kodo.supervise.restart_backoff_s` | list of int | 1, 2, 5, 15, 60, 300 | install | operator | No |
| `kodo.probe.pool.<name>.concurrency` | int | local 8, db 6, external 4, slow 2 | install | operator | No |
| `kodo.probe.timeout_ms.default` | int | 2000 | install | operator | No |
| `kodo.state.stability_window_s` | int | 300 | install | operator | No (floor: 60) |
| `kodo.state.flap_limit` | int | 6 in 30 minutes | install | operator | No |
| `kodo.integrity.scan_interval_min` | int | 5 | install | operator | No (floor: 5) |
| `kodo.integrity.probation_min` | int | 10 | install | operator | No |
| `kodo.journal.checkpoint_records` | int | 1000 | install | operator | No |
| `kodo.journal.checkpoint_minutes` | int | 15 | install | operator | No |
| `kodo.journal.anchor_sinks` | list | owner devices | install | operator | Yes; adding a sink needs the owner |
| `kodo.journal.spill_max_mb` | int | 512 | install | operator | No |
| `kodo.retention.journal_days` | int | 730 | install | operator | Yes; counsel (retention and erasure) |
| `kodo.retention.raw_series_days` | int | 7 | install | operator | No |
| `kodo.retention.rollup_days` | int | 730 | install | operator | No |
| `kodo.retention.evidence_days` | int | 2555 | install | operator | Yes; counsel (legal hold interplay) |
| `kodo.score.weights.<category>` | int | per 12.2 | install | operator | No |
| `kodo.slo.<name>.target` | decimal | per 12.3 | install | operator | No (floor: no lower than 95 percent for server availability) |
| `kodo.alert.escalation_min.critical` | int | 15 | install | operator | No |
| `kodo.incident.auto_enter` | list | the four triggers in 16.3 | install | operator | Yes; cannot be removed below the floor |
| `kodo.recover.unattended` | list | empty | install | operator | Yes; owner. Only `host_restart` and `slot_switch` are accepted; any other value is refused. Each unattended run is still audited, bounded by the retry cap and verified by the runbook's verify step, and a failed verify stops with needs-human |
| `kodo.failover.enabled` | bool | false | install | operator | Yes; owner |
| `kodo.failover.auto_failback` | bool | false | install | operator | Yes; owner |
| `kodo.recover.unattended` | list | empty | install | operator | Yes; owner. Only `host_restart` and `slot_switch` are accepted; any other value is refused. Each unattended run is still audited, bounded by the retry cap and verified by the runbook's verify step, and a failed verify stops with needs-human |
| `kodo.explain.mode` | enum `off`, `ask`, `auto` | `off` | install | operator | Yes; counsel for `auto` |
| `kodo.explain.allow_auto` | bool | false | install | operator | Yes; owner |
| `kodo.chaos.allowed` | bool | false | install | owner | Yes; owner |
| `kodo.statuspage.enabled` | bool | false | install | owner | Yes; owner (bind floor, 15.5) |
| `kodo.metrics.bind` | string | loopback | install | operator | Yes if non-loopback |
| `kodo.metrics.require_token` | bool | true when non-loopback | install | operator | Floor: true when non-loopback |

## 21. Work packages (planned order)

Each package is a small PR with its tests (CLAUDE.md). Nothing is started.

1. Settings keys and defaults (03 registry), `kodo` section.
2. Journal: record format, hash chain, spill file, replay. Pure code with property tests first, then Postgres migration (doc 04 conventions).
3. Evaluator: states, transitions, hysteresis, flap detection, scoring with caps. Pure, table-driven tests.
4. Probe runtime: bulkhead pools, timeouts, breakers, `unknown` semantics. Fault tests.
5. Built-in probes in the order of section 9.1, starting with `host.*`, `db.*`, `secrets.store`, `kodo.self.*`.
6. Module health contract: abstractions, `ModuleHealthContractTests<T>`, loader gate. Depends on doc 42 for the lifecycle. First slice (probe interface, loader gate, contract test base) is built; its choices are PROPOSED in section 10.5.
7. Immutable runtime: manifest format, signing and verification (release key from 37), slot pointer, verifier in the sentinel. Spike first (section 22).
8. Sentinel process: supervision, slot switching, rollback, loopback status file.
9. Read API and `kodo` stream on doc 02's registry.
10. Webhooks, Prometheus and OTel export (after doc 23's pipeline exists).
11. Predictor: forecasts, baselines, drift; advisory only.
12. Responder: alerts routing, runbook engine, approval wiring (doc 15), one-click fixes.
13. Incident mode: record, freeze, evidence, notify, guided recovery, summaries.
14. Anchor sinks and Merkle proofs.
15. Warm standby (optional): replication, witnesses, fencing. Last, behind a setting.
16. Client Monitor panes in `Bazaar.Client.Kodo`, starting with Status and Fleet, then map, logs and time-travel.
17. Testing suites: fault harness, self-checks, chaos mode in the sandbox (after doc 17 sandboxes exist).

## 22. PLANNED versus built

| Part | Status | Notes |
|---|---|---|
| Module identity, naming and projects | PLANNED | No project exists |
| Immutability: signed manifest, A/B slots, read-only runtime, tamper detection | PLANNED | Pure parts built in `src/Bazaar.Kodo/Runtime` (manifest, signature check, slot pointer, verifier; package 7). No sentinel or swap code yet. The design uses a signed pointer file (6.3), not a junction. Needs spike K1 (Windows read-only slot and pointer replace) and spike K2 (Linux bind-mount slots and pointer rename) run on hardware. Proposed for doc 35; not numbered there yet |
| Journal hash chain, checkpoints, anchors, Merkle proofs | PARTIAL | Pure parts built in `src/Bazaar.Kodo/Anchoring` (package 14): Merkle tree, inclusion and consistency proofs with verifiers, anchor record and codec, `IAnchorSink`, planner, fail-safe publisher, anchor verifier. Choices are PROPOSED in 6.5.1. No sinks, checkpoint signing, storage, API route or settings wiring yet. Needs spike K3 (anchor sink latency and object lock support) |
| Server journal (chain, spill, proof route) | PARTIAL | In-memory chain with an optional local spill file and a Merkle proof route in `Bazaar.Server.Kodo` (5.4). Postgres store `Bazaar.Server.Kodo.Postgres` (journal, state history, latest snapshot, replay by hash, retention, proof over the stored history; PROPOSED in 7.10), registered in the host. No checkpoints, anchoring, spill seal or size bound yet |
| Sentinel process | PARTIAL | Pure parts built in `src/Bazaar.Kodo/Sentinel` (package 8): restart backoff, slot switch with probation and rollback, startup fallback, loopback status shape. Choices are PROPOSED in 7.8. No OS launcher, file store, loopback server, crash-loop detection or audit event yet |
| Failure model: supervision, bulkheads, breakers, spill | PARTIAL | Pure parts testable first. Spill fallback and replay into Postgres built in `Bazaar.Server.Kodo` (7.10, PROPOSED): the local journal keeps writing while the database is down and is replayed by hash on return. Supervision, bulkhead wiring beyond the probes, the spill seal and its size bound are not built |
| Warm standby and fencing | PARTIAL, optional | Pure core built in `src/Bazaar.Kodo/Standby`: role model, fencing epoch, lease record and codec with digest, witness quorum evaluator with a strict-majority floor, failover decision, self-fence, and a journalled fail-safe controller over witness, journal and store interfaces. Choices are PROPOSED in 7.9. No concrete witnesses, stores or replication, no database-side rejection, no incident wiring and no settings yet. Still needs a fencing spike on the chosen database setup |
| State model (5 states plus `unobserved` display) | PARTIAL | Table design in 8. The server loop evaluates the state each pass (5.4); incident freeze, recovery actions, crash loop, integrity failures and SLO inputs are not fed yet (passive recovery stands in for a Responder) |
| Built-in probes | PARTIAL | Catalogue in 9.1. Wired in `Bazaar.Server.Kodo` (5.4): `host.disk.free.data`, `host.memory.pressure`, `kodo.self.journal_writable`, `kodo.self.spill`; `host.clock.drift` is built and off by default. All other rows depend on modules that are not built |
| Module health contract | PLANNED | Depends on doc 42 |
| Module manifest | PARTIAL | Pure core in `src/Bazaar.Kodo/Manifest`: strict decoder, canonical bytes, rule validator, signature check against the trust root through the runtime verifier, load decision with stable cause codes and an owner-approval list. Choices PROPOSED in 11.1. No package reader, signed catalogue file, approval store, install dry run or manager UI yet |
| Score and SLOs | PARTIAL | Pure SLO core built in `src/Bazaar.Kodo/Slo`: definitions, bounded sample store with an in-memory implementation, error budget, multi-window burn rules (tighten-only), deterministic status, adapter to the report builder's `ReportSlo`. Choices PROPOSED in 12.5. The health score (12.2) is not built. No journal replay of uptime, durable store, settings wiring or Responder routing yet |
| Prediction (forecast, baseline, drift) | PARTIAL | Pure core in `src/Bazaar.Kodo/Prediction` (package 11): robust trend forecaster, per-install baseline learner, CUSUM drift detector, saved memory (JSON), probe adapters. Choices PROPOSED in 13.6. No scheduler, storage, Page-Hinkley test, hourly recompute, alert wiring or Monitor pane yet |
| Model explanations | PLANNED, OFF by default | Depends on the gateway (doc 05) |
| Monitor pane in each client | PARTIAL | Status and Fleet views built in `src/Bazaar.Client.Kodo` (package 16, first slice): the pane uses the shared `KodoStreamTracker` (one stream tracker; reducer removed) through a thin `KodoStreamSession`, view models, Avalonia views, headless tests. Choices PROPOSED in 14.6. No pane registry registration, palette wiring, stream transport, fleet DTO or endpoint, live metrics, logs, time-travel or map yet |
| Read API, stream, webhooks, metrics and OTel export | PARTIAL | Pure core in `src/Bazaar.Kodo/Api` (package 9): snapshot builder and `kodo` stream revisions, with the wire DTOs and client `unobserved` derivation in `Bazaar.Contracts`. Choices PROPOSED in 15.6. No HTTP routes, no `/ws` registration, webhooks, metrics or OTel yet. Depends on doc 02 registry and doc 23 pipeline |
| Alerts, runbooks, incident mode, summaries | PARTIAL | Pure core in `src/Bazaar.Kodo/Responder` (package 12): alert model and router (severity, dedupe, grouping, rate limit, quiet window, breakers, escalation), runbook catalogue and engine (signed definitions, journalled steps, bounded retries, rollback, resume from saved state), approval gate interface, one-click fix catalogue. Choices PROPOSED in 16.8. Pure core in `src/Bazaar.Kodo/Incidents` (package 13): incident record and lifecycle, freeze rules and automatic-entry floor, evidence manifest and collector, guided recovery over the Responder, notifications through the router, deterministic post-incident summary draft with fact check. Choices PROPOSED in 16.9. No channel, runner, journal, store, freeze sink or evidence source implementations, no doc 15 gate or kill switch, no external anchoring or legal hold, no summary publishing or revisions, no model prose and no alert-fatigue metrics yet. Depends on doc 15 approval gates and doc 48 roles |
| Release ring cohort signals and auto-halt | PLANNED | Depends on doc 49 (rings, channels) and doc 37 (signed releases) |
| Testing: fault harness, self-checks, chaos mode | PLANNED | Depends on doc 17 sandbox and doc 33 restore |
| Read API, stream, webhooks, metrics and OTel export | PARTIAL | Pure core in `src/Bazaar.Kodo/Api` (package 9): snapshot builder and `kodo` stream revisions, with the wire DTOs and client `unobserved` derivation in `Bazaar.Contracts`. Choices PROPOSED in 15.6. Wired in `Bazaar.Server.Kodo` (5.4, PROPOSED): `GET /api/kodo/v1/status`, `/events` and `/journal/proof/{sequence}` behind the host guard, and the `kodo` stream on `/ws` through the existing wire subscription engine (`KodoStreamGate`, `KodoWireStreamPublisher`; snapshot on subscribe, then `state_changed` and `heartbeat`; owner-only by default). Not wired: the other section 15.1 routes, the topics of 15.2 beyond `status`, host registration in `Program.cs`, webhooks, metrics and OTel. Depends on doc 02 registry and doc 23 pipeline |
| Alerts, runbooks, incident mode, summaries | PARTIAL | Pure core in `src/Bazaar.Kodo/Responder` (package 12): alert model and router (severity, dedupe, grouping, rate limit, quiet window, breakers, escalation), runbook catalogue and engine (signed definitions, journalled steps, bounded retries, rollback, resume from saved state), approval gate interface, one-click fix catalogue. Choices PROPOSED in 16.8. Pure core in `src/Bazaar.Kodo/Incidents` (package 13): incident record and lifecycle, freeze rules and automatic-entry floor, evidence manifest and collector, guided recovery over the Responder, notifications through the router, deterministic post-incident summary draft with fact check. Choices PROPOSED in 16.9. No channel, runner, journal, store, freeze sink or evidence source implementations, no doc 15 gate or kill switch, no external anchoring or legal hold, no summary publishing or revisions, no model prose and no alert-fatigue metrics yet. Depends on doc 15 approval gates and doc 48 roles |
| Scheduled health reports | PARTIAL | Pure core in `src/Bazaar.Kodo/Reports`: daily and weekly schedule with explicit offset rules, bounded catch-up, deterministic report builder with digest, `IReportSink`, fail-safe journalled runner. Choices PROPOSED in 16.10. No sinks, signing, monthly cadence, forecasts, host wiring or settings yet |
| Testing: fault harness, self-checks, chaos mode | PARTIAL | Pure cores built: self-checks in `src/Bazaar.Kodo/SelfChecks` (definitions, interval schedule, runner interface, never-green results, bounded history, fail-safe scheduler, observation mapper; choices PROPOSED in 17.6) and the chaos gate in `src/Bazaar.Kodo/Chaos` (owner-enabled gate, sandbox-only target class, bounded fault plan, abort monitor, audit record, injector interface; choices PROPOSED in 17.7). The CI fault harness (17.1), concrete runners and injectors, the signed catalogue, the resilience score, journal wiring and settings are not built. Depends on doc 17 sandbox and doc 33 restore 17.4 in-process part built (package 17b): seeded fuzz, property and one-bit mutation tests in `tests/Bazaar.Kodo.Tests/Fuzz` for the wire, journal, signature and export code, the display derivation, backoff, alert rate limiter and slot switcher. No Merkle proof code exists yet to cover, and the nightly time-boxed run is not wired |
| Release ring cohort signals and auto-halt | PARTIAL | Pure core in `src/Bazaar.Kodo/Rings`: deterministic ring membership, cohort signal records, baseline comparison, halt-rule evaluator with floors, `insufficient data` outcome, journal-friendly halt decision, ring progression, approval-backed resume gate, channel profiles. Choices PROPOSED in 16.9. No signal collectors, storage, journal or alert wiring, rollout driver, auto-rollback setting or settings keys yet. Depends on doc 49 (rings, channels) and doc 37 (signed releases) |
| Public status page | PLANNED, OFF by default | Needs owner approval (section 19, item 6) |
| Existing code this would reuse | BUILT elsewhere | `/health/live` and `/health/ready` are in doc 03's design; the component kernel (doc 40) exists in the code base on the owner branch. Kodo builds on those, it does not replace them |

Nothing in this document is built. This table is the single list of what is not yet built.

## 23. Questions for the owner

1. **Witness set for standby.** Default: the owner's paired devices plus one optional third node, 2 of 3. Is a paired-device-only quorum acceptable when no third node exists? (The core already accepts 2 of 2 as a strict majority, so the question is only whether to allow it by default: it is safe but a single device being offline blocks failover. See 7.9.)
2. **Android controls.** Default: step-up controls on Android, read-only otherwise. Confirm the exception to the doc 23 Android rule.
3. **Status page split (C40).** Default: doc 23 keeps the agent and usage sections; Kodo adds a `health` section to the same `/status` endpoint. Ratify or reject.
4. **Audit log ownership.** Default: doc 15 keeps the `audit_log` table until doc 48 is written, and Kodo's journal stays separate and cross-referenced. Confirm.
5. **External anchor defaults.** Default: the owner's paired devices, plus an optional RFC 3161 authority when the owner configures one. Is that enough, or should an object-locked store be on by default when the operator has one?
6. **Kodo's licence for its SDK packages (section 19, item 3).** Apache-2.0 as before, or AGPL-3.0 with the module exception?
