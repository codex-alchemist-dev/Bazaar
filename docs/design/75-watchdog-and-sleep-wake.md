# 75. Watchdog and Sleep/Wake: kicks, silence events and idle-service sleeping

Status: PLANNED. Design intent only. Nothing here is built; no project, migration or pane exists. The owner's direction of 2026-11-19 to adopt the Resibo watchdog and the Resibo supervisor's sleep/wake feature as Bazaar modules is DECIDED (sections 2 and 4). Everything else is PROPOSED or OPEN. These two modules extend Kodo as separate modules. They do not change Kodo's decided design (doc 46).
Depends on: 46-kodo-health-module.md (health contract, A/B slots, sentinel, probes), 03-server-host-and-providers.md (startup, instance lock, service host, settings registry), 42-deps-as-modules.md (out-of-process dependencies, host-owned process handles), 40-core-feature-modules-audit.md and BAZAAR-COMPONENTS.md (module layout, graph, `Principal`, migration namespaces), 06-agent-runtime.md (leases, wake model), 16-agent-vms-and-environments.md (VM idle save), 49-gap-decisions-operations.md (rings, pins, rollback), 15-security-taint-and-approvals.md (approval tiers, audit).
Used by: 46 (Kodo reads both modules), 16 and 20 (sleepable VMs and engine bridges), 05 (gateway sidecars), 27 to 30 (Monitor sub-views), 34 (test strategy).

## 1. Purpose

Two Resibo features have no Bazaar equivalent:

- **Watchdog.** Every long-running service calls in periodically ("kick") with its instance id and version. A service that stops kicking is reported silent. This catches a process that is hung but not crashed, which a process-table check and a pull-style liveness probe both miss. Silence raises events; the watchdog never restarts anything.
- **Sleep/wake.** Services that are idle most of the time stop, and a defined trigger wakes them on demand. This frees memory and VRAM on the single server node (doc 49 section 1) without changing what the services do.

Both are needed for the same reason Kodo exists: a persistent company must notice a stuck loop and must not waste the one machine it runs on. They are small, they carry no business logic, and they are modules, so an operator who does not want them leaves them out.

## 2. Decided inputs

- **Adopt (owner, 2026-11-19, DECIDED).** The Resibo watchdog and the Resibo supervisor's sleep/wake are adopted as `Bazaar.Server.Watchdog` and `Bazaar.Server.SleepWake`, each with its own Abstractions project. Where a Resibo detail conflicts with Bazaar's design it becomes a separate module instead of being chosen over Bazaar's (owner direction, 2026-11-19; the brief for this doc). Source of the direction: the 2026-11-19 owner note relayed with this task, and `CONVERGENCE.md` rows 3, 4, B5 and B6.
- **Kodo owns slot choice and the health contract (doc 46, DECIDED 2026-11-13).** A/B slots, the sentinel, shadow-mode probation, rollback and the mandatory module health contract are not restated or changed here.
- **Providers are plain modules on Core, no selector (doc 03 provider paragraph).** Activation mechanics are provider modules. A consumer receives all registered providers and decides what to do with them.
- **`Principal` (owner, 2026-11-19; BAZAAR-COMPONENTS section 4.2).** Every actor field below is a `Principal` in canonical `kind:id` text.
- **Configurability rule (DECISIONS-LOG round 13).** Open values are operator settings with safe defaults. DECIDED items and security invariants are floors, not settings. Settings may tighten, never loosen.
- **Module layout (doc 40, BAZAAR-COMPONENTS section 2.1).** Server half `Bazaar.Server.<Name>` plus `.Abstractions`; client half `Bazaar.Client.<Name>`. No feature code in `Bazaar.Server`, `Bazaar.Client.Core` or `Bazaar.Core`. Shared libraries hold contracts and logic only, with no third-party dependencies; providers are separate modules.
- **Host owns child processes (doc 42 section 6.2).** The host starts, stops and supervises dependency processes. No other process may.

## 3. What this doc does not decide

- Kodo's catalogue, evaluator, state machine, runbooks and Responder. This doc states what these modules give Kodo and what small Kodo additions it proposes (section 5.6). The additions are PROPOSED and need the owner, because Kodo's signed catalogue changes only by update.
- The update policy engine, channels, rings, snapshots and the pin record (doc 49 section 7, and the module manager in `src/Bazaar.Server.Modules`). This doc only reads pins.
- The VM lifecycle and idle save (doc 16). SleepWake adapts to it; it does not replace it.
- A Resibo Task Scheduler equivalent. Section 5.4 defines only the wake-schedule table that sleeping needs.
- The extracted Ouroboros updater. It is not in the repository today (`src` has `Bazaar.Updates` and `Bazaar.Server.Modules`; no Ouroboros project). Sections 5.8 and 5.9 define seams only.
- Sleep behaviour of client apps. This is server modules only.

## 4. Identity and naming

> WORKING NAME. Module and project names are proposals, not owner-named, except that the owner named the two features.

| Item | Watchdog | SleepWake |
|---|---|---|
| Component id | `watchdog` | `sleep-wake` (migration namespace `sleep_wake`, per `ComponentManifest.MigrationNamespace`) |
| Server module | `Bazaar.Server.Watchdog` | `Bazaar.Server.SleepWake` |
| Contracts | `Bazaar.Server.Watchdog.Abstractions` | `Bazaar.Server.SleepWake.Abstractions` |
| Providers (own projects) | none | `Bazaar.Server.SleepWake.Systemd` (Linux socket unit), `Bazaar.Server.SleepWake.ActivationProxy` (managed loopback relay, first tested on Windows) |
| Client half | `Bazaar.Client.Watchdog` (Monitor sub-view, optional) | `Bazaar.Client.SleepWake` (Monitor sub-view, optional) |
| Kind | `feature` | `feature` |
| `DependsOn` | none | none |
| `SoftDependsOn` | `kodo` | `watchdog`, `kodo` |
| Settings sections | `Bazaar:Watchdog` | `Bazaar:SleepWake` |
| Version | plain SemVer with commit appended, as for every module | same |

Soft edges follow BAZAAR-COMPONENTS section 4.1: they never set start order and a missing target degrades only the features that name it. Neither module can depend on Kodo for correctness: Kodo reads them, they do not need Kodo.

Core additions (section 5.1) land in `Bazaar.Core` as kernel lifecycle code, not as a feature module. That is a kernel change and needs the owner's confirmation (section 11, Q1).

## 5. Design

### 5.1 Core lifecycle hooks: ordered launch, readiness gating, single instance, boot report

Resibo's supervisor launches services in dependency order, waits for each service's first health check, fails closed, and reports progress to a boot screen (`supervisor/boot_sequence.py`: `topological_order`, `launch_one`, `boot_many` with `on_result`). Bazaar already has half of this: `ComponentGraph` validates hard edges and orders start (`src/Bazaar.Core`), and BAZAAR-COMPONENTS section 4.1 says readiness gates follow hard edges. What is missing is the gate itself, and a record of what happened. These are Core hooks, not a new module.

PROPOSED additions to `Bazaar.Core` (additive; existing `IBazaarComponent` is unchanged):

