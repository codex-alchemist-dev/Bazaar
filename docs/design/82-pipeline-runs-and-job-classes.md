# 82. Pipeline runs and job classes (Bazaar.Server.PipelineRuns, Bazaar.Server.JobClasses)

Status: PLANNED. Design intent only; nothing here is built. The owner's direction of 2026-11-19 to adopt the Resibo execution, background-worker and task-scheduler features as Bazaar modules, to over-engineer, and to split conflicting parts into separate modules is DECIDED (section 2). Every design detail below that direction is PROPOSED unless marked otherwise. Owner questions: section 14.
Depends on: 03-server-host-and-providers.md (background jobs, settings registry, provider seam), 04-postgres-schema-and-migrations.md (conventions, NOTIFY, migrations), 06-agent-runtime.md (lease and step journal, which this doc does not replace), 09-work-life-simulation.md (agent availability as an idle input), 12-kanban-and-dependencies.md (Review column, card flags), 15-security-taint-and-approvals.md (audit log, approvals), 38-ryusei-fuda.md (triggers, tick job), 40-core-feature-modules-audit.md (component manifests, Jobs abstractions move), 46-kodo-health-module.md (module health contract), BAZAAR-COMPONENTS.md (component rules, migration namespaces, section 4.2 `Principal`).
Used by: 21 (PR review pipeline), 12 (Kanban Review automation), 18 (Vault reindex sweeps), 24 and 25 (company systems that register maintenance jobs), 33 (backup spot checks), 38 (tick job class), 46 (Kodo probes), 43 (telemetry jobs), the Company Build module.

## 1. Purpose

Bazaar already has a durable job queue: the `job` table with `FOR UPDATE SKIP LOCKED` claims, leases, bounded attempts, dedupe keys and a recurring-job planner (`src/Bazaar.Server/Jobs`). What it lacks, and what Resibo (a sister programme whose receipt pipeline faced the same problems) solved, is everything between "a row is due" and "a long multi-stage piece of work finished correctly":

- **PipelineRuns** is a checkpointed run scheduler. A run takes items through ordered stages. Every finished stage writes a checkpoint, so a retry or a crash resumes at the first unfinished stage instead of redoing expensive work (model calls, builds, test suites). Failures are bounded and escalate once. Cancel takes effect within one item. Triggers arriving close together coalesce into one run with a hard ceiling. A run has an open phase (intake) and a closing phase (finalize). Configuration reloads between runs. Stuck runs are visible.
- **JobClasses** classifies every background job by how it must execute (io, native, cpu-process, idle-time), keeps one consolidated registry of every job the installation can run, proves with a test that the registry and the code agree, stops a permanently failing job from retrying forever, and lets a person schedule only allowlisted actions, waking a sleeping module when one comes due.

Neither module is a receipt feature. Resibo's receipt stages, OCR, Matching and Geo logic, BIR checks and flag taxonomy stay in Resibo (CONVERGENCE.md, "Resibo only"). Only the generic mechanisms are adopted.

## 2. Decided inputs

- Owner, 2026-11-19: incorporate everything from Resibo that can benefit Bazaar; over-engineer; where a Resibo idea conflicts with Bazaar's design, make it a separate module rather than choosing; account for everything. DECIDED, and this is the "adopt as module X" decision for X in {`Bazaar.Server.PipelineRuns`, `Bazaar.Server.JobClasses`}.
- Owner, 2026-11-19: providers are plain modules on Core with no selector; actor attribution is `Principal` (BAZAAR-COMPONENTS 4.2); shared libraries and modules hold contracts and logic only with no third-party dependencies; providers are separate modules.
- Every feature is its own module `Bazaar.Server.<Name>` plus `Bazaar.Server.<Name>.Abstractions`, with a `Bazaar.Client.<Name>` if it has a client side. Nothing goes into `Bazaar.Server` or `Bazaar.Client.Core` (CLAUDE.md; architecture tests).
- Configurability rule (DECISIONS-LOG round 13): open values are operator settings with safe defaults; DECIDED items and security invariants are not settings; settings may tighten, never loosen below a floor.
- Do not create a second job table. The `job` table (`common/0006_job.sql`, frozen namespace) stays the only queue.
- GCs are the coordination system. Pipelines and jobs serve GCs and never replace them; a pipeline that speaks to people does it as a GC line (CLAUDE.md; doc 38).
- Resibo's own CONVERGENCE note of 2026-11-19 ("fold Resibo ideas into decided libs = NONE for now") concerns the extracted shared libraries, not Bazaar modules. It does not conflict with this doc, which adds Bazaar modules; see section 14, question 1.

## 3. What this doc does not decide

- Agent turn execution. Doc 06's `agent_lease`, `turn` and `turn_step` journal remain the crash-recovery mechanism for agent turns. PipelineRuns does not wrap or replace them (section 7.1).
- The agent wake triggers of doc 38 (`timer`, `interval`, `watcher`, `event`). They stay RyuseiFuda's. User schedules in this doc run allowlisted actions, not agents (section 7.4).
- The design of sleep and wake for idle modules (CONVERGENCE B6, "Consider"). This doc defines only the seam `IModuleWaker` and a fail-safe default; who implements it is OPEN.
- A general flag or review-queue module. Section 8 records the pattern and the contract PipelineRuns needs; whether it becomes its own module is OPEN (question 6).
- A receipt, OCR, vendor, geo or BIR feature of any kind.
- Remote or multi-node execution. One node is the default (doc 46 decided inputs); the multi-node variants are marked OPEN where they differ.

## 4. Reuse and what is genuinely new

### 4.1 Reused as is (state exactly)

| Existing | Where | Reused for |
|---|---|---|
| `job` table (`id`, `kind`, `dedupe_key`, `run_at`, `state`, `lease_owner`, `lease_until`, `attempts`, `payload`, `last_error`) | `src/Bazaar.Server/Data/Sql/common/0006_job.sql` | The only queue. A run executes as one job of kind `pipeline.run`. A job class never adds a table column. |
| `JobStore` claim, complete, fail and `FailExhaustedAsync` | `src/Bazaar.Server/Jobs/JobStore.cs` | All claiming, leasing and attempt counting. |
| `JobPoller`, `JobPollerOptions` (lease, batch, `MaxAttempts`, exponential retry delay) | `src/Bazaar.Server/Jobs` | The single poll loop. No second poller. |
| `IJobHandler`, `JobRecord` | same | A module's job is still an `IJobHandler`. JobClasses decorates handlers; it does not introduce a competing handler type. |
| `RecurringJob`, `RecurringJobPlan`, `RecurringJobScheduler` (bucket dedupe key; a missed poll costs one job, not a burst) | same | All timer-based cadence. The bucket-per-interval, no-burst behaviour already matches Resibo's "missed runs never catch up" (`core/task_scheduler/CLAUDE.md`). |
| Doc 38 tick job `ryusei.trigger_tick` | `src/Bazaar.RyuseiFuda` | Becomes the first registered job of class `Io` and the first entry in the consolidated registry. Unchanged otherwise. |
| `AgentLeaseRecoveryHandler` | `src/Bazaar.Server/Agents/Recovery` | Registered as an `Io` job. Unchanged. |
| `BaseBackupJob` handler | `src/Bazaar.Server.Components.Backup/Jobs` | Registered as a `Native` or `CpuProcess` job per its real behaviour. Unchanged. |
| `IRateLimiter`, `RateLimitPolicy` | `src/Bazaar.RateLimits.Abstractions` | Per-provider rate gates in fan-out stages and per-principal limits on schedule creation. |
| `IAuditLog.AppendAsync` | `src/Bazaar.Audit.Abstractions` | All audit events, actor written as a canonical `Principal` string. |
| `IModuleHealth` | doc 46 section 10 | Both modules implement it; Kodo probes read their tables. |
| Settings registry | doc 03, `src/Bazaar.Server/Settings` | All keys in section 10. |
| Kanban transitions and card flags | doc 12 | Default escalation target for pipelines that belong to a card. |

### 4.2 Genuinely new

Seams that must exist in the shared jobs layer (a generic, feature-free addition owned by the Jobs abstractions extraction of doc 40, not by either module):

