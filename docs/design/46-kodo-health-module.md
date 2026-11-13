# 46. Kodo: the health module (鼓動, heartbeat)

Status: PLANNED. Design intent only. Nothing in this document is built, and no code, migration, probe or pane exists yet. Owner decisions of 2026-11-13 are DECIDED; every detail below them is PROPOSED unless marked otherwise. Owner questions: 6 (section 23).
Depends on: 02-wire-protocol-and-sync.md (authenticated HTTP, `/ws` stream registry, `/status` format), 03-server-host-and-providers.md (`/health/live`, `/health/ready`, provider seam, settings registry, status listener bind floor), 04-postgres-schema-and-migrations.md (conventions, NOTIFY, migrations), 05-model-gateway.md (provider and lane health, quota states), 15-security-taint-and-approvals.md (approval tiers, taint, `audit_log` in its current form), 19-constellation-and-git-solar-system.md (renderer seam and server-side layout for the system map), 23-observatory-and-usage.md (telemetry pipeline, presence, usage), 33-backup-dr-and-export.md (backups, restore wizard, recovery bundle), 39-conduit-remote-connectivity.md (`IConduitProvider.ProbeAsync`, rate limits), 40-core-feature-modules-audit.md (component manifests, `SoftDependsOn`), 41-client-gui-customization-and-dsl.md (pane registry, mnemonics, keyboard-first rule).
Not yet written, referenced here: 42 (module contract), 48 (audit log), 49 (update system). See section 19.
Used by: 27, 28, 29, 30 (client Monitor panes), 34 (test strategy), 35 (spikes), 37 (packaging and signed releases).

## 1. Purpose

Kodo is the module that answers, at every moment and for every part of a Bazaar installation, "is it alive, is it healthy, what is going to break, and what do we do about it". It watches the server, every first-party and third-party module, the fleet of clients, the physical host, the network paths, the certificates and clocks, the backups, and the AI model endpoints. It scores all of that, predicts the failures it can predict, runs the recovery runbooks, and, when something is seriously wrong, puts the whole installation into incident mode.

Kodo is also meant to be the hardest module to damage. Its runtime is signed and verified on every start, it runs from a read-only slot, it recovers from crashes by design, and it is supervised by a process smaller than itself that does not depend on Bazaar code.

Scope of the name: "Kodo" (鼓動, heartbeat) is the module and the prose name. The module id is `kodo`, the project is `Bazaar.Kodo`, and the brief's form `Bazaar.KODO` is the same module. The owner's words for the goal, recorded as the design brief: "the most immutable app known to man" and "the most impressive named feature on the planet". This document takes the maximal option wherever the brief offers a choice, and says so where a cheaper option was considered and rejected.

## 2. Decided inputs (owner, 2026-11-13, DECIDED)

- Kodo is the Health API module. Its name is Kodo; its module id is `kodo` (brief: Bazaar.KODO).
- Immutability, failure handling, a monitor pane in every client, testing, prediction, scope, scoring, the API (including the mandatory module health contract), recovery and incident mode are all in scope, at maximal depth.
- Kodo is a normal first-party module for now. Extraction (a Bazaar-free core, split packages, its own repository) waits until the owner asks for it. The extraction rule is copied from the brief; doc 49 is not yet written to confirm it (section 19).
- Deps are modules. Postgres and pgvector are modules Kodo monitors, not packages Kodo installs. Install is a Bazaar-only action with zero external installs for users.
- Telemetry and crash reports: on-device redaction is never model-dependent, and every server-side model explanation is labelled "unverified diagnosis" (see section 13).
- Configurability rule (DECISIONS-LOG round 13): every open value is an operator setting with a safe default. DECIDED items and security invariants are not settings. Settings may tighten, never loosen.
- Server build is one OS-agnostic .NET build. Anything OS-specific sits behind a provider interface (doc 03). Kodo follows the same rule for SMART, sensors, power, service state and Windows ACLs.
- Clients are Avalonia (`Bazaar.Client.Core` plus thin heads). No web client. The only browser-facing surface is the read-only status page, which is allowed by CLAUDE.md and governed by section 15.5.
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

## 6. Immutability

The brief's requirement is: signed and verified on every start, read-only runtime with separate writable data, atomic A/B versions with instant rollback, and runtime tamper detection. Each part is specified here.

### 6.1 Signed manifest