```csharp
namespace Bazaar.Core;

/// Optional. A component that has a start or stop step beyond AddServices implements this.
public interface IComponentLifecycle
{
    ValueTask StartAsync(ComponentStartContext context, CancellationToken ct);
    ValueTask StopAsync(ComponentStopContext context, CancellationToken ct);
}

/// Kernel asks this after StartAsync. The default (none registered) is "ready once started".
/// The module manager supplies an adapter that answers from IModuleHealthContract.ReadinessAsync.
public interface IComponentReadiness
{
    ValueTask<ReadinessVerdict> CheckAsync(CancellationToken ct);
}
public sealed record ReadinessVerdict(bool Ready, string? Reason);

public enum BootStepState { Pending, Starting, WaitingReady, Ready, Failed, Blocked, Skipped }
public sealed record BootStep(string ComponentId, BootStepState State, DateTimeOffset? StartedAt,
    DateTimeOffset? ReadyAt, string? Reason, int Level);
public sealed record BootReport(DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, IReadOnlyList<BootStep> Steps, bool Complete);

public interface IBootObserver { void OnStep(BootStep step); }   // many allowed; Watchdog and Kodo register one
public interface IBootReportSource { BootReport Current { get; } }

/// Named instance locks. The host's Postgres advisory lock of doc 03 step 3 is the scope "host".
public interface IInstanceLock
{
    ValueTask<IAsyncDisposable?> TryAcquireAsync(string scope, CancellationToken ct);
}
```

Rules:

1. **Order and parallelism.** Start order is the existing topological order over hard edges. Components in the same topological level start concurrently, bounded by `core.boot.max_parallel` (default 4). Resibo launches strictly one at a time; Bazaar can overlap independent branches because components share one process and the cost is only startup latency. Ties still break by id so the report is stable.
2. **Readiness gate.** After a component's `StartAsync`, the kernel polls its `IComponentReadiness` until ready or `core.boot.ready_timeout_seconds` (default 30, Resibo's `DEFAULT_HEALTH_TIMEOUT_SECONDS`). Dependents that hard-depend on it start only after it is ready.
3. **Failure.** A component that does not become ready is `Failed`. Every component that hard-depends on it, directly or not, is `Blocked` and never started. Components on other branches continue. A soft dependency that fails never blocks (the dependent reports amber, per BAZAAR-COMPONENTS 4.1). If the failed component is one doc 03 step 5 treats as required (a required readiness role), the host refuses to start, as doc 03 already says. Resibo stops at the first failure of any kind; Bazaar narrows that to the failed branch because a broken optional module should not take down GCs. Fail closed still holds for hard edges and required roles.
4. **Reporting.** Every step change calls each registered `IBootObserver` and updates `IBootReportSource`. The report is read-only data: Kodo reads it for `host.ready` context, the Watchdog module exposes it on its read API (5.2), and the server admin panel (doc 51) can show a boot progress view. A boot screen in a client is a presentation of this report, never its own logic.
5. **Stop.** Reverse order, each component's `StopAsync` bounded by doc 03's `host.shutdown_timeout_seconds`. Crash-only holds: nothing may rely on `StopAsync` having run (doc 46 section 7.2).
6. **Single instance.** `IInstanceLock` generalises doc 03's lock into named scopes. `host` is doc 03's existing Postgres advisory lock and stays authoritative for the server. A module that launches an out-of-process service takes scope `module:<id>` (or `module:<id>:<version>` for multi-instance, section 5.9) before spawning; a second attempt fails with a clear reason instead of starting a duplicate. Managed Postgres (doc 42 section 6.2) is the obvious first user: the host owns one handle and the lock makes a duplicate start impossible even from a second host process.

Where Resibo's `single_instance.py` restart-on-version applies, see 5.8.

### 5.2 Watchdog module

Resibo model (doc `v3-deepdive-34-watchdog.md`, code `core/health/watchdog/`): inversion of control (services call in, the watchdog never reaches into a service), `Heartbeat(service, instance_id, version_commit, kicked_at)`, `check_for_silence()` on its own interval, a global timeout with per-service overrides, `UNSEEN` distinct from `SILENT`, version tracking riding the heartbeat only, and **no restart anywhere in the package**: the detector is deliberately not the code that acts.

#### Contracts (`Bazaar.Server.Watchdog.Abstractions`)

```csharp
public enum Liveness { Alive, Silent, Unseen, Starting, Suspended }

public sealed record WatchedServiceDescriptor(
    string ServiceId,
    string ModuleId,
    InstanceMode Mode,                 // Single (default) or Multi, with a reason string (5.9)
    int? KickIntervalSeconds,          // null = module default setting
    int? TimeoutSeconds,               // null = default; operator may lower, not raise past a floor
    string? MultiReason);

public enum InstanceMode { Single, Multi }

public sealed record Heartbeat(string ServiceId, string InstanceId, string Version, DateTimeOffset KickedAt);

public sealed record WatchdogState(string ServiceId, string InstanceId, Liveness Liveness,
    DateTimeOffset? LastKickAt, string Version, double SilentForSeconds, int TimeoutSeconds);

public sealed record SilenceReport(IReadOnlyList<WatchdogState> States, DateTimeOffset CheckedAt)
{
    public IEnumerable<string> SilentServices => States.Where(s => s.Liveness == Liveness.Silent).Select(s => s.ServiceId);
}

/// Hot path. No I/O, no allocation beyond the stored heartbeat.
public interface IWatchdogClient
{
    void Kick(string serviceId, string instanceId, string version);
}

public interface IWatchdogRegistry
{
    void Register(WatchedServiceDescriptor descriptor);
    /// A retired instance is dropped so it does not stay Silent forever. Audited.
    void Retire(string serviceId, string instanceId, Principal by, string reason);
}

public interface IWatchdogReader
{
    SilenceReport Check();                                        // current view, never restarts
    WatchdogState State(string serviceId, string instanceId);
    IReadOnlyDictionary<string, IReadOnlyList<string>> FleetVersions(); // service -> distinct versions
}

/// SleepWake (and anything that deliberately stops a service) calls this so a stopped service is not "silent".
public interface IWatchdogSuspension
{
    void Suspend(string serviceId, string instanceId, Principal by, string reason);
    void Resume(string serviceId, string instanceId, TimeSpan startupGrace);
}

/// Piggyback seam: a lease or resource ledger can extend its TTL on a kick (CONVERGENCE row 4). No ledger is defined here.
public interface IKickListener { void OnKick(Heartbeat heartbeat); }

/// Silence and recovery events go to every registered sink. Sinks are plain modules (Kodo observation, audit, push alert).
public interface IWatchdogEventSink { ValueTask PublishAsync(WatchdogEvent evt, CancellationToken ct); }
public sealed record WatchdogEvent(string Kind, string ServiceId, string? InstanceId, string? Version,
    double? SilentForSeconds, Principal Actor, DateTimeOffset At);
```

Event kinds: `silent`, `recovered`, `unseen_expired` (a registered service never kicked within its startup grace), `version_changed`, `retired`, `detector_stalled`.

#### Behaviour