1. `IJobAdmission`: asked after a claim, before the handler. Answers `Run`, `Defer(until)` or `Skip(reason)`. Needed for idle gating, concurrency limits and disabled kinds. Without it, a claimed job can only run or fail.
2. `JobStore.DeferAsync(id, owner, delay)`: returns a claimed job to `queued` with a later `run_at` and **does not consume the attempt**. Today every claim increments `attempts`, so deferral by failing would exhaust the cap on a healthy but busy system.
3. `JobStore.ExtendLeaseAsync(id, owner, lease)`: renews a lease. Today a handler that outlives `LeaseDuration` (default 5 minutes) is reclaimed by another poller and runs twice. Long pipeline runs cannot exist without this. See finding F1 in section 14.
4. `JobStore.SkipAsync(id, owner, reason)`: completes a job as `succeeded` with `last_error = 'skipped: <reason>'`. Done this way so the frozen `state` CHECK constraint (`queued|running|succeeded|failed`) needs no migration.
5. `IJobOutcomeObserver`: called after each handler result (success, failure, skip, defer) so JobClasses can count failures per kind. The poller owns no failure policy.

New modules (this doc): the run state machine, per-stage checkpoints, attempt counters with a latched escalation, the debounce coalescer, run-level cancel, the stage fan-out helper, idle-time sweeps, the class router, the consolidated registry and completeness test, the per-kind consecutive-failure guard, and allowlisted user schedules with a cron parser.

## 5. Module names and ids

| Module id | Projects | Kind |
|---|---|---|
| `pipeline-runs` | `Bazaar.Server.PipelineRuns`, `Bazaar.Server.PipelineRuns.Abstractions` | First-party feature module |
| `job-classes` | `Bazaar.Server.JobClasses`, `Bazaar.Server.JobClasses.Abstractions` | First-party feature module |
| `job-classes-worker-process` (provider) | `Bazaar.Server.JobClasses.WorkerProcess` | Provider module: runs `CpuProcess` jobs in a child process |
| `job-classes-os-wake-windows`, `job-classes-os-wake-systemd` (providers, OPEN) | `Bazaar.Server.JobClasses.OsWake.Windows`, `.Systemd` | Provider modules: wake a stopped host from the OS scheduler |
| `pipeline-runs-client` (PROPOSED) | `Bazaar.Client.PipelineRuns` | Run Monitor pane (client side), via the Rubedo pane registry |

Manifest edges: `pipeline-runs` hard-depends on `job-classes` (it registers `pipeline.run` as a class `Io` job, and it supplies the idle reader) and soft-depends on `kodo`, `kanban` and `agents`. `job-classes` hard-depends on the Jobs abstractions and soft-depends on `pipeline-runs` (the idle source) and `ryusei-schedule`. The soft cycle between the two is allowed (BAZAAR-COMPONENTS 4.1). Migration namespaces: `src/Bazaar.Server.PipelineRuns/Data/Sql/pipeline-runs/` and `src/Bazaar.Server.JobClasses/Data/Sql/job-classes/`. Neither writes the other's tables; they talk through the Abstractions projects. Abstractions hold contracts and records only, with no third-party dependency and no Npgsql.

## 6. PipelineRuns design

### 6.1 Concepts

- **Pipeline definition**: code-registered, id like `pr-review`, ordered stages, a `RulesVersion` integer, stage callables supplied by the consumer. PipelineRuns never contains a stage's logic (Resibo's boundary: the run API calls stages through their own contracts and does not reimplement them).
- **Scope**: the key that groups runs for concurrency and coalescing. In Resibo it was the user. In Bazaar it is a `scope_key` string such as `repo:codex-alchemist-dev/Bazaar`, `project:<id>` or `agent:<id>`.
- **Run**: an open window of items for one `(pipeline, scope)`.
- **Item**: one unit moving through stages (a PR head commit, a Vault document, a build). It has an optional `content_hash`.
- **Stage checkpoint**: the recorded result reference of one finished stage for one item. Large outputs live in blobs or the Vault; the checkpoint stores a reference, never the content (audit and privacy rule: ids, not content).

### 6.2 Run state machine (taken from Resibo `v3-deepdive-10` section 3, changed)

States are data, not `if` chains, so a test can enumerate and a reviewer can read them:

```csharp
public enum RunState { Waiting, Open, Closing, Paused, Completed, Cancelled }

public static class RunTransitions
{
    public static readonly IReadOnlyDictionary<RunState, RunState[]> Allowed =
        new Dictionary<RunState, RunState[]>
        {
            [RunState.Waiting]   = [RunState.Open, RunState.Cancelled],
            [RunState.Open]      = [RunState.Closing, RunState.Paused, RunState.Cancelled],
            [RunState.Closing]   = [RunState.Paused, RunState.Completed, RunState.Cancelled],
            [RunState.Paused]    = [RunState.Open, RunState.Closing, RunState.Cancelled],
            [RunState.Completed] = [],   // terminal
            [RunState.Cancelled] = [],   // terminal: a stopped run never resumes
        };
}
```

Changes from Resibo, with reasons:

- Resibo's deep-dive 10 keeps a `RUNNING` member that is documented as an unused legacy-compat alias. Bazaar has no old callers, so the member is simply absent. "Is it processing" is `state is Open or Closing`.
- Resibo's final state `SHUTTING_DOWN` doubles as "cancelled", and Resibo's `services/execution_core/CLAUDE.md` records that it has no distinct `COMPLETED` state and that this is worth resolving before run history is rendered. Bazaar adds `Completed` (all items terminal, finalizer ran) and renames the cancel terminal `Cancelled`. Bazaar owns this taxonomy, so there is no reason to leave the gap.
- Transitions return a result object (`Applied`, `Error`), not an exception, so a "pause a cancelled run" request is an ordinary answer for the client.

### 6.3 Open and closing phases