- Every file in a Kodo slot (binaries, managed assemblies, runbook files, probe catalogue, default settings file, the sentinel's verifier) is listed in `kodo.manifest.json` with its SHA-256 and size.
- The manifest is signed with the Bazaar release signing key (kept on the server build machine, doc 37). The signature is a detached signature over the canonical JSON of the manifest (RFC 8785 canonicalisation). The manifest also carries the Kodo version, the protocol version of the health contract (section 10), the build commit and the SBOM digest (doc 37).
- The install trust root is a set of public keys pinned at install time in the sentinel's configuration, with key ids and a validity window. A manifest signed by an unknown key is rejected. Rotation adds a new key under an old-key signature, and a revoked key list is itself signed.
- Verification runs at four points: every start (before any Kodo code loads), on a timer (default every 5 minutes), before every control action from the Responder, and before any slot switch.

### 6.2 Read-only runtime, writable data

- The runtime slot is made read-only by the OS provider. Windows: a dedicated directory whose ACL grants read and execute to the Kodo service account and to Administrators, and denies write to everyone except the installer's signed update path. Linux: the slot is read-only at the mount or bind level, and the update path remounts it for the swap. Both are behind `IImmutableRuntimeProvider`, probed at start (a provider that cannot enforce read-only downgrades the module state to degraded and says so in the status, it does not silently run writable).
- Data and evidence directories are the only places Kodo writes. Kodo never writes into its slot, and the verifier treats any file in the slot that is not in the manifest as tampering.
- Config is not in the slot. Operator values are in the settings registry (doc 03) and in `kodo-data`, so changing a value never requires writing the runtime.

### 6.3 Atomic A/B slots and instant rollback

- Two slots, `slot-a` and `slot-b`. One pointer file, `current`, names the active slot. The pointer is written by write-to-temp then atomic rename (on Windows, a directory junction swap with the same rule; on Linux, a symlink swap by rename). The pointer is the only mutable item in the runtime area.
- An update writes the new version into the inactive slot, verifies its manifest (6.1), runs preflight (doc 49), starts the new Kodo in shadow mode for a probation window (default 10 minutes, a setting) where it observes but does not act, and only then flips the pointer. The old slot is kept.
- Instant rollback is the same pointer flip to the previous slot, after that slot's manifest is re-verified. It needs no network and no database, so it works when the server is down. The sentinel performs rollback on its own when the new slot fails its probation or fails the signature check at start.
- A rollback writes an audit event and a Kodo journal event before the flip is announced to clients. Rollback never deletes the slot it leaves.

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

## 8. State model

### 8.1 States

Kodo's health state is one of five values. Each has a colour token from the theme (doc 27) and a text word for accessibility, so colour is never the only signal.

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

Kodo cannot report its own death. Clients derive a sixth display value, `unobserved`, when the Kodo stream has been silent for longer than its heartbeat budget (default 30 seconds) or when the status snapshot is older than its freshness budget. `unobserved` is not a server state, is never written to the journal by Kodo, and is rendered as its own word and colour. The client writes it to its own local timeline.

## 9. Scope: what Kodo watches

Kodo's scope is the server, its modules, the fleet of clients and the whole environment (brief: hardware SMART, temperature, power; Conduit network paths; certificates; clock drift; backups; AI model endpoints). Each category is a probe family. Every probe declares its category, its criticality, its cadence, its pool and its provider interface.

Probe criticality: **critical** probes can make the state `failing`. **Non-critical** probes can make it `degraded` only. A probe may be `unknown` (timeout, breaker open, provider missing) and `unknown` never counts as green.

### 9.1 Built-in probes (PROPOSED catalogue)

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
- Each control is a high-impact action under doc 15: approval tier, audit entry, idempotency key, and a dry-run preview where the action has one. Restart, isolate and roll back are step-up required by default.
- Viewing logs and traces is separately granted because logs can contain personal data. Defaults grant the owner all of these and grant no other role any of them.

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

- A `kodo` stream on the existing `/ws` connection, registered in doc 02's stream registry. It sends a snapshot on subscribe and then revisioned deltas. Any revision gap triggers a snapshot re-fetch, as in doc 23's presence stream.
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
| Host not ready, service stopped | Restart host through the sentinel | Auto, step-up not required | `host.ready` green within 2 minutes |
| Disk near full on data volume | Rotate logs, prune spill past its window, offer Vault archive | Approval | Free space above threshold |
| Replication broken (7.6) | Re-seed standby from the latest base backup | Approval, step-up | Replication status green |
| Slot signature failure | Switch to the other verified slot | Auto | `kodo.self.signature_verify` green on the new slot |
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

### 18.3 Audit log (doc 48, not yet written)

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

### 18.6 Module contract (doc 42, not yet written)

Section 10 is the health part of the contract. When doc 42 is written, its module lifecycle, dependency rules and compatibility shims must include section 10 unchanged, or this document changes with it.

### 18.7 Extraction (doc 49, not yet written)

Kodo and Constellation.Renderer stay first-party modules until the owner asks for extraction. When asked, the plan (copied from the brief) is:

1. A Bazaar-free core with an adapter for the host.
2. Split NuGet packages: core, probes, storage, API, UI pane.
3. Docs and samples per package.
4. Its own repository with independent versioning, CI, SBOM and signed releases.
5. Bazaar pins a version like any dependency. The module carries its own version in its metadata.

Until then, nothing in this document depends on being outside Bazaar. The contract in section 10 is written so that extraction only moves code.

## 19. Conflicts and gaps (not silently resolved)

1. **Docs 42, 48 and 49 are not in the repository.** The brief cites them. They are not on the working branch or the owner branch (`claude/project-thread-b9omz8`, which holds docs 39 and 40 and the Constellation doc). This document marks each dependency and does not invent their content. Owner or the doc owners should confirm the section 10 and section 18 interfaces.
2. **Audit log owner.** Doc 03 and doc 15 name the audit log `audit_log` and place it in doc 15. The brief names it as doc 48. Section 18.3 keeps Kodo's journal separate either way; the owner should say which doc owns the audit log's storage.
3. **Licence for Bazaar.Sdk and Bazaar.Contracts.** CLAUDE.md (2026-10-25) says Apache-2.0. The 2026-11-13 brief says AGPL-3.0 everywhere, with a module exception and a dual licence. The repository's NOTICE still says Apache-2.0 for `src/Bazaar.Sdk` and `src/Bazaar.Contracts`, pending counsel review. Kodo's manifest signing and its contract base class live in `Bazaar.Kodo.Abstractions` and `Bazaar.Kodo.Testing`, so the owner must choose a licence for those two packages before they ship. Options: (a) Apache-2.0 as before, which lets third-party modules use them without AGPL obligations; (b) AGPL-3.0 with the module exception, as the brief describes. Not decided here.
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
| `kodo.failover.enabled` | bool | false | install | operator | Yes; owner |
| `kodo.failover.auto_failback` | bool | false | install | operator | Yes; owner |
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
6. Module health contract: abstractions, `ModuleHealthContractTests<T>`, loader gate. Depends on doc 42 for the lifecycle.
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
| Immutability: signed manifest, A/B slots, read-only runtime, tamper detection | PLANNED | Needs spike K1 (Windows read-only slot and junction swap) and spike K2 (Linux bind-mount swap). Proposed for doc 35; not numbered there yet |
| Journal hash chain, checkpoints, anchors, Merkle proofs | PLANNED | Needs spike K3 (anchor sink latency and object lock support) |
| Sentinel process | PLANNED | No code |
| Failure model: supervision, bulkheads, breakers, spill | PLANNED | Pure parts testable first |
| Warm standby and fencing | PLANNED, optional | Needs a fencing spike on the chosen database setup |
| State model (5 states plus `unobserved` display) | PLANNED | Table design in 8 |
| Built-in probes | PLANNED | Catalogue in 9.1; most depend on modules that are not built |
| Module health contract | PLANNED | Depends on doc 42 |
| Score and SLOs | PLANNED | Formula in 12 |
| Prediction (forecast, baseline, drift) | PLANNED | Deterministic parts first |
| Model explanations | PLANNED, OFF by default | Depends on the gateway (doc 05) |
| Monitor pane in each client | PLANNED | Depends on client module split (doc 40 step 7) |
| Read API, stream, webhooks, metrics and OTel export | PLANNED | Depends on doc 02 registry and doc 23 pipeline |
| Alerts, runbooks, incident mode, summaries | PLANNED | Depends on doc 15 approval gates |
| Release ring cohort signals and auto-halt | PLANNED | Depends on doc 49 (rings, channels) and doc 37 (signed releases) |
| Testing: fault harness, self-checks, chaos mode | PLANNED | Depends on doc 17 sandbox and doc 33 restore |
| Public status page | PLANNED, OFF by default | Needs owner approval (section 19, item 6) |
| Existing code this would reuse | BUILT elsewhere | `/health/live` and `/health/ready` are in doc 03's design; the component kernel (doc 40) exists in the code base on the owner branch. Kodo builds on those, it does not replace them |

Nothing in this document is built. This table is the single list of what is not yet built.

## 23. Questions for the owner

1. **Witness set for standby.** Default: the owner's paired devices plus one optional third node, 2 of 3. Is a paired-device-only quorum acceptable when no third node exists?
2. **Android controls.** Default: step-up controls on Android, read-only otherwise. Confirm the exception to the doc 23 Android rule.
3. **Status page split (C40).** Default: doc 23 keeps the agent and usage sections; Kodo adds a `health` section to the same `/status` endpoint. Ratify or reject.
4. **Audit log ownership.** Default: doc 15 keeps the `audit_log` table until doc 48 is written, and Kodo's journal stays separate and cross-referenced. Confirm.
5. **External anchor defaults.** Default: the owner's paired devices, plus an optional RFC 3161 authority when the owner configures one. Is that enough, or should an object-locked store be on by default when the operator has one?
6. **Kodo's licence for its SDK packages (section 19, item 3).** Apache-2.0 as before, or AGPL-3.0 with the module exception?