- **Kick path.** In-process services call `IWatchdogClient.Kick` directly from their own loop (turn runner, job poller, GC NOTIFY listener, scheduler). The registry is a concurrent map keyed by `(serviceId, instanceId)`; instance id is part of the key so a healthy new instance cannot vouch for a hung old one during an A/B or multi-version overlap (Resibo `KickRegistry`). The map is in memory only. A kick does no database or file I/O.
- **Out-of-process kicks.** Dependencies and sidecars (managed Postgres supervisor, FreeLLMAPI sidecar, engine bridges, the VM guest agent of doc 16) kick over an authenticated loopback endpoint of the wire surface (doc 02). Each launched service receives a per-instance kick token from Identity (doc 14) through the same channel doc 42 uses for database credentials (never a command-line argument). Transport (HTTP or named pipe) is OPEN (Q3). A service that cannot call in cannot be hang-detected; it remains covered by Kodo's pull probes and the host's process supervision.
- **Detector.** One hosted service runs `Check()` every `watchdog.check_interval_seconds` (default 5). An instance silent longer than its timeout becomes `Silent` and publishes one `silent` event per transition (not per tick). `Unseen` is never `Silent`: a service that has not started is not one that stopped, and restarting a never-started service acts on a fiction (Resibo `state_for`). `Unseen` becomes an `unseen_expired` event only after the startup grace.
- **Starting and Suspended.** Bazaar adds two states Resibo lacks, needed once services can sleep and start late. `Starting` covers a new or just-woken instance until its first kick or the startup grace ends. `Suspended` is set through `IWatchdogSuspension` by SleepWake (or a host shutdown) and is never reported silent.
- **Timeouts.** Default kick interval 15 seconds and timeout 60 seconds, as in Resibo. Per-service override map (Resibo's resolution of its open question: model load needs a longer cycle than a light heartbeat). Floor: timeout is never below twice the kick interval (PROPOSED).
- **Version tracking.** The heartbeat carries the version; no other message does (Resibo section 5). `FleetVersions()` returns more than one version per service during a hot swap; that is information, not a conflict. Version strings are Bazaar's `0.MM.PP+commit` form.
- **No actor.** `Bazaar.Server.Watchdog` has no restart, kill or start method, and an architecture test asserts it references no process API and no service-host provider (section 9). Detector and actor stay separate so the code deciding "restart this" is never the code that may itself be hung. The actors are: Kodo's Responder through approval-gated runbooks (doc 46 section 16.2), the host's supervision of its own children (doc 42 section 6.2) and the sentinel (doc 46 section 7.1).
- **The detector watches itself.** Its loop stamps `LastCheckAt`; `LivenessAsync` fails if that is older than three check intervals, and a `detector_stalled` event is raised by the next successful pass. Kodo's pull probe on the Watchdog module therefore catches a stalled detector.
- **Read API.** Under the authenticated surface, `GET /api/watchdog/v1/state`, `/silence`, `/versions`, `/boot`. Read-only; nothing here mutates.

### 5.3 SleepWake module

Resibo model (`v3-deepdive-38-supervisor.md` section 6, `supervisor/sleep_wake/`): a `SleepPolicy` of `NEVER`, `IDLE_TIMEOUT` or `SCHEDULED_ONLY`; a static classification table that defaults every unclassified service to `NEVER` (fail closed); in-memory live state; a schedule table for timed wakes; `ForceWake` audited; Linux wake by systemd socket activation, Windows wake by a custom activation proxy.

#### Contracts (`Bazaar.Server.SleepWake.Abstractions`)

```csharp
public enum SleepPolicy { Never, IdleTimeout, ScheduledOnly }
public enum SleepState { Running, Draining, Sleeping, Waking, Failed, NeverSleeps }
public enum WakeReason { Request, Schedule, Dependency, Event, Manual, SelfTest }

/// A module declares what it can do. An operator can only choose among what the module allows.
public sealed record SleepProfile(
    string ServiceId,
    string ModuleId,
    IReadOnlySet<SleepPolicy> Allowed,       // empty = never sleepable
    SleepPolicy DefaultPolicy,               // shipped default; Never unless the module says otherwise
    TimeSpan DefaultIdleTimeout,             // 30 minutes, Resibo idle_timeout_minutes
    TimeSpan DrainTimeout,
    IReadOnlyList<ActivationMode> Activation);

public enum ActivationMode { None, Relay, SocketUnit }

public interface ISleepRegistry { void Register(SleepProfile profile); }

public interface ISleepStateReader
{
    SleepStatus Status(string serviceId);
    IReadOnlyList<SleepStatus> All();
}
public sealed record SleepStatus(string ServiceId, SleepPolicy Policy, SleepState State, DateTimeOffset? LastActivityAt,
    DateTimeOffset? NextWakeAt, int WakesLastHour);

public interface ISleepCoordinator
{
    /// Coalesces concurrent wakes into one start and awaits readiness. Throws WakeFailedException on timeout.
    ValueTask EnsureAwakeAsync(string serviceId, WakeReason reason, Principal by, CancellationToken ct);
    ValueTask ForceWakeAsync(string serviceId, Principal by, string reason, CancellationToken ct);   // audited
    ValueTask SleepNowAsync(string serviceId, Principal by, string reason, CancellationToken ct);    // audited
}

/// A module (or a lease holder) vetoes sleep while it has real work.
public interface ISleepGuard
{
    string ServiceId { get; }
    ValueTask<SleepVeto?> CheckAsync(CancellationToken ct);   // null = no objection
}
public sealed record SleepVeto(string Reason);

/// Marks real use. Probes and health checks never call this (5.6).
public interface IActivityReporter { void Touch(string serviceId, ActivityKind kind); }
public enum ActivityKind { Request, Lease, InFlight }

/// What the module being slept actually does to stop and start.
public interface ISleepable
{
    string ServiceId { get; }
    ValueTask DrainAsync(CancellationToken ct);
    ValueTask StopAsync(CancellationToken ct);
    ValueTask StartAsync(string? version, CancellationToken ct);
    ValueTask<bool> IsReadyAsync(CancellationToken ct);
}

/// Provider seam (5.4).
public interface IActivationProvider
{
    ActivationMode Mode { get; }
    ValueTask<ProviderStatus> ProbeAsync(CancellationToken ct);            // doc 03 capability reporting
    ValueTask InstallAsync(ActivationSpec spec, CancellationToken ct);
    ValueTask RemoveAsync(string serviceId, CancellationToken ct);
}
public sealed record ActivationSpec(string ServiceId, string ListenAddress, string TargetAddress, string WakeCommand);
```

#### State machine

`Running` to `Draining` when the idle timer expires (or a schedule says stop, or `SleepNowAsync`) and no `ISleepGuard` vetoes. `Draining` stops new work, waits for in-flight work up to `DrainTimeout`, then `StopAsync`, then `Sleeping`. A wake trigger moves `Sleeping` to `Waking`, which runs `StartAsync` then polls `IsReadyAsync` up to `sleepwake.wake_timeout_seconds` (default 30, Resibo `DEFAULT_WAKE_TIMEOUT_SECONDS`), then `Running`. A failed wake becomes `Failed`, publishes an event and an audit entry, and the service is not retried automatically more than `sleepwake.wake_retry_max` times (PROPOSED, default 2). `NeverSleeps` is a fixed state for anything the profile does not allow.

#### Policy rules

- **Fail closed.** A service with no registered profile, or whose profile allows nothing, is `NeverSleeps` (Resibo classification default). An operator setting can move a service to a policy the module allows; it can never enable sleep on a module that did not declare it.
- **Floor, not a setting.** These never sleep: `watchdog`, `sleep-wake`, `kodo` and its sentinel, `identity`, `audit`, the GC log writer, the managed database and the cache/presence provider. Resibo's list is Auth, Gateway, Health, Persistence, Logs and Watchdog (`sleep_wake/classification.py`); the Bazaar equivalents are the hot-path and monitoring components. Sleeping managed Postgres on a fully idle install was considered and is REJECTED for v1: every GC append, NOTIFY listener, agent lease and Kodo journal write needs it, and agents have schedules (work-life simulation), so it is never truly idle.
- **Guards.** Resibo sleeps on a last-activity timestamp alone. Bazaar also consults guards, so a service holding an agent lease (doc 06 `agent_lease`), a job lease (doc 03 `job`) or an in-flight request cannot be slept mid-work. Activity is reported by the module, by the activation endpoint's connection count, and by lease holders.
- **Wake storms.** A service that wakes more than `sleepwake.max_wakes_per_hour` (default 12) is pinned awake and an event is raised, so a flapping client cannot start-stop a heavy process in a loop (the same idea as doc 06's `runtime.guard.wakes_per_hour`). Pinning awake is reported, never silent.
- **Dependency wake.** Waking a service first wakes any of its sleeping hard dependencies, in the Core graph's order (5.1). A hard dependency that fails to wake leaves the dependent `Failed` with the cause named.
- **Schedules.** Resibo's schedule table (service to next wake, checked on a short interval) is kept: `sleep_schedule` rows with `daily_at`, `interval` and `once` kinds, checked every `sleepwake.schedule_tick_seconds` (default 30). Missed wakes do not catch up in a burst (Resibo task-scheduler rule: a missed run waits for the next occurrence). An optional daily maintenance time staggers log rotation and then scheduled wakes a few minutes apart (Resibo `daily_maintenance_time`). Cron expressions are not in v1 because a cron parser would be a new dependency; a later module can supply them (Q5).
- **Coalescing.** Concurrent `EnsureAwakeAsync` calls for one service share one start and one wait (Resibo `ActivationProxy._waking_lock`).

#### What can sleep (candidates, PROPOSED)

| Candidate | Why | Notes |
|---|---|---|
| Local model runtimes (llama.cpp, Ollama-style, doc 05 local runtimes) | VRAM and RAM | Best fit: resident only while a model call is active. Wake on first gateway call, so the gateway must tolerate wake latency and show `Waking` |
| FreeLLMAPI sidecar and other gateway proxies (doc 05) | Idle between lane uses | Wake on first request to its lane. The sidecar's lane health must report `asleep`, not down |
| Engine bridges (Godot headless, Studio relay, doc 20) | Heavy, used per build | Idle timeout; wake on first engine command. Per-engine version instances (5.9) |
| Agent VMs (doc 16) | Memory | Already have Running, Saved and idle save timers owned by the Environment service. See below |
| Browser profile hosts (doc 16 Chromium) | Memory | After the VM idle rule |
| Heavy `runtime` deps (doc 42 section 4) | Run per job already | Already "started per job"; no change |
| Nothing in the never list | Section above | Fixed |

**VM idle save is not duplicated.** Doc 16 owns `vm.idle_save_minutes.linux` and `.windows` and the Saved state. SleepWake offers an `ISleepable` adapter for the VM Environment service (a separate module that doc 16's owner writes, or `Bazaar.Server.SleepWake` references the VM abstractions only), so VMs appear in the same status and wake API. The idle timers stay doc 16's settings: SleepWake reads them, it does not add a second timer. Where the adapter is absent, VMs behave exactly as doc 16 says.

### 5.4 Activation providers (separate modules)

Resibo needs a wake trigger that works while the service is not running: on Linux, systemd socket activation; on Windows, a hand-built always-resident relay, because "no OS primitive exists" (`supervisor/sleep_wake/`). Resibo's own notes say the Linux runtime socket passing is unverified and the Windows relay is live-confirmed. Bazaar's rules (one OS-agnostic build; OS-specific code behind providers; host owns child processes, doc 42 section 6.2) make both providers rather than core code.

**`Bazaar.Server.SleepWake.ActivationProxy`** (`ActivationMode.Relay`). A managed `System.Net.Sockets` loopback listener on the service's public address. On the first connection while the target is not reachable it calls `ISleepCoordinator.EnsureAwakeAsync(..., WakeReason.Request, Principal service:activation-proxy)`, then relays bytes both ways to the target address; later connections relay immediately. The relay holds the connection while `Waking`, bounded by `wake_timeout_seconds`, and closes it with a clear error on timeout. It has no OS API, no third-party dependency, and runs on every OS; it is first tested on Windows because that is the first supported host (CLAUDE.md). It counts connections as `ActivityKind.Request`. Bind is loopback only, never wider, a floor not a setting.

**`Bazaar.Server.SleepWake.Systemd`** (`ActivationMode.SocketUnit`). Generates a `.socket` and a paired unit for the `systemd-socket-proxyd` shape (the fallback Resibo names), not runtime socket passing to the service, because Resibo's socket-passing path is unverified (Resibo `supervisor/CLAUDE.md`) and because the host must remain the owner of the real process handle (doc 42 section 6.2). The proxy unit's pre-start step runs the host's local `wake <serviceId>` command, which calls the coordinator; systemd then forwards traffic to the already-started child. Unit text generation is pure and unit-tested; installing units needs the same privilege step as doc 03's systemd unit and is OPEN for unprivileged installs (Q6). `ProbeAsync` reports unavailable on a host without a responsive `systemctl`, as Resibo's `is_systemd_available()` does.

Both providers register as plain modules. SleepWake receives all registered providers; each `SleepProfile` lists the modes it can use in preference order, and SleepWake picks the first mode that has a usable provider. A service whose only mode has no usable provider is `NeverSleeps` and says so in its status, so a missing provider degrades sleeping, never availability.

Third kinds of wake, such as an in-process call, need no provider: `ActivationMode.None` means callers must use `EnsureAwakeAsync` themselves (as the agent scheduler does for VMs and the gateway does for local runtimes).

### 5.5 Boot sequence reporting

Covered by 5.1. Two additions on the Watchdog side. First, after boot completes, every service in `BootReport` that has a descriptor is moved from `Starting` to `Unseen` or `Alive` by its first kick, so a boot step that reached `Ready` through the readiness gate but never kicks is raised as `unseen_expired`. This closes the gap Resibo records in its own notes: its boot gate was a reachability probe and "no Core API calls Kick yet" (about 25 servicers). In Bazaar the Watchdog registration is part of the module's own health contract tests (section 9), so the gap cannot recur. Second, the report is retained for the last `watchdog.boot_report_retention` boots (default 20) in `watchdog_boot` so Kodo's time-travel can show what started and how long each step took.

### 5.6 How sleep/wake interacts with Kodo health scoring

**A sleeping module is not unhealthy.** Kodo polls `module.<id>.health` every 30 seconds (doc 46 section 9.1). If that poll reached a stopped process it would return Fail or Unknown, and a quiet install would look broken. If the poll reached an awake process and counted as activity, nothing would ever sleep. Rules, all within the existing health contract so Kodo's decided design does not change:

1. **Probes are not activity.** Kodo probe traffic and `IModuleHealthContract` calls never call `IActivityReporter.Touch` and never wake. The activation endpoint distinguishes probe connections by the authenticated caller, `service:kodo`.
2. **A sleeping stub answers.** For every `Sleeping` service SleepWake registers a stand-in `IModuleHealthContract` (doc 46 section 10, `src/Bazaar.Server.Modules.Abstractions/Health/IModuleHealthContract.cs`). It returns Liveness `Pass` and Readiness `Pass` with facts `sleep=asleep`, `policy`, `last_activity`, `next_wake`, and **only while the activation path is intact** (the provider's `ProbeAsync` reports the listener alive). If the activation path is broken, readiness is `Fail` with reason `activation_unavailable`, because a service that cannot be woken is unavailable.
3. **Self-test.** The contract tests reject a self-test that passes without running anything (doc 46 section 10.3 item 7). The stub therefore does not claim a pass. It reports the last real self-test result from before the sleep, with facts `stale=true` and `asleep=true`, and `Quick` is satisfied from that record. The scheduled `Full` self-test wakes the module (`WakeReason.SelfTest`) when `sleepwake.selftest_wakes` is on (default on), runs, and lets it sleep again. Kodo's `selfcheck` timing is unchanged.
4. **Waking and Failed are visible.** `Waking` reports Readiness `Warn` reason `waking`. `Failed` reports `Fail` and is a real unhealthy state that Kodo scores normally.
5. **Hysteresis.** Kodo's grace and flap rules (doc 46 section 8.2) already prevent a wake cycle from flapping the state; `Waking` is shorter than any grace.
6. **Score.** Because the stub passes, a sleeping module contributes its normal green. The score and the caps (doc 46 section 12.2) need no change.

PROPOSED Kodo additions (not applied here; the owner decides; Kodo's catalogue is signed, so they land as a Kodo update):

- Probe rows `watchdog.silent_services` (critical when the silent service is marked critical in its descriptor, otherwise non-critical, cadence 10 s, pool local) and `sleepwake.activation` (non-critical, 60 s, local). They read `IWatchdogReader` and `ISleepStateReader` through those modules' health contracts.
- A cleaner long-term fix is a fifth `HealthStatus` value `Suspended` that the evaluator treats as green and excludes from `unknown` budgets. That changes the decided contract (doc 46 section 10.1) and is OPEN (Q2). The stub approach works without it.

The same stub idea fixes an existing gap: doc 46's `agents.vm.<vm>.reachable` probe would read a Saved VM (doc 16) as unreachable. Reading `ISleepStateReader`, the probe can report `Pass` with `asleep`. That is a probe-catalogue note for Kodo, not an edit here.

### 5.7 Self-update and re-exec: mapped to Kodo's slot model

Resibo's supervisor cannot update itself the way it updates everything else, so it uses a two-phase re-exec: stage the new build where the running supervisor does not touch; launch it with `--smoke-test` (read config, locate releases, report healthy, touch no live state); hand off only on a pass; otherwise the old one keeps running untouched; owner confirmation is mandatory and never configurable off; the candidate soaks 14 days (`supervisor/self_update/reexec.py`). Bazaar already has this shape, so nothing is duplicated:

| Resibo | Bazaar (already decided, doc 46) |
|---|---|
| The supervisor, kept minimal because it cannot self-update normally | The sentinel, smaller than Kodo, no Bazaar dependencies, A/B-versioned (doc 46 sections 5.2, 6.7) |
| Stage new build in a separate place | Write the new version into the inactive slot, verify the manifest (6.3) |
| `--smoke-test` run touching no live state | Shadow mode for a probation window: observes, does not act (6.3) |
| Hand off arbitration, then exit | Flip the `current` pointer by atomic rename (6.3); the host is restarted from the new slot by the sentinel; crash-only means no in-process handoff is needed |
| Failure leaves the old one running | Probation failure leaves the pointer unchanged; rollback is the same flip and needs no network (6.3) |
| Revert `ActiveRelease` | Slot rollback (6.3); server rollback via the snapshot (doc 49 section 7) |
| `TriggerRollback` on a channel | Ring auto-halt and approval-gated rollback runbooks (doc 46 section 16.6, doc 49 section 6) |

Kodo decides the slot; Watchdog and SleepWake neither choose a slot nor restart a process. Two Resibo rules are not yet in Bazaar's docs and are offered as PROPOSED additions for the owner (Q4):

- **Owner confirmation for the sentinel's own update.** Resibo makes confirmation mandatory and permanent for the one component that cannot roll itself back. The sentinel is that component in Bazaar. Proposal: a sentinel slot flip needs a Tier 2 approval (doc 15) as a floor, not a setting.
- **Longer soak for the sentinel.** Resibo's 14-day soak for supervisor candidates. Proposal: doc 49's ring policy gets a longer soak for the sentinel and for Kodo's verifier (a follow-up to doc 49; no setting is added to doc 49's namespace here).

### 5.8 Single-instance restart-on-version

Resibo's `single_instance.py` restarts a single-instance service on a requested version in a strict order: confirm the target release exists and is launchable, stop the old instance, launch the new one, wait for its health check. The step-1-before-step-2 order is the mitigation, because once the old instance is stopped nothing is left to fall back to. Resibo notes honestly that step 1 only checks that the clone and its environment exist, because it has no dry-run launch.

Bazaar already has this: the module manager's line-move machinery (`src/Bazaar.Server.Modules/LineMove`: `LineMovePreflight`, `LineMoveRunner`, `DryRunRunner`, `ILineMoveStepExecutor`) plus doc 49's apply, verify and automatic rollback to a snapshot. This doc reuses it and adds nothing parallel. The mapping is: Resibo step 1 is preflight (and Bazaar has a real dry run, which is stronger than Resibo's), steps 2 to 4 are the executor's stop, start and verify steps, and "wait for its health check" is the Core readiness gate (5.1). I read these file names, not their contents; the exact seam is for the module-manager owner to confirm.

What this doc adds around it: a module's restart sets `IWatchdogSuspension.Suspend` before the stop and `Resume` with a startup grace after the start, so a deliberate restart never raises `silent`.

### 5.9 Version pins and dynamic version start (where an Ouroboros-style updater applies)

Resibo has two related pieces: `ServiceVersionPin(channel, service_name, pinned_version|None, pinned_by, pinned_at)` (a surgical alternative to a whole-channel rollback, audited, surfaced on a Fleet screen), and `dynamic_start.ensure_version_running` (several concurrent version instances of one service, started on demand and not pre-booted; two single-instance exceptions enforced structurally).

Bazaar mapping:

- **Pins are doc 49's.** Doc 49 section 7 already has "Pin: hold a module or the server at the current version, showing its owner and reason, expiring only if the policy says so". It is not duplicated. SleepWake reads pins through a port, `IVersionPinReader` (defined in `Bazaar.Server.SleepWake.Abstractions`, implemented by the module manager or the updater), so a wake starts the pinned version. Gap: Resibo's pin is scoped per channel and per service; doc 49's pin as written is per module or server with no channel scope. A channel-scoped pin is a follow-up for doc 49 (Q7); until then SleepWake treats a pin as install-wide.
- **Dynamic start is a wake with a version.** `ISleepable.StartAsync(version)` and `IReleaseLocator` (resolve a version to a release directory or package, the Resibo `find_release_dir_for_version`) generalise "wake" to "wake this version". The instance key becomes `(serviceId, version)`, so two versions of one out-of-process module can run at once. The best real use is engine bridges, where doc 20 already pins an engine version per project (for example the Godot 4.7.2 pin) and two projects can need two versions; each version instance sleeps independently when idle.
- **Single is the default.** Resibo defaults most services to multi-version and names two single-instance exceptions. Bazaar inverts it: every service is `InstanceMode.Single` unless its descriptor says `Multi` with a written `MultiReason`, shown in the Monitor sub-view. That is the same fail-closed stance as the sleep default. Attempting a second version of a `Single` service is refused, not queued; the supported path is restart-on-version (5.8).
- **The locator is a seam.** `IReleaseLocator` is implemented by `Bazaar.Updates` / the module manager today and by the extracted Ouroboros updater later (CONVERGENCE decided Ouroboros; it is not in `src`). If no locator is registered, only the current version can start and dynamic start is off. In-process modules never use any of this: they share the host and the host's slot.

### 5.10 Data (migration namespaces)

Per BAZAAR-COMPONENTS section 5: `src/Bazaar.Server.Watchdog/Data/Sql/watchdog/NNNN_*.sql` and `src/Bazaar.Server.SleepWake/Data/Sql/sleep_wake/NNNN_*.sql`. Table and column names are PROPOSED. Kicks are never written; only transitions and registrations are.

```sql
-- watchdog/0001_watchdog.sql
CREATE TABLE watchdog_service (
  service_id        text PRIMARY KEY,
  module_id         text NOT NULL,
  instance_mode     text NOT NULL CHECK (instance_mode IN ('single','multi')),
  multi_reason      text,
  kick_interval_s   integer,
  timeout_s         integer,
  critical          boolean NOT NULL DEFAULT false,
  registered_at     timestamptz NOT NULL DEFAULT now(),
  CHECK (instance_mode = 'single' OR multi_reason IS NOT NULL)
);

CREATE TABLE watchdog_event (
  id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  at            timestamptz NOT NULL DEFAULT now(),
  service_id    text NOT NULL,
  instance_id   text,
  kind          text NOT NULL,       -- silent, recovered, unseen_expired, version_changed, retired, detector_stalled
  version       text,
  silent_for_s  integer,
  actor         text NOT NULL        -- Principal text, kind:id
);
CREATE INDEX watchdog_event_service_at ON watchdog_event (service_id, at DESC);

CREATE TABLE watchdog_version_seen (
  service_id   text NOT NULL,
  instance_id  text NOT NULL,
  version      text NOT NULL,
  first_seen   timestamptz NOT NULL DEFAULT now(),
  last_seen    timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (service_id, instance_id, version)
);

CREATE TABLE watchdog_boot (
  boot_id      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  started_at   timestamptz NOT NULL,
  finished_at  timestamptz,
  complete     boolean NOT NULL DEFAULT false,
  report       jsonb NOT NULL       -- BootReport
);
```

```sql
-- sleep_wake/0001_sleep_wake.sql
CREATE TABLE sleep_policy (
  service_id       text PRIMARY KEY,
  policy           text NOT NULL CHECK (policy IN ('never','idle_timeout','scheduled_only')),
  idle_timeout_s   integer,
  set_by           text NOT NULL,    -- Principal text
  set_at           timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE sleep_schedule (
  id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  service_id     text NOT NULL,
  kind           text NOT NULL CHECK (kind IN ('daily_at','interval','once')),
  spec           text NOT NULL,       -- 'HH:mm' + timezone, ISO-8601 duration, or an instant
  next_wake_at   timestamptz NOT NULL,
  enabled        boolean NOT NULL DEFAULT true,
  created_by     text NOT NULL        -- Principal text
);
CREATE INDEX sleep_schedule_due ON sleep_schedule (next_wake_at) WHERE enabled;

CREATE TABLE sleep_transition (
  id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  at          timestamptz NOT NULL DEFAULT now(),
  service_id  text NOT NULL,
  version     text,
  from_state  text NOT NULL,
  to_state    text NOT NULL,
  trigger     text NOT NULL,          -- idle, schedule, request, dependency, manual, selftest, guard_veto
  actor       text NOT NULL,          -- Principal text
  duration_ms integer
);
CREATE INDEX sleep_transition_service_at ON sleep_transition (service_id, at DESC);
```

Live sleep state is in memory, as in Resibo: which children are asleep is a fact about this host process and its own children, which are all gone after a crash (Job Object kill-on-close, doc 42 section 6.2), so nothing meaningful is reloaded. Only transitions are persisted, for history and time-travel. Retention for both event tables follows Kodo's raw-series retention settings (doc 46 section 20) unless the operator sets their own.

### 5.11 Settings registry rows (safe defaults)

All install scope. Floors are stated; a value that breaks a floor is rejected. Each change writes an audit entry with the changer's `Principal`.

| Key | Type | Default | Who may change | Approval |
|---|---|---|---|---|
| `core.boot.max_parallel` | int | 4 | operator | no |
| `core.boot.ready_timeout_seconds` | int | 30 | operator | no |
| `watchdog.kick_interval_seconds` | int | 15 | operator | no |
| `watchdog.timeout_seconds` | int | 60 | operator | no (floor: at least 2x interval) |
| `watchdog.timeout_seconds.<service>` | int | unset | operator | no (same floor) |
| `watchdog.check_interval_seconds` | int | 5 | operator | no |
| `watchdog.startup_grace_seconds` | int | 120 | operator | no |
| `watchdog.boot_report_retention` | int | 20 | operator | no |
| `watchdog.events.sinks` | list | audit, kodo | operator | no; adding a push or webhook sink follows doc 46 channel rules |
| `sleepwake.enabled` | bool | false | operator | no (off until the operator opts in) |
| `sleepwake.policy.<service>` | enum | module default, `never` unless the module declares otherwise | operator | no; cannot exceed the module's allowed set; floor list is never sleepable |
| `sleepwake.idle_timeout_minutes.<service>` | int | 30 | operator | no (floor: 1) |
| `sleepwake.drain_timeout_seconds` | int | 30 | operator | no |
| `sleepwake.wake_timeout_seconds` | int | 30 | operator | no |
| `sleepwake.wake_retry_max` | int | 2 | operator | no |
| `sleepwake.max_wakes_per_hour` | int | 12 | operator | yes if loosened (owner) |
| `sleepwake.schedule_tick_seconds` | int | 30 | operator | no |
| `sleepwake.daily_maintenance_time` | time or null | null | operator | no |
| `sleepwake.selftest_wakes` | bool | true | operator | no |
| `sleepwake.relay.bind` | string | loopback | n/a | floor: never wider than loopback |

`sleepwake.enabled` defaults to false so an install behaves exactly as before until the operator turns sleeping on in the setup wizard or advanced settings. Watchdog has no enable key beyond the component's own enable/disable (BAZAAR-COMPONENTS section 3); it is read-only and cheap.

### 5.12 Audit events (Principal-attributed)

Written to the audit log (`IAuditLog.AppendAsync` shape `AuditEntry(Ts, Actor, Kind, Payload)`; `Actor` is `Principal` text). Payloads carry ids and versions only, never secrets or content (doc 15, doc 48).

| Kind | Actor | When |
|---|---|---|
| `watchdog.service.registered` | `service:<module>` | A descriptor is registered, including `Multi` with its reason |
| `watchdog.service.silent` | `service:watchdog` | Silent transition |
| `watchdog.service.recovered` | `service:watchdog` | Kick resumes after silence |
| `watchdog.service.retired` | the caller | `Retire` |
| `watchdog.service.version_changed` | `service:watchdog` | A new version kicks for a known instance id |
| `watchdog.setting.changed` | the changer | A `watchdog.*` setting |
| `sleepwake.slept` | `service:sleep-wake` or the manual actor | `Sleeping` reached; trigger recorded |
| `sleepwake.woke` | the requester (`service:gateway`, `agent:<id>`, `user:<id>`) | `Running` reached after `Waking` |
| `sleepwake.wake_failed` | `service:sleep-wake` | Wake timeout or start failure |
| `sleepwake.force_wake` | the user | `ForceWakeAsync` (Resibo audits it) |
| `sleepwake.pinned_awake` | `service:sleep-wake` | Wake storm limit |
| `sleepwake.policy.changed` | the changer | Policy or schedule change |

The audit log is not queried on the kick path; kicks are never audited.

### 5.13 Kodo health contract for the two modules

Each module implements `IModuleHealthContract` (doc 46 section 10) and inherits `ModuleHealthContractTests<T>`.

**Watchdog.** Liveness: no I/O; fails if `LastCheckAt` is older than three check intervals. Readiness: Pass when the registry accepts kicks and the detector has run; Warn when a critical service is `Silent` (readiness of Watchdog is about Watchdog; the silence itself reaches Kodo through the `watchdog.silent_services` probe, 5.6). Dependencies: soft `kodo`. Self-test: `Quick` kicks a synthetic instance with an injected clock, advances past the timeout and asserts `Silent`, then retires it; `Full` runs the same in a sandbox scope and checks `LeftNoResidue` (no rows or registry entries remain).

**SleepWake.** Liveness: no I/O; the coordinator loop stamp is recent. Readiness: Pass when at least one usable activation provider exists for every sleepable service that is currently `Sleeping`, otherwise Warn for a degraded profile and Fail only if a `Sleeping` service has no usable activation path. Dependencies: soft `watchdog`, `kodo`. Self-test: `Quick` drives a fake `ISleepable` through Running, Draining, Sleeping and Waking against a fake clock; `Full` does so with the relay provider on an ephemeral loopback port and requires the port released and no row residue.

## 6. What is taken from Resibo, and what changes

| Resibo item (path) | Taken | Changed for Bazaar, and why |
|---|---|---|
| Watchdog kicks and inversion (`docs/apis/v3-deepdive-34-watchdog.md`, `core/health/watchdog/kicks.py`) | Kick contract, key by `(service, instance_id)`, in-memory hot path | Instance id kept; in-process direct call added; out-of-process via authenticated loopback and a per-instance token (Bazaar secrets rule) |
| Silence detector (`timeout_detector.py`, `contracts.py`) | `ALIVE/SILENT/UNSEEN`, global timeout plus per-service overrides, detector never restarts | Added `Starting` and `Suspended` for sleep and late start; timeout floor; detector self-liveness; architecture test for "no restart" |
| Version tracking (`version_tracking.py`) | Version rides the heartbeat only; multiple versions per service is normal | Bazaar version format; persisted version history |
| Kick client loop (`common/watchdog_client.py`) | One reusable loop every service starts | Becomes a helper in the Abstractions project plus a mandatory contract test, so "no service calls Kick" (Resibo's recorded gap) cannot recur |
| Boot sequence (`supervisor/boot_sequence.py`) | Topological order, readiness gate, fail closed, progress callback | Becomes Core hooks; parallel by level; failure blocks only the dependent branch; readiness comes from the health contract, not a reachability probe |
| Sleep policy table (`supervisor/sleep_wake/classification.py`) | Three policies, fail-closed `NEVER` default, 30 minute idle default | Profiles declared by modules, not a central table; floor list of never-sleep components; guards and leases added |
| Live state (`sleep_wake/state.py`) | In-memory live state | Same; transitions persisted for history |
| Linux wake (`socket_activation_linux.py`) | Socket-unit idea and unit text generation | Separate provider; proxyd shape rather than unverified socket passing; host keeps the process handle |
| Windows wake (`activation_proxy_windows.py`) | Wake-on-first-connection bidirectional relay, wake lock | Managed, loopback-only, OS-agnostic provider in .NET |
| Scheduled wake and Task Scheduler wake trigger (`v3-deepdive-39-task-scheduler.md`) | Schedule table; no catch-up flood | No cron in v1 (dependency rule); no user-scheduled-task UI here |
| Self-update re-exec (`self_update/reexec.py`) | Two-phase stage, smoke test, hand off on pass, owner confirmation, long soak | Mapped to Kodo slots and the sentinel (5.7); no code duplicated |
| Single-instance restart (`single_instance.py`) | Confirm-before-stop order | Reuses the module manager's line-move preflight and dry run (5.8) |
| Version pins and dynamic start (`version_pins.py`, `dynamic_start.py`, `available_versions.py`, `instance_registry.py`) | Pin semantics, on-demand multi-version instances, closed single-instance list | Pins stay doc 49's; dynamic start is a versioned wake; single-by-default |
| Rollback and arbitration (`rollback.py`, `arbitration.py`) | Idea of reverting the active release | Not taken as code: Kodo's slot pointer and doc 49 rings are the Bazaar mechanism |
| Fleet and instance registry (`fleet.py`, `instance_registry.py`) | Fleet view of instances and versions | Presented through `IWatchdogReader.FleetVersions` and the Monitor sub-view |
| Background Workers dispatch (`v3-deepdive-12-background-workers-api.md`) | Only the point that a sleeping dispatcher cannot poll its own rows | Wake schedules live in the always-awake SleepWake, not in the sleeping service; Bazaar's job poller (doc 03) is unchanged |

Resibo facts not carried over: gRPC transport and Python process model, the venv-per-service launch, `ActiveRelease` JSON files, Keymaster licence gating, and Resibo-specific service names (OCR, Preprocessing, Inference, Ingestion). Resibo is a separate codebase; this is a design adoption, not a port of its source.

## 7. Interaction with existing modules

| Existing item | Reused or extended | Not changed |
|---|---|---|
| `Bazaar.Core` (`IBazaarComponent`, `ComponentGraph`, `SoftDependsOn`) | Hard-edge order and soft edges reused; lifecycle, readiness, boot report and named instance lock added as kernel hooks (5.1) | Existing manifest and registration |
| `Bazaar.Kodo` (evaluator, probe runner, catalogue) | Reads both modules through the health contract; two proposed probe rows | State machine, score, caps, slot model, sentinel |
| `Bazaar.Server.Modules` (health aggregator, line-move, releases, pins) | Readiness adapter; restart-on-version; pin reads | Install gate, rings, rollback planner |
| `Bazaar.Updates` | `IReleaseLocator` implementer for dynamic start (when present) | Manifest and verifier |
| Doc 03 host | Instance lock generalised; host stays process owner; startup steps unchanged in order | Startup steps 1 to 7 and shutdown order |
| Doc 06 agent runtime | Leases act as sleep guards; `agent_lease` is not reused for kicks | Wake model, lease tables |
| Doc 16 VMs | Adapter exposes Saved VMs in the sleep status; idle timers stay doc 16's | VM lifecycle |
| Doc 42 deps | Out-of-process dependencies kick the watchdog and can sleep | Host owns the process handle; managed Postgres never sleeps |
| Doc 49 rings, pins | Pins read; sentinel confirmation and soak proposed | Policy engine |
| `Bazaar.Audit.Abstractions` | `IAuditLog` and `Principal` for all events | Hash chain |

Nothing in this doc duplicates Kodo's pull-based liveness (still the source for module health), the host's child supervision with crash restart (doc 42 section 6.2), doc 06's lease TTLs, or doc 16's idle save.

Overlaps the owner should know: (a) Watchdog push and Kodo pull liveness overlap by design; push is added only for hang detection of loops and out-of-process services; (b) doc 16 VM idle save and SleepWake sleeping are two timers for one thing unless the adapter reads doc 16's settings (5.3); (c) doc 49 pins have no channel scope.

## 8. Security and privacy

- **No restart authority.** The detector cannot act. Remediation goes through Kodo runbooks and the approval gates of doc 15. A compromised or noisy service cannot cause a restart by lying about being silent, because silence is the absence of kicks and acting needs an approval-tier runbook.
- **Kick authentication.** Out-of-process kicks carry a per-instance token from Identity (doc 14), minted at launch, never on a command line, scoped to one `(service, instance)`. A kick for another instance id is rejected. A forged kick can only make a service look alive; the Kodo pull probe is the independent check, and a token-holding attacker already runs as that service.
- **Heartbeat contents.** Service id, instance id, version string only. No hostnames, no secrets, no content.
- **Wake as an attack surface.** Waking costs resources. Wake requests carry a `Principal`; the activation relay binds loopback only; a request-triggered wake is rate-limited by `max_wakes_per_hour`; an anonymous wake is impossible because the relay only fronts loopback services the host itself calls. `ForceWake` is audited and needs `kodo.control.restart` style role grant (the role name is doc 48's; OPEN, Q8).
- **Taint.** A wake reason derived from untrusted content (for example a log line) must not call `ForceWake`; the standing rule of doc 15 applies to any caller that is an agent session.
- **Audit and privacy.** Events hold ids and versions. Agent ids appear only as `Principal` text in the audit actor column, as elsewhere in Bazaar. The status page (doc 46 section 12.4) never shows service ids from these modules.
- **Sleep and secrets.** A stopped service holds no decrypted secrets in memory; on wake, secrets are re-injected by the server (CLAUDE.md "secrets are injected by the server"). This is a small security gain, noted not relied on.

## 9. Testing (xUnit v3; no test waits on wall time)

- **Watchdog.** Table-driven detector tests with an injected clock: alive, silent after timeout, unseen never silent, starting grace, suspended never silent, per-service override, floor rejection, recovery event once. Concurrency tests on the registry (kicks from many threads while the detector runs). Instance-key test: a healthy new instance does not mask a hung old one. Architecture test: `Bazaar.Server.Watchdog*` references no `System.Diagnostics.Process` and no service-host abstraction, and exposes no method named Restart, Kill or Start. Contract test base `ModuleHealthContractTests<T>` (doc 46 section 10.3), plus a Watchdog-specific rule that every first-party module with a background loop registers a descriptor and kicks (a generated test over the component list, so Resibo's "nobody calls Kick" gap fails CI).
- **Core hooks.** Graph tests: parallel levels, ready gate blocks only dependents, soft failure does not block, required role failure refuses start, report content and observer calls, `IInstanceLock` second acquire fails, stable order on ties.
- **SleepWake.** State machine tests on a fake clock and fake `ISleepable`: drain then stop, guard veto, lease veto, wake coalescing (many concurrent callers, one start), wake timeout to Failed, retry cap, wake storm pin, dependency wake order, schedule due and no catch-up burst, fail-closed default, never-sleeps floor list rejects a policy change.
- **Kodo interaction.** The sleeping stub: pass while the activation path is intact, fail when it is not, `stale` self-test fact, probes never call `Touch` (assert zero activity from probe calls), contract-test item 7 not tripped.
- **Providers.** Relay: loopback integration with an ephemeral port, first connection wakes, second does not, bytes flow both ways, wake timeout closes the connection, bind refuses non-loopback. Systemd: unit text snapshot tests and `ProbeAsync` unavailable on a host without `systemctl` (no Linux host needed); real-systemd runs are a Linux-CI job, marked OPEN until a runner exists.
- **Fault tests (doc 46 section 17.1).** Add: a kicking service that hangs; a kicker killed at each registry write boundary (none, by design); a sleepable whose `StopAsync` hangs; a wake that never becomes ready.
- **Client.** If the Monitor sub-views are built: Avalonia.Headless tests for silent, suspended, waking and asleep rows, state words with colour (never colour alone), keyboard-only operation, screen reader labels (doc 46 section 14, doc 41).
- **Mutation.** Mutation test on the detector comparison (`>` versus `>=`, timeout resolution) and on the guard check, so a one-token change fails a test.

## 10. Work packages (small, independent, each with tests; dependency order)

1. **WP1 Core lifecycle hooks.** `IComponentLifecycle`, `IComponentReadiness`, `BootReport`, `IBootObserver`, `IInstanceLock` in `Bazaar.Core`, with graph tests. No dependency. Owner confirmation first (Q1).
2. **WP2 Watchdog.Abstractions.** Contracts in section 5.2 and the `Principal`-based event types. After WP1 only for the boot report types; otherwise none.
3. **WP3 Watchdog core.** Registry, detector, version tracker, in-process kicks, events, architecture test. After WP2.
4. **WP4 Watchdog persistence.** `watchdog` migration, event and boot-report writers, audit events. After WP3.
5. **WP5 Watchdog out-of-process kick endpoint.** Authenticated loopback endpoint and token minting through Identity. After WP3 and the Q3 answer.
6. **WP6 Watchdog health contract and contract-test wiring.** Including the "every loop registers a descriptor" generated test. After WP3.
7. **WP7 SleepWake.Abstractions.** Contracts in section 5.3. Independent of WP2 except `IWatchdogSuspension`.
8. **WP8 SleepWake core.** State machine, guards, policy, coalescing, wake storm limit, dependency wake, fakes. After WP1 and WP7.
9. **WP9 SleepWake schedules and persistence.** `sleep_wake` migration, schedule tick, transitions, audit. After WP8.
10. **WP10 Activation relay provider.** `Bazaar.Server.SleepWake.ActivationProxy`, with loopback tests. After WP7.
11. **WP11 Systemd provider.** `Bazaar.Server.SleepWake.Systemd`, unit text and probe. After WP7.
12. **WP12 Kodo bridge.** Sleeping stub, readiness adapter in `Bazaar.Server.Modules`, and the two proposed probe rows as a Kodo change. After WP3, WP8 and the owner's answer to Q2.
13. **WP13 Version and pin seams.** `IReleaseLocator`, `IVersionPinReader`, versioned instance keys. After WP8; implementers come from the module manager or Ouroboros work.
14. **WP14 VM and engine adapters.** `ISleepable` adapters for doc 16 and doc 20, reading existing idle settings. After WP8 and the VM module.
15. **WP15 Client sub-views.** `Bazaar.Client.Watchdog` and `Bazaar.Client.SleepWake`, headless tests. After the Monitor pane exists (doc 46 package 16).

WP2 to WP6 (Watchdog) and WP7 to WP11 (SleepWake) are two independent tracks after WP1.

## 11. Open questions

1. **Kernel change.** Owner: confirm the additive Core hooks of section 5.1 (lifecycle, readiness, boot report, named instance lock) belong in `Bazaar.Core`. Default if unanswered: hold WP1 and keep both modules usable with only the current component order.
2. **Kodo `Suspended` status.** Default: use the sleeping stub and add no status value. Option: add `Suspended` to the health contract (changes doc 46 section 10.1).
3. **Out-of-process kick transport.** HTTP on the loopback wire surface or a named pipe or Unix socket? Default: authenticated HTTP, as the rest of the wire surface.
4. **Sentinel update rule.** Tier 2 owner approval as a floor, and a longer soak for the sentinel (section 5.7). Default: propose both to doc 49 and doc 46.
5. **Cron schedules.** Default: not in v1; `daily_at`, `interval`, `once` only. A later module could add cron.
6. **Systemd without privilege.** Installing socket units normally needs root. Default: the provider reports unavailable on unprivileged installs, and the relay provider is used.
7. **Channel-scoped pins.** Doc 49's pin has no channel scope; Resibo's does. Owner: extend doc 49 or keep install-wide.
8. **Roles.** Which role names gate `ForceWake`, `SleepNow` and the Monitor sub-views (doc 48 owns role names).
9. **Which services sleep first.** Candidates are in section 5.3. Owner: confirm the first targets (default: local model runtimes and engine bridges; VMs only via doc 16).
10. **Linux runtime verification.** Real-systemd behaviour is untested in Resibo and here. Default: ship the relay provider as the tested path.

## 12. Conflicts and overlaps for the owner

1. **VM idle save versus SleepWake.** Doc 16 already owns idle save for VMs. SleepWake does not add a second timer, it adapts. Confirm doc 16's Environment service stays the owner.
2. **Doc 42 (host owns processes) versus systemd socket activation.** Resibo lets systemd launch services. Bazaar's host owns every child, so the systemd provider uses the proxyd shape and calls back into the host.
3. **Kodo pull versus Watchdog push.** Both measure liveness. Kept separate: Kodo stays decided; Watchdog adds hang detection and version reporting.
4. **Kodo health contract has no "suspended" state.** A sleeping module needs the stub (section 5.6) or a contract change.
5. **Doc 49 pins lack channel scope** (Resibo has it).
6. **Ouroboros is decided in CONVERGENCE but absent from `src`.** Version-pin and dynamic-start seams are abstractions only until it exists.
7. **Kodo's catalogue is signed and changes only by update.** The proposed probe rows are therefore a Kodo change, not something these modules can add at runtime.
8. **`Principal` is DECIDED (BAZAAR-COMPONENTS 4.2) but I found no `Principal` type in `src` yet.** Signatures here assume it lands in `Bazaar.Core` first.