- **Open**: the run accepts new items. Items already present are processed (the run's job is running). Each accepted trigger extends the debounce window.
- **Closing**: intake is closed (a late trigger starts a new run; it never joins a closing one, so no item is added to a run whose finalizer already started). Remaining items drain, then the pipeline's `IRunFinalizer` runs (batch writes, the completion GC line, notifications), then `Completed`.
- `closing_started_at` is stamped on entry to Closing, never reconstructed from logs.

### 6.4 Debounce with max wait (taken from Resibo `v3-deepdive-10` section 5.2)

Each trigger pushes `window_expires_at` out by the window, never past `ceiling_at`. The ceiling is measured from `opened_at`, never from the latest trigger, and is a real boundary: a trigger at or after it closes that run and opens a new one. A sweep (`DueForClosing`) closes a run when either the window elapsed (quiet case) or the ceiling was reached (trickle case). The coalescer is synchronous arithmetic over timestamps and takes an injected `TimeProvider`, so the ceiling test is exact and instant. Defaults: window PT10S, ceiling PT2M (Resibo's 10 s and 120 s; section 10 makes both settings).

Coalescing state is persisted (`pipeline_run`), not held in memory as Resibo does, so a restart does not forget an open window. Resibo flags its own in-memory registry as a known gap.

### 6.5 Concurrency

Two independent limits, both must have capacity for a run to start: per-scope (default 1, doubles as rate limiting) and global (protects shared resources such as the build VMs and model quota). Acquisition order is always global first, then per-scope; release is in `finally`; a cancelled or failing run never strands a slot. Implemented as an `IJobAdmission` that defers the claimed `pipeline.run` job when no slot is free (`DeferAsync`, no attempt consumed). Single-node in-process gates are the default. A database-counted variant for several nodes is OPEN (question 7).

### 6.6 Per-stage checkpoints and resume (Resibo `v3-deepdive-10` section 6)

```csharp
public interface ICheckpointStore
{
    ValueTask<StageCheckpoint?> GetAsync(Guid itemId, string stage, CancellationToken ct);
    ValueTask WriteAsync(StageCheckpoint cp, CancellationToken ct);              // throws CheckpointWriteFailed
    ValueTask<StageCheckpoint?> FindFinishedByContentHashAsync(string pipelineId, string contentHash, CancellationToken ct);
}

public sealed record StageCheckpoint(
    Guid ItemId, Guid RunId, string Stage, DateTimeOffset CompletedAt,
    string OutputRef, string? ContentHash, int RulesVersion);

public delegate ValueTask<string> StageFunc(StageContext ctx, CancellationToken ct);   // returns an output reference
```

`RunStage` returns `(OutputRef, Resumed)`. If a checkpoint exists it returns it with `Resumed = true` and does no work. Two asymmetries are the point and are tested by name:

- A **checkpoint write failure fails the attempt**. An unrecorded success is indistinguishable from a failure on the next pass, so counting it as success would silently redo the expensive stage.
- A **narrative (GC line or audit note) failure is swallowed**. A missing log line must not fail a paid-for stage. A **resumed stage does not re-emit** its narrative line, because it did not happen again.

`CompletedStages` asks the store per stage and does not assume a prefix: a checkpoint set with a hole is a real state, and resuming at the first miss would otherwise skip everything after it.

Run-level idempotency uses `content_hash`, never time. An item whose terminal-stage checkpoint already exists for the same hash in the same pipeline is skipped and counted as a duplicate. An empty hash never matches. Bazaar example: the same PR head SHA reviewed twice runs once.

### 6.7 Bounded retry with a latched single escalation

```csharp
public interface IAttemptCounter
{
    ValueTask<int> GetAsync(Guid itemId, string stage, CancellationToken ct);
    ValueTask<int> IncrementOnFailureAsync(Guid itemId, string stage, CancellationToken ct);
    ValueTask<bool> TryLatchEscalationAsync(Guid itemId, string stage, CancellationToken ct); // true only for the first caller
}

public enum StageOutcome { Completed, Resumed, Failed, Escalated }
public sealed record StageAttempt(string Stage, StageOutcome Outcome, int Attempts, string? OutputRef, string? Error);
```

Rules, each a named test:

1. Cap check first. An item at its cap does no work (the expensive stages are the ones not to repeat).
2. **Escalation is latched**: `TryLatchEscalationAsync` is an atomic `UPDATE ... WHERE escalated_at IS NULL`. One escalation per item and stage, however many sweeps see it. This fixes Resibo's own sketch, which created a new flag on every sweep and so reintroduced its motivating bug (57 identical warnings).
3. The outcome is data (`StageAttempt`), not a null return, because a stage may legitimately return nothing.
4. The counter increments on **failure**, never on entry, so cancelled or resumed passes do not count.
5. Escalation calls `IEscalationSink` (section 8). The latch is taken before the sink call and released only if the sink throws, so a failing sink is retried on the next sweep and never double-fires.

### 6.8 Fine-grained cancel

Cancel is checked at the top of every item and at every stage boundary, and a cooperative `CancellationToken` is handed to the stage. It is read from the run row (`state = 'cancelled'`) through a `ICancelSignal` that is kept hot by Postgres `NOTIFY pipeline_run_cancel` and re-read from the table at each item boundary, so a missed notification cannot delay a stop by more than one item. Resibo's origin bug: a 156-item run kept going for minutes after a stop request.

The check interval is **not a setting and there is no field for it**. A test asserts that no registered setting key or options property for it exists. `Cancelled` has no outgoing edge, so a stopped run cannot be reopened and resume a batch the person stopped.

### 6.9 Config hot reload each cycle

```csharp
public sealed class PipelineConfigReloader
{
    public PipelineConfig Active { get; }
    public int ReloadCount { get; }
    public void Stage(PipelineConfig next);          // lands immediately, affects nothing running
    public PipelineConfig ApplyForNextRun();         // called between runs; ticks ReloadCount only if something changed
}
```

A change is staged at once and becomes active at the next run boundary, so a run sees one consistent configuration for its whole life. `ReloadCount` ticks only on a real change (a counter that ticks every run answers a question nobody asked). It is exposed through the status API so a support session can confirm "this run used the change made five minutes ago". CONVERGENCE A8 asks the settings registry for reload-on-change events; this reloader is a consumer of those events when they exist and polls the registry snapshot each cycle until then.

### 6.10 Watchdog kicks

A kick is a heartbeat from inside the work, at the start of each cycle, before and after each stage, and throughout any idle wait. A process that only kicks between runs looks healthy while wedged inside one. Bazaar has no watchdog service yet (CONVERGENCE B5 proposes one as a Kodo feature), so the kick is defined against what exists:

- A kick (a) writes `pipeline_run.last_kick_at` (throttled to `pipeline.kick_interval`), and (b) calls `JobStore.ExtendLeaseAsync` for the run's job. One call serves both the stuck-run signal and the lease heartbeat.
- A kick that cannot be delivered never fails the run (a degraded monitor must not become an outage). Failures are counted in `failed_kicks`.
- A Kodo probe (section 11) reads `last_kick_at` and fails an Open or Closing run whose last kick is older than `pipeline.stuck_after`. When Kodo gains a heartbeat API, an adapter forwards the same kick to it (OPEN, question 8).
- `kick_count` is testable against the number of items processed, which proves kicks happen inside the loop and not only around it.

### 6.11 Items fan-out helper (provider-set pattern, from Resibo Matching and Geo)

Resibo runs several independent providers (two geocoders, several OCR engines) in parallel and lets the consuming API corroborate. CONVERGENCE A1 asks Bazaar's provider model for exactly this "provider set". Under the owner's rule that providers are plain modules on Core with no selector, a stage that wants a set resolves all registered providers of a role from DI and uses `ProviderSetRunner`:

```csharp
public interface IProviderSetRunner
{
    ValueTask<IReadOnlyList<ProviderResult<TOut>>> RunAsync<TProvider, TIn, TOut>(
        IReadOnlyList<TProvider> providers, IReadOnlyList<TIn> candidates,
        Func<TProvider, TIn, CancellationToken, ValueTask<TOut>> call,
        ProviderSetOptions options, CancellationToken ct) where TProvider : IProviderSetMember;
}
public sealed record ProviderResult<TOut>(string ProviderId, int CandidateIndex, bool Ok, TOut? Value, string? Error, TimeSpan Took);
```

Properties taken from Resibo `v3-deepdive-16` section 3 and 5, and `v3-deepdive-15` section 5:

- **Two axes, both concurrent**: providers and candidates (several plausible inputs to each provider). One axis alone misses the case where every provider confidently agrees on a wrong input. Never a sequential loop; total time is about the slowest single provider.
- **Per-provider rate limit** through `IRateLimiter` (a policy per provider id), not by slowing a loop.
- **A missing or failed provider degrades**: it yields a failed `ProviderResult`, never a failed run, matching Resibo's "a missing engine is unavailable, not a failed run".
- **The runner never picks a winner.** It returns every result. Corroboration belongs to the consuming module, supplied as an `ICorroborationPolicy`. A policy enum is taken from Resibo's vendor-corroboration setting: `Always` (default; the second opinion always runs), `BelowThreshold` (cheaper, can miss a confidently wrong answer), `Never`. The consumer registers its own setting; this doc defines only the enum and the runner.
- Optional response cache keyed by a normalized query, using the existing cache provider, to stretch free tiers. Off by default.

Bazaar uses: several model reviewers on one PR (independent first, in line with the Council's independent-first rule of CLAUDE.md), several search or embedding providers, and the gateway's own upstream probing. The runner is generic; none of those consumers is decided here.

### 6.12 Idle-time retroactive sweep (Resibo Reconciliation and Background Workers section 4)

The pattern: **anything the live pipeline can do, a sweep can do to old data, with one function and two orchestrators** (Resibo PRINCIPLES 1.9). Concretely:

- Each stage callable is registered once. The live run calls it for new items. A **sweep run** (`run_kind = 'sweep'`) calls the same callable for historical items.
- Every checkpoint carries `rules_version`. When a pipeline's `RulesVersion` increases (a rule, prompt or model changed), a sweep selects items whose terminal checkpoint has an older version and queues a sweep run. Whether a change is "worth a sweep" is the owning module's call, never assumed (Resibo: the substrate does not decide).
- A sweep run is registered as an **`IdleTime`** job (section 7). It starts only when its scope is idle and yields as soon as a foreground run for the scope appears (a foreground trigger pauses the sweep run: `Open -> Paused`, and it resumes later from its checkpoints).
- Bazaar candidates: re-running PR review checks after the review rules change, rebuilding Vault index entries after an extractor change, recomputing Kanban dependency state, re-scoring Constellation edges. None of these is designed here.

## 7. JobClasses design

### 7.1 Execution classes (Resibo Background Workers section 3, changed)

| Class | Runs on | Typical Bazaar job |
|---|---|---|
| `Io` | The poller's async context | Postgres reads and writes, HTTP, `ryusei.trigger_tick`, `AgentLeaseRecoveryHandler` |
| `Native` | A bounded thread pool owned by JobClasses | Native or blocking library calls (git object work, compression, hashing) |
| `CpuProcess` | A child process through `IWorkerProcessRunner` | CPU-heavy work that would starve the host (large reindex, bulk propagation, spot verification of backups) |
| `IdleTime` | As above, but admitted only when its scope is idle | Retroactive sweeps, curation, cleanup |

The class is **declared by the registering module, never inferred**: the router cannot know whether a domain job blocks. A blocking job on the `Io` path stalls every job behind it. A CPU job in a child process pays serialisation cost that an `Io` job should not pay. The cost of a wrong declaration is real in both directions, so the registration test (section 7.3) checks that a class is declared.

Change from Resibo: Resibo has three classes plus an `idle_only` flag. Bazaar makes `IdleTime` a fourth class (it matches how operators think about it) and requires an `IdleTime` registration to also declare its `Substrate` (`Io`, `Native` or `CpuProcess`) for how it runs once admitted. `JobKindRegistration.Class` is therefore a four-valued field and `Substrate` is mandatory for `IdleTime` only.

The class router is an `IJobHandler` decorator applied by JobClasses to every registered handler (resolved from DI; modules register their handler as today). `Native` jobs go to the thread pool and the poll loop does not wait inline; `CpuProcess` jobs go to the provider. The poller currently runs claimed jobs one after another inside one poll while each lease clock is already running (finding F1), so concurrent class-based dispatch with lease extension is part of the work, not an optimization. The inline fallback (run inline if no pool or process provider is wired) is **kept for `Native` only** (PROPOSED), and `CpuProcess` without a provider fails closed to "not run, health Warn", because silently running a CPU job in the host defeats the isolation its class promises. The `CpuProcess` rule is Bazaar's own, PROPOSED (Bazaar's design, not from Resibo), because Bazaar's host also serves interactive clients.

### 7.2 Idle detection

```csharp
public interface IIdleSource
{
    string Id { get; }
    ValueTask<IdleVerdict> IsIdleAsync(JobScope scope, CancellationToken ct);   // throws or returns Unknown when it cannot answer
}
public enum IdleState { Idle, Busy, Unknown }
public sealed record IdleVerdict(IdleState State, string Reason);      // Reason is always set, idle or not
public readonly record struct JobScope(string Key);                      // "global" or a project, repo or agent key
```

- A job is admitted only if **every** registered idle source answers `Idle` for the job's scope (a provider-set AND). Sources shipped: PipelineRuns (no Open or Closing run for the scope), Agents (no active `agent_lease` for the agent scope; an off-shift agent counts as idle, from doc 09 availability), and a host-load source (Kodo's memory and CPU readings, optional).
- **Unavailable means not idle.** A source that cannot answer, or no source at all, gives `Unknown`, which is treated as `Busy`. An idle-only job exists to yield, so running it while unable to confirm quiet defeats the class; skipping costs one interval, running a heavy sweep in a live batch costs someone's actual work. This is a security-style invariant, not a setting.
- **Per-scope versus global is derived, not configured.** A job whose target is per-project or per-agent checks that scope's idle state; a genuinely global job checks nobody's. One scope's activity must not block another's maintenance.
- The `Reason` is always populated ("2 open runs for repo:X") so an operator reads why a sweep did not start without opening the source. A skip is `Defer` (no attempt consumed, no failure counted).

### 7.3 Consolidated job registry and completeness test

```csharp
public sealed record JobKindRegistration(
    string Kind, string OwnerModule, JobClass Class, JobSubstrate? Substrate,
    TimeSpan? Interval, string Scope /* "global" or a scope template */, bool Schedulable, string Description);

public interface IJobKindRegistry
{
    IReadOnlyList<JobKindRegistration> All { get; }
    JobKindRegistration Require(string kind);                 // throws UnknownJobKind
    IReadOnlyList<string> UnregisteredKnown();                 // in the doc table, not yet registered by a module
    IReadOnlyList<string> UnknownRegistered();                 // registered but absent from the doc table
}
```

- Registration is at start from each module's manifest-declared `AddJobKinds`. A duplicate kind **throws**; "last wins" would make which job runs depend on load order, a bad property for work nobody watches. `OwnerModule` is for observability, not enforcement (the registry does not police who may register what).
- `Interval = null` means event-triggered (the registry still lists it so the workload is visible).
- The registry is persisted to `job_kind` at start so Kodo and the status page can show "what does this installation do while nobody is watching", and so a kind present in the queue but missing from the registry is visible.
- **Registry-completeness test**: Resibo mirrors a Markdown table into code because a table that exists only in prose cannot be checked, and its own history shows three jobs fully designed and never added. Bazaar does the same with a checked-in table (section 7.6, the "Known jobs" table) and a test in `tests/Bazaar.Architecture.Tests` that parses the table and asserts both directions: a job in the table but not registered fails, and a registered job not in the table fails. A seeded-violation test proves each direction fires (the pattern doc BAZAAR-COMPONENTS section 6 uses).
- The "Known jobs" table below is updated in the same PR that adds a job. This doc seeds it with the jobs that exist today.

### 7.4 Retry cap for permanently failing jobs

The existing cap is per job row: `MaxAttempts` (5) fails one row. It does not stop a **recurring** kind, because each interval bucket is a new row with a new dedupe key and a fresh attempt budget, so a permanently failing recurring kind retries forever (Resibo's V2 failure mode). JobClasses adds a per-kind guard through `IJobOutcomeObserver`:

- `job_kind_health.consecutive_failures` counts consecutive **failed** outcomes of a kind, across rows. A **skip is not a failure** (a sweep yielding every hour for a week has not failed once; counting skips would disable the healthiest jobs on the busiest systems).
- At `jobs.max_consecutive_failures` (default 5) the kind **auto-disables**. The tripping outcome carries `TrippedFailureGuard = true`, and exactly one escalation is raised through the latch (one audit event, one Kodo state change, one GC line to the owner, not five).
- `Admission` returns `Skip("disabled")` for a disabled kind (and `RecurringJobScheduler` is not asked to enqueue it again once the kind is known disabled, avoiding a growing pile of skipped rows).
- **Re-enabling is an explicit action by a named `Principal`** with the right role (`user:...`), never automatic and never on a timer. The counter resets only on re-enable. A `force` run does not clear it; otherwise force becomes a way to keep a broken job limping without anyone looking at why.
- Change from Resibo: Resibo's deep-dive 12 fixes the cap at five (`MAX_CONSECUTIVE_FAILURES`, `core/background_workers/CLAUDE.md`) and documents no setting for it. Bazaar's configurability rule says open values are operator settings. Resolution: the cap is a setting with range 1 to 20 and default 5, and the **guard itself** (that a cap exists and re-enable is explicit) is not a setting. Owner may prefer a fixed value; see question 3.

### 7.5 Run history

`job_run` records one row per executed or failed dispatch (outcome, class, duration, error text capped at 2000 characters like `JobStore`). Skips are aggregated on `job_kind_health` (`skipped_not_idle_count`, `last_skip_reason`), not one row each, to avoid flooding on a busy system. Retention is `jobs.run_history_retention`.

A "next due" estimate (`INextDueEstimator`) exposes seconds until each timer kind fires and whether idle gating currently blocks it. It is labelled approximate (the real fire time also depends on idle state and on the job finding work). Resibo kept this because it is good transparency.

### 7.6 Known jobs (seed table for the completeness test)

| Kind | Owner module | Class | Interval | Scope |
|---|---|---|---|---|
| `ryusei.trigger_tick` | ryusei-schedule | Io | per doc 38 | global |
| `agents.lease_recovery` | agents | Io | per doc 06 | global |
| `backup.base_backup` | backup | Native | per doc 33 | global |
| `pipeline.run` | pipeline-runs | Io | event | `scope_key` |
| `pipeline.sweep` | pipeline-runs | IdleTime (Substrate Io) | event | per pipeline scope |
| `pipeline.checkpoint_purge` | pipeline-runs | Io | P1D | global |
| `jobs.run_history_purge` | job-classes | Io | P1D | global |
| `jobs.schedule_tick` | job-classes | Io | PT1M | global |

Other modules add their rows when they register jobs (log retention, session cleanup, update checks, Vault reindex and so on). The exact kinds and intervals for `ryusei.trigger_tick`, `agents.lease_recovery` and `backup.base_backup` are those in docs 38, 06 and 33 and the code today; this table records class and ownership only. Kind strings for the first two must be checked against the code during WP-J4 (OPEN).

### 7.7 Allowlisted user-schedulable actions

Taken from Resibo (`core/task_scheduler/CLAUDE.md`, deep-dive 39). A person (or an agent acting for a person; see Principal below) may schedule a recurring run of a **registered action** and nothing else.

```csharp
public sealed record SchedulableAction(
    string ActionId, string JobKind, string Description,
    JsonSchemaRef ParamsSchema, string[] RequiredRoles, bool WakesModule, string? TargetModuleId);

public interface ISchedulableActionRegistry
{
    IReadOnlyList<SchedulableAction> All { get; }            // starts empty
    SchedulableAction? Find(string actionId);
}

public sealed record UserSchedule(
    Guid Id, Principal Owner, string ActionId, string ParamsJson, string CronExpression,
    string TimeZone, bool Enabled, Principal CreatedBy, DateTimeOffset LastEvaluatedAt);
```

Rules (each a named test):

- **The allowlist starts empty, and that is the safe state.** With no registered action, every creation is rejected. An absent allowlist that permits would invert the rule exactly while nothing constrains it. A schedulable action is a registered `JobKindRegistration` with `Schedulable = true`, so the allowlist and the registry cannot drift.
- **Rejection happens at creation, never at dispatch.** An unregistered action, malformed parameters (checked against the action's schema), a malformed cron expression, a role that lacks the action's `RequiredRoles`, and the per-principal cap are all rejected in the form. A task failing at 2 a.m. on a Sunday is a support ticket; a rejection in the form is a form error.
- **Scheduling arbitrary code is impossible**: an action maps to a registered job kind, and parameters are data validated by a schema; there is no free command field. An agent or schedule cannot create an action.
- **Cron parser**: written in this module, pure .NET, no third-party package (the shared-lib rule; Resibo also wrote its own, for the same reason). Standard five fields plus a time zone. Parsing is a total function over a string and lives apart from the firing logic so it is testable without time.
- **Missed runs never catch up** (Resibo resolution). `Evaluate(cron, lastEvaluatedAt, now, grace)` only asks about the one most recent occurrence. Fire if `now - occurrence <= grace`; otherwise record `missed` and skip to the next future occurrence. Windows are non-overlapping (the next call searches strictly after the occurrence just evaluated), so nothing is double-counted or skipped except a genuinely late check. The alternative releases a burst of catch-up jobs just as the system is recovering. This already agrees with doc 38's "missed runs coalesce into one fire".
- `grace` defaults to PT1M and is clamped to at most PT5M: widening it away would reintroduce the burst.
- Firing enqueues one `job` row of the action's kind with dedupe key `sched:{scheduleId}:{occurrenceUtc}`, so a crash-retried tick, two hosts and a manual re-evaluate all collapse to one run (same reasoning as doc 38's `UNIQUE (trigger_id, scheduled_for)`).

**Waking sleeping modules.** Resibo found that a dispatcher that may be asleep cannot poll its own schedule, and fixed it with a registry of wake mechanisms (in-app timer, OS-native, supervisor wake). Bazaar's equivalent:

```csharp
public interface IModuleWaker
{
    bool IsAvailable { get; }
    ValueTask ScheduleWakeAsync(string moduleId, string scheduleId, DateTimeOffset at, CancellationToken ct);
    ValueTask CancelWakeAsync(string scheduleId, CancellationToken ct);   // idempotent
}
```

- The always-awake host's `jobs.schedule_tick` job (PT1M) is the **in-process timer** and covers every module the host keeps running. It is the default and needs nothing else.
- A module that **can sleep** (a heavy module or an agent runtime stopped to save memory; CONVERGENCE B6 is "consider") declares `WakesModule = true` on its action. The action's due time is then also armed with the registered `IModuleWaker`.
- **No `IModuleWaker` registered means `IsAvailable = false`, and creating a schedule for a sleep-capable module is rejected with an explicit reason**, rather than accepted and silently never firing. Fail-safe until wired; there is no permissive default to forget to remove.
- OS-level wake of a stopped host is a **provider**: `job-classes-os-wake-windows` (Task Scheduler) and `job-classes-os-wake-systemd` (timer units). They are named only. Whether Bazaar wants a stopped-host wake at all is OPEN (question 4), and the sleep and wake design for modules is not in any Bazaar doc yet (section 3).

**Principal.** A schedule is owned by a `Principal`. `kind = user` is the normal case. `agent` may create schedules only when the action's `RequiredRoles` allow agents and the schedule is for work that agent owns; the allowlist and role gate are the same for everyone, and each schedule records `Owner` and `CreatedBy` separately so an agent acting for a person is visible. `service` and `system` principals are used by seeded schedules (for example "weekly backup spot check") that ship disabled until an operator enables them.

### 7.8 Relationship to RyuseiFuda (conflict, handled by separation)

| | RyuseiFuda (doc 38) | `job-classes` user schedules |
|---|---|---|
| Fires | Wakes an agent with a purpose | Enqueues a registered job kind |
| Spec | `timer`, `interval`, `watcher`, `event`; closed set; cron deferred | Cron plus time zone; allowlisted action |
| Authority | None: the agent acts under its own permissions | Whoever owns the schedule, through the action's role gate |
| Surface | Micro schedule layer of the three-layer board | Settings and a schedule pane |

The two overlap on "do something later". They do not share code and neither depends on the other. RyuseiFuda keeps its closed set and defers cron (its stated reason: timezone handling and injection risk). This module takes cron because its target is a job kind validated by schema, not free text for an agent. A future agent-facing trigger that fires a schedulable action is possible by an `event` trigger on `job.schedule.fired`; not decided. Doc 38's own note of CONVERGENCE B13 ("already similar; note only") is consistent with this split.

## 8. Escalation and the review-queue pattern (patterns for PR review and the Kanban Review column)

PipelineRuns needs one outward contract and no queue of its own:

```csharp
public interface IEscalationSink
{
    ValueTask EscalateAsync(Escalation e, CancellationToken ct);   // idempotent on e.DedupeKey
}
public sealed record Escalation(
    string DedupeKey, string PipelineId, Guid RunId, Guid ItemId, string Stage,
    EscalationSeverity Severity, string Reason, IReadOnlyDictionary<string, string> Facts);
public enum EscalationSeverity { Routine, High }
```

Resibo's Review/Flagging (`v3-deepdive-25`) contributes a lifecycle and a severity policy that Bazaar's Review column and PR review can use. They are recorded here as a pattern, not as a built receipt feature:

- **Lifecycle**: `open -> assigned -> resolved | dismissed`. **Dismissed (false positive) is distinct from resolved (a real fix)**, and the two are never counted together in metrics or queue views. A test asserts it.
- **Routing**: a shared open queue; anyone permitted may self-assign; the owner may reassign. This is the same shape as the PR review request in doc 21 and the Kanban Review column in doc 12 (reviewer requested; `Review -> In progress` with reason; closed-unmerged PR flagged).
- **Severity tiers**: `High` (security-relevant or compliance-relevant) raises an immediate notification and a GC line to the owner; `Routine` appears in the queue only, because an alert for every routine item trains people to ignore the channel. Which items are `High` is each producer's declaration, not decided here.
- **Taxonomy lives elsewhere**: the producer of a flag registers its types; the queue never invents types. For Bazaar, the type set is owned by the module that raises it.
- **Resolution writes through the normal path**: any data change from resolving an item goes through the owner module's usual write path (for a card, `kanban.transition`), never a bypass, so it is audited like any other change.
- **Audit view**: a read-only workflow view that pulls the relevant slice of existing logs (turn steps, checkpoints, GC lines) for one item. It is a query and presentation layer, not a second log store.

Default sinks for PipelineRuns (all in the owning module, not here): a PR-review pipeline escalates by flagging the PR's Kanban card (`kanban.pr_closed_unmerged`-style flag in doc 12) plus one GC line; a build pipeline escalates to a GC line only. If the owner wants a generic flag queue with this lifecycle for several consumers, it becomes a third module `Bazaar.Server.ReviewQueue` that implements `IEscalationSink`; this doc does not design it (question 6).

## 9. Data (SQL sketches)

Per-component migration namespaces. `job` is not touched. Sketches only.

```sql
-- src/Bazaar.Server.PipelineRuns/Data/Sql/pipeline-runs/0001_pipeline_run.sql
CREATE TABLE pipeline_run (
    id                 uuid PRIMARY KEY,
    pipeline_id        text        NOT NULL,
    scope_key          text        NOT NULL,
    run_kind           text        NOT NULL DEFAULT 'live' CHECK (run_kind IN ('live','sweep')),
    state              text        NOT NULL CHECK (state IN ('waiting','open','closing','paused','completed','cancelled')),
    opened_at          timestamptz NOT NULL,
    window_expires_at  timestamptz,
    ceiling_at         timestamptz,
    closing_started_at timestamptz,
    finished_at        timestamptz,
    rules_version      integer     NOT NULL,
    config_reload_seq  integer     NOT NULL,          -- ReloadCount the run started with
    item_count         integer     NOT NULL DEFAULT 0,
    last_kick_at       timestamptz,
    cancelled_by       text,                          -- canonical Principal text
    cancel_reason      text,
    job_id             bigint                         -- the pipeline.run job; read-only link, no FK to a frozen table
);
-- At most one live open run per (pipeline, scope): coalescing target.
CREATE UNIQUE INDEX pipeline_run_one_open_idx ON pipeline_run (pipeline_id, scope_key)
    WHERE run_kind = 'live' AND state IN ('open','waiting','paused');
CREATE INDEX pipeline_run_due_idx ON pipeline_run (window_expires_at) WHERE state = 'open';

CREATE TABLE pipeline_run_item (
    id           uuid PRIMARY KEY,
    run_id       uuid NOT NULL REFERENCES pipeline_run(id),
    subject_ref  text NOT NULL,                       -- e.g. pr:codex-alchemist-dev/Bazaar#123@<sha>
    content_hash text,
    status       text NOT NULL CHECK (status IN ('pending','processing','done','failed','escalated','skipped_duplicate','cancelled')),
    reached_stage text,
    added_at     timestamptz NOT NULL DEFAULT now(),
    UNIQUE (run_id, subject_ref)
);

CREATE TABLE pipeline_checkpoint (                    -- 0002
    item_id       uuid NOT NULL REFERENCES pipeline_run_item(id),
    stage         text NOT NULL,
    run_id        uuid NOT NULL,
    completed_at  timestamptz NOT NULL,
    output_ref    text NOT NULL,
    content_hash  text,
    rules_version integer NOT NULL,
    PRIMARY KEY (item_id, stage)
);
CREATE INDEX pipeline_checkpoint_hash_idx ON pipeline_checkpoint (content_hash) WHERE content_hash IS NOT NULL AND content_hash <> '';

CREATE TABLE pipeline_stage_attempt (                 -- 0003
    item_id       uuid NOT NULL REFERENCES pipeline_run_item(id),
    stage         text NOT NULL,
    failures      integer NOT NULL DEFAULT 0 CHECK (failures >= 0),
    last_error    text,
    escalated_at  timestamptz,                        -- the latch; set once by UPDATE ... WHERE escalated_at IS NULL
    PRIMARY KEY (item_id, stage)
);
```

```sql
-- src/Bazaar.Server.JobClasses/Data/Sql/job-classes/0001_job_kind.sql
CREATE TABLE job_kind (
    kind          text PRIMARY KEY,
    owner_module  text NOT NULL,
    class         text NOT NULL CHECK (class IN ('io','native','cpu_process','idle_time')),
    substrate     text CHECK (substrate IN ('io','native','cpu_process')),
    interval_secs integer,
    scope         text NOT NULL,
    schedulable   boolean NOT NULL DEFAULT false,
    description   text NOT NULL DEFAULT '',
    registered_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT idle_needs_substrate CHECK ((class = 'idle_time') = (substrate IS NOT NULL))
);

CREATE TABLE job_kind_health (                        -- 0002
    kind                   text PRIMARY KEY REFERENCES job_kind(kind),
    consecutive_failures   integer NOT NULL DEFAULT 0,
    total_runs             bigint  NOT NULL DEFAULT 0,
    total_failures         bigint  NOT NULL DEFAULT 0,
    skipped_not_idle_count bigint  NOT NULL DEFAULT 0,
    last_skip_reason       text,
    last_run_at            timestamptz,
    last_outcome           text,
    last_error             text,
    disabled_at            timestamptz,
    disabled_reason        text,
    escalated_at           timestamptz,               -- latch: one escalation per trip
    re_enabled_by          text,                      -- canonical Principal text
    re_enabled_at          timestamptz
);

CREATE TABLE job_run (                                -- 0003
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    job_id      bigint NOT NULL,
    kind        text   NOT NULL,
    class       text   NOT NULL,
    outcome     text   NOT NULL CHECK (outcome IN ('succeeded','failed','deferred')),
    started_at  timestamptz NOT NULL,
    finished_at timestamptz NOT NULL,
    error       text
);
CREATE INDEX job_run_kind_time_idx ON job_run (kind, started_at DESC);

CREATE TABLE user_schedule (                          -- 0004
    id                uuid PRIMARY KEY,
    owner             text NOT NULL,                  -- canonical Principal text
    created_by        text NOT NULL,
    action_id         text NOT NULL,
    params            jsonb NOT NULL DEFAULT '{}'::jsonb,
    cron              text NOT NULL,
    time_zone         text NOT NULL,
    enabled           boolean NOT NULL DEFAULT true,
    last_evaluated_at timestamptz NOT NULL,
    next_check_after  timestamptz NOT NULL,
    created_at        timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX user_schedule_due_idx ON user_schedule (next_check_after) WHERE enabled;
```

Because `job` is in the frozen `common/` namespace, JobClasses stores class and health in side tables keyed by `kind` and reads the queue through the Jobs abstractions. A foreign key to `job` is not declared (the frozen file is not edited; a cross-component FK is allowed only to a table a dependency declares in its contract). `pipeline_run.job_id` and `job_run.job_id` are plain columns.

## 10. Settings registry rows (all install scope, role: operator; audit on change)

| Key | Type | Default | Range or floor | Approval |
|---|---|---|---|---|
| `pipeline.debounce.window` | ISO duration | PT10S | PT1S to PT10M | No |
| `pipeline.debounce.max_wait` | ISO duration | PT2M | PT10S to PT30M; must be >= window | No |
| `pipeline.max_attempts_per_stage` | int | 3 | 1 to 10 | No |
| `pipeline.concurrency.per_scope` | int | 1 | 1 to 8 | No |
| `pipeline.concurrency.global` | int | 8 | 1 to 64 | Yes if raised above 16 |
| `pipeline.kick_interval` | ISO duration | PT15S | PT1S to PT2M | No |
| `pipeline.stuck_after` | ISO duration | PT5M | at least 3x kick interval | No |
| `pipeline.checkpoint_retention` | ISO duration | P30D | P1D to P365D after a run completes | No |
| `pipeline.fanout.provider_timeout` | ISO duration | PT30S | PT1S to PT10M | No |
| `pipeline.sweep.enabled` | bool | true | | No |
| `jobs.max_consecutive_failures` | int | 5 | 1 to 20 | Yes if raised above 5 |
| `jobs.native.max_threads` | int | min(4, CPU count) | 1 to CPU count | No |
| `jobs.cpu.max_processes` | int | 2 | 1 to CPU count | No |
| `jobs.cpu.job_timeout` | ISO duration | PT30M | PT1M to PT24H | No |
| `jobs.idle.recheck_interval` | ISO duration | PT1M | PT10S to PT1H | No |
| `jobs.run_history_retention` | ISO duration | P30D | P1D to P365D | No |
| `jobs.schedule.enabled` | bool | true | | No |
| `jobs.schedule.min_interval` | ISO duration | PT5M | floor PT1M | No |
| `jobs.schedule.grace` | ISO duration | PT1M | ceiling PT5M | No |
| `jobs.schedule.max_per_principal` | int | 20 | 1 to 200 | No |
| `jobs.schedule.disabled_actions` | list | empty | operator may disable any action, never enable an unregistered one | No |

**Not settings (invariants):** the per-item and per-stage-boundary cancel check; the escalation latch; "unavailable means not idle"; "the allowlist starts empty and rejection happens at creation"; "re-enable is an explicit action and force does not clear the guard"; "a duplicate job kind throws"; and the rule that a checkpoint write failure fails the attempt. A test asserts that none of these has a setting key.

## 11. Audit events and Kodo health

All entries use `IAuditLog.AppendAsync` with `Actor` written as the canonical `Principal` text and a payload of ids and counts only (no content, no secrets; the audit payload guard already blocks secret-like keys).

| Kind | Actor | Payload |
|---|---|---|
| `pipeline.run.opened` / `.closing` / `.completed` | `service:pipeline-runs` | run id, pipeline id, scope key, item count, rules version |
| `pipeline.run.paused` / `.resumed` / `.cancelled` | the requesting principal (`user:dom`, `agent:charles`, or `system:` for a foreground-yield pause) | run id, reason |
| `pipeline.stage.escalated` | `service:pipeline-runs` | run id, item id, stage, attempts |
| `pipeline.config.reloaded` | `service:pipeline-runs` | old and new reload count, changed keys |
| `job.kind.registered` | `system:job-classes` | kind, class, owner module (once per kind per version, not per start) |
| `job.kind.disabled` | `service:job-classes` | kind, consecutive failures, last error head |
| `job.kind.enabled` | the re-enabling principal | kind, who, why |
| `job.schedule.created` / `.updated` / `.deleted` | the principal | schedule id, action id, cron, owner, created by |
| `job.schedule.rejected` | the principal | action id, reason code |
| `job.schedule.fired` / `.missed` | `service:job-classes` | schedule id, occurrence |

Per-stage and per-item events are not audited (volume). They are queryable from the tables.

**Kodo health contract.** Both modules implement `IModuleHealth` and inherit `ModuleHealthContractTests<TModule>` from `Bazaar.Kodo.Testing` (doc 46 section 10).

- **Liveness** (no I/O): the run executor loop and the schedule evaluator report their last in-memory tick time; stale beyond a bound fails.
- **Readiness** (cached dependency state): `pipeline-runs` is ready when Postgres and `job-classes` are ready; `job-classes` when Postgres and the Jobs abstractions are ready. A missing `IWorkerProcessRunner` degrades readiness to `Warn` if any `CpuProcess` kind is registered. Soft dependencies (`kodo`, `kanban`, `agents`, `ryusei-schedule`) only degrade.
- **Dependencies**: declared with Hard or Soft as in section 5.
- **Self-test**: `Quick` checks that the transition table is closed (terminal states have no outgoing edge; no state is unreachable), the registry has no duplicate or class-less kind, and the cron parser accepts and rejects a fixed probe set. `Full`, inside a rolled-back sandbox transaction, runs a three-stage fake pipeline with an injected failure at stage two and verifies the retry resumes at stage two, the escalation latches once, a cancel lands within one item, and the sandbox leaves no residue.
- **Kodo probes** (read-only, added to the probe catalogue of doc 46 through the manifest): `pipeline.stuck_run` (Open or Closing run with a kick older than `pipeline.stuck_after`), `pipeline.escalations_open`, `jobs.disabled_kinds` (any auto-disabled kind is amber and links the last error), `jobs.registry_drift` (a `job` row whose kind is not in `job_kind`), `jobs.idle_starvation` (an `IdleTime` kind that has been deferred longer than a bound; informational, since a permanently busy system is legitimate), and `jobs.lease_loss` (jobs reclaimed by another owner while running, the signal for finding F1).

## 12. What is taken from Resibo, and what is changed

| Resibo source | Taken | Changed for Bazaar and why |
|---|---|---|
| `v3-deepdive-10` section 3 | Transitions as data, terminal cancel state, errors as data | No unreachable `RUNNING`; add `Completed`; rename cancel terminal. Bazaar has no legacy callers and needs run history. |
| Deep-dive 10 section 5.1 | Global and per-scope limits, fixed acquisition order, release in `finally` | Scope is a generic key, not a user. Enforcement is an admission over the job table so a restart does not lose it. |
| Deep-dive 10 section 5.2 | Debounce with a ceiling measured from `opened_at`, new run after ceiling | Persisted in `pipeline_run`; injected `TimeProvider`. |
| Deep-dive 10 section 6 | Per-stage checkpoints, resume at first miss, asymmetries, content-hash idempotency | Reference output, not content. Stored in Postgres, not in memory (Resibo's `services/execution_core/CLAUDE.md` records its in-memory checkpoint store as a known gap). |
| Deep-dive 10 section 7 | Bounded retry, latched single escalation, outcome as data, count on failure | Escalation goes to an `IEscalationSink`, not a hard-coded flag type. |
| Deep-dive 10 section 8 | Cancel at the top of every item | Also at every stage boundary; token passed into stages; no setting. |
| Deep-dive 10 section 9 | Kicks inside the loop, failure-tolerant; staged and active config with a reload counter | Kick also renews the job lease; no watchdog service exists, so a Kodo probe reads `last_kick_at`. |
| `core/background_workers/CLAUDE.md`; deep-dive 12 sections 3 and 4 | Declared job class and routing; idle-only gating | `IdleTime` is a fourth class with a substrate; `CpuProcess` has no inline fallback. ("Unavailable means not idle", per-scope idle and scheduler-isolated failure are Bazaar's own rules, PROPOSED.) |
| `KNOWN_JOBS` (`core/background_workers/CLAUDE.md`); deep-dive 12 sections 6 and 9 | One consolidated registry; completeness test both ways | Table lives in this doc and is checked by an architecture test. |
| `MAX_CONSECUTIVE_FAILURES`; deep-dive 12 section 10 | Per-kind auto-disable; one escalation; skip is not failure; explicit re-enable; force does not clear | Cap is a setting (range 1 to 20, default 5), per the configurability rule. |
| `core/task_scheduler/CLAUDE.md`; deep-dive 39 | Allowlist, empty by default, rejection at creation, own cron parser, no catch-up, wake-provider registry, fail-safe when no waker | Allowlist is the schedulable subset of the job-kind registry; owner and creator are separate `Principal`s. |
| `v3-deepdive-17` Reconciliation (correction propagation, check inventory) | Only the pattern: one function with live and retroactive orchestrators; sweeps are idle-time jobs | None of the 12 receipt checks. `rules_version` marks stale items. |
| `v3-deepdive-25` Review/Flagging | Lifecycle, dismissed distinct from resolved, shared queue, severity split | Recorded as a pattern and as `IEscalationSink`; no flag taxonomy and no receipt audit screen. |
| `v3-deepdive-15` and `-16` Matching and Geo | Parallel provider set, two concurrent axes, per-provider rate gates, caller-side corroboration, corroboration policy enum | Not matching or geocoding; only the runner and policy enum. |

## 13. Interaction with existing modules, security and privacy

**Interaction.**

- *Jobs (doc 03, `src/Bazaar.Server/Jobs`)*: the only queue; section 4. Both modules require the five seams of section 4.2 to be present in the Jobs abstractions. Doc 03 lists background jobs as PROPOSED, and doc 40 plans their extraction; this doc adds requirements to that extraction.
- *Agent runtime (doc 06, `Bazaar.Server.Agents`)*: unchanged. Agent turns recover through `agent_lease` and `turn_step`. A pipeline stage that runs an agent turn does so through the existing agent contracts and stores the turn id as its `OutputRef`. The two journals do not merge: one is per turn step, the other per item stage.
- *RyuseiFuda (doc 38)*: its tick becomes a registered `Io` kind; triggers stay its own (section 7.8). A trigger action that starts a pipeline does so by enqueuing, with no authority of its own.
- *Kanban (doc 12) and PR review (doc 21)*: consumers through `IEscalationSink` and `pipeline.run` events. Reviewer approval and card transitions keep their existing guard.
- *Work-life simulation (doc 09)*: an off-shift agent is an idle source for agent-scoped sweeps; the simulation is not changed.
- *Kodo (doc 46)*: probes and health contract (section 11). No change to Kodo code is required until a heartbeat API exists.
- *Approvals and taint (doc 15)*: a pipeline stage runs with the principal and taint state of whoever started the run; a stage that touches untrusted input inherits the same high-impact approval rule as any session. A user schedule's `RequiredRoles` is checked at creation and again at fire time against the owner's current role.
- *Rate limits*: provider-set gates and schedule creation limits use `IRateLimiter`.
- *Backup (doc 33)*: `pipeline_run`, `pipeline_checkpoint`, `user_schedule` and `job_kind_health` are included in the install backup class with other structured state; `job_run` is optional.
- *Erasure and export (docs 33, 44)*: checkpoint rows carry references, so crypto-erase of the referenced blob leaves a dangling reference that reads as "erased". `user_schedule.owner` is a personal reference and follows the erasure design (itself OPEN).

**Security and privacy.**

- No stage code or shell string is stored or executed from a schedule. Parameters are schema-checked data; actions are code-registered.
- Audit payloads carry ids and counts only. Checkpoints store references. Error text is capped at 2000 characters and passes the existing secret scrubber before storage.
- `CpuProcess` workers run as a child process of the host with no inherited secrets and a minimal environment; secrets reach a stage only through the Identity Service injection rule. The provider must refuse to start under a more privileged account than the host.
- Cancelling or re-enabling needs a role (`operator` or the run's owner); the check fails closed. A re-enable by an `agent` principal is refused (an auto-disabled job needs a human to look at why).
- Fail closed on every security check, degrade on every monitoring check: a failed idle read, kick or narrative never fails a run; a failed permission read, schema check or checkpoint write never lets work proceed.
- Cron time zones are IANA identifiers from the host tz database (via the platform provider on Windows), validated at creation.

## 14. Testing, work packages and open questions

### 14.1 Testing (xUnit v3; `Bazaar.Testing` fixtures; Avalonia.Headless for the pane)

- State machine: every legal edge applies, every other edge returns `Applied = false`, terminal states have no outgoing edge, no state is unreachable.
- Coalescer: a trickle never holds a run past the ceiling (fake `TimeProvider`, no sleeping); a trigger past the ceiling opens a new run and closes the old one; a late trigger never joins a `Closing` run; the sweep closes on either window or ceiling.
- Checkpoints: resume at the first miss; a set with a hole resumes at the hole and not after it; checkpoint write failure fails the attempt; narrative failure is swallowed; a resumed stage emits no narrative; empty content hash never matches.
- Retry: cap checked before work; counter increments on failure only; escalation latched under 32 concurrent callers (exactly one sink call); sink failure releases the latch; outcome is data.
- Cancel: stop lands within one item (instrumented 200-item run); cancel mid-stage observes the token; `Cancelled` cannot reopen; no setting for the check exists.
- Reload: staged config does not change a running run; `ReloadCount` ticks only on a real change.
- Kicks: count is at least the number of items; a throwing kicker does not fail a run; a kick renews the lease.
- Fan-out: parallel not sequential (timing with a fake clock); one failing provider yields a failed result, not a failed run; per-provider limit applied; every result returned.
- Class router: each class lands on its substrate; `CpuProcess` with no provider fails closed; a throwing job never propagates; a job that is `Native` does not block an `Io` job.
- Idle: `Unknown` is `Busy`; per-scope idle does not block another scope; reason always set; a deferred job does not consume an attempt.
- Guard: five consecutive failures disable and escalate once; a skip is not a failure; force does not clear; re-enable by an agent is refused; the counter resets only on re-enable.
- Registry: duplicate throws; **completeness both ways with a seeded-violation test each**.
- Schedules: empty allowlist rejects everything; rejection at creation for unknown action, bad schema, bad cron, missing role, cap; missed run does not fire and does not burst; non-overlapping windows; dedupe collapses a double tick; no `IModuleWaker` rejects a sleep-capable action.
- Postgres-backed integration tests: concurrent claim under `SKIP LOCKED`, lease loss and reclaim with `ExtendLeaseAsync`, restart mid-run resumes from checkpoints, cancel NOTIFY.
- Architecture tests: both modules reference only their allowed projects; no Windows-only API; no files in `common/`; manifests and registration tests present.
- Kodo: both modules inherit `ModuleHealthContractTests<TModule>`.
- Headless: the Run Monitor pane renders runs, states, kicks and escalations, and keyboard navigation reaches every action (mnemonics, no Ctrl chords).

### 14.2 Work packages

Each is small, in its own project with tests. Order: J0 first; then P and J streams in parallel; consumers last.

| WP | Content | Depends on |
|---|---|---|
| J0 | Jobs abstractions extraction with the five seams of section 4.2 (`IJobAdmission`, `DeferAsync`, `ExtendLeaseAsync`, `SkipAsync`, `IJobOutcomeObserver`); poller concurrency fix for finding F1 | doc 40 extraction; `JobStore`, `JobPoller` |
| J1 | `Bazaar.Server.JobClasses.Abstractions`: `JobClass`, registrations, `IJobKindRegistry`, `IIdleSource`, `IModuleWaker`, schedule records | J0 |
| J2 | Registry and migration `job_kind`; duplicate throw; persisted at start | J1 |
| J3 | Class router decorator; `Native` thread pool; `IdleTime` admission with idle sources | J1 |
| J4 | Registry-completeness test and the Known jobs table check; register today's jobs | J2 |
| J5 | Failure guard: `job_kind_health`, observer, auto-disable, latch, re-enable, audit | J2, J3 |
| J6 | `Bazaar.Server.JobClasses.WorkerProcess` provider (`IWorkerProcessRunner`) | J1 |
| J7 | Cron parser and `Evaluate` (pure) | none |
| J8 | Schedulable-action registry, `user_schedule`, creation validation, `jobs.schedule_tick`, audit | J2, J7 |
| J9 | `IModuleWaker` fail-safe path, wake arming for sleep-capable actions | J8 |
| J10 | OS wake providers (Windows, systemd), only if question 4 is yes | J9 |
| P1 | `Bazaar.Server.PipelineRuns.Abstractions` and the run transition table | none |
| P2 | `pipeline-runs` migrations and repositories for run, item, checkpoint, attempt | P1 |
| P3 | Checkpoint store, `RunStage`, `CompletedStages`, content-hash idempotency | P2 |
| P4 | Attempt counter, escalation latch, `IEscalationSink` | P3 |
| P5 | Coalescer and open and closing phases, finalizer | P2 |
| P6 | Executor job `pipeline.run`; slot gate admission; cancel signal with NOTIFY | P5, J0, J3 |
| P7 | Config reloader; kicks and lease renewal; `last_kick_at` | P6 |
| P8 | `ProviderSetRunner`, `ICorroborationPolicy`, rate-limit gates | P1 |
| P9 | Sweep orchestrator (`rules_version`, idle-time `pipeline.sweep`) | P6, J3 |
| K1 | `IModuleHealth` for both modules; Kodo probes | P7, J5 |
| C1 | `Bazaar.Client.PipelineRuns` Run Monitor pane and schedule pane (headless tests) | P6, J8 |

### 14.3 Findings the owner should know

- **F1. The existing poller runs claimed jobs sequentially while every lease clock is already running.** `JobPoller.PollOnceAsync` claims up to `BatchSize` (10) jobs, each with a 5 minute lease, and runs them one by one. A slow first job can let the later jobs' leases expire before they start, and a job longer than the lease is reclaimed and run twice. Doc 03's handlers are required to be idempotent, which limits the damage, but it is a latent defect. J0 fixes it with concurrent per-class dispatch and lease extension.
- **F2.** `job.state` is a frozen CHECK (`queued|running|succeeded|failed`) in the frozen `common/` namespace. Skips are therefore stored as `succeeded` with a `last_error` prefix, and `job_kind_health` is the source of truth.
- **F3.** The doc-index lists two documents numbered 40. This doc cites `40-core-feature-modules-audit.md` for the Jobs extraction and component manifests, per its prompt.

### 14.4 Open questions

1. Owner confirmation that CONVERGENCE's "fold Resibo ideas into decided libs = NONE for now" (2026-11-19 card) concerns only the extracted shared libraries and not Bazaar modules, as read in section 2. OPEN.
2. Should user schedules (J7 to J9) be their own module `Bazaar.Server.ScheduledActions` instead of living in JobClasses? Kept in JobClasses because the prompt names it there and it reuses the registry; the split is cheap if wanted. OPEN.
3. Keep the failure cap a setting (this doc) or fix it at 5 as Resibo does? OPEN. Default is 5 either way.
4. Is waking a stopped host from the OS scheduler wanted (J10 providers), or only waking sleeping modules inside a running host? Needs the sleep and wake design (CONVERGENCE B6), which does not exist. OPEN.
5. Pipelines Bazaar should adopt first (PR review, Vault reindex, build). None are decided here; the owning docs (21, 18, Company Build) must opt in. OPEN.
6. Does the review-queue pattern of section 8 become its own module `Bazaar.Server.ReviewQueue`, or stay as `IEscalationSink` implementations in Kanban and PR modules? OPEN.
7. Multi-node concurrency limits (database-counted slot gate) and multi-node idle sources. OPEN until the several-node design exists.
8. Kodo heartbeat API shape (CONVERGENCE B5). `pipeline.last_kick_at` plus a probe is the interim; the adapter moves to Kodo's API when it exists. OPEN.
9. Whether agent principals may create user schedules at all, or only people (this doc allows it only when the action's roles say so). OPEN.
10. Default `pipeline.concurrency.global` should follow the VM and hardware tiering decision (CONVERGENCE B8, VM sizing OPEN). 8 is Resibo's number, not a Bazaar measurement. OPEN.
11. Exact kind strings for `ryusei.trigger_tick`, `agents.lease_recovery` and `backup.base_backup` in the seed table are to be confirmed against the code in J4. OPEN.
