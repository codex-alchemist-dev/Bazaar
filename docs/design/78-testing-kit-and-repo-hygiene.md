# 78. Testing kit extensions and repo hygiene (Bench, FailureInjection, InterfaceWalker, Fuzz, Load, Hygiene)

Status: PROPOSED. The owner's direction of 2026-11-19 to adopt Resibo's test and hygiene features, and to split anything that conflicts with Bazaar's design into a separate module instead of choosing, is DECIDED as direction ("incorporate everything from Resibo that can benefit Bazaar; where something conflicts, make it a separate module; account for everything"). The module names, numbers, contracts and rules below are PROPOSED, except where a row restates a DECIDED item of doc 34 or CLAUDE.md. Nothing here is built.
Depends on: 34-testing-strategy.md (layers, settings, verification gate), 03-server-host-and-providers.md (settings registry, providers), 04-postgres-schema-and-migrations.md (migration conventions), 15-security-taint-and-approvals.md (approvals, taint), 40-core-feature-modules-audit.md and BAZAAR-COMPONENTS.md (module rules, `Principal`, section 4.2), 41-client-gui-customization-and-dsl.md and 41c-rubedo-view-declarations.md (commands, panes, views), 46-kodo-health-module.md (health contract, chaos mode), 47-licensing-and-trademarks.md (dependency licence check), `docs/FORWARD-FACING.md`, `docs/pr-flow.md`, `docs/ci.md`.
Used by: 34 (L10, WP12 become Bench), 16 (benchmark harness), 17 (benchmark runner), 19 and constellation-scale-and-lod.md (Constellation benches), 31 (NPU harness), 43 (fuzzing of redaction), 46 (chaos mode, module contract tests), 49 (operations), every module's Testing section and PR.

## 1. Purpose

Doc 34 decides how Bazaar proves its code: layers L0 to L12, real PostgreSQL, scripted gateway, cassettes, the server-run verification gate. `Bazaar.Testing` already ships the base pieces (`FakeClock`, `DeterministicIds`, `TestSeed`, `SeededRun`, the Postgres fixture, replay, live-eval and its credential guard).

Resibo (the sister program, `receipt-system-v3`) proves its code with ideas that Bazaar does not yet have: a benchmark suite that runs the real pipeline with per-case process isolation, deliberate failure injection, an interface walker that can never go stale, fuzzing of parsers that touch untrusted bytes, a concurrent-load harness, and a set of repository rules enforced by CI (per-category PR templates with a parsed checklist, a file-size ceiling, one concern per file, forward-compatibility by feature detection). This doc adopts all of it as separate modules and rules, and says exactly where it overlaps what exists.

It also settles three loose ends: how the owner's rule "every PR names the docs section it implements" and Resibo's checklist become one check; where real data may and may not go (never CI, never git); and how Resibo's "forward-compatibility pattern" merges with `docs/FORWARD-FACING.md` without breaking the CLAUDE.md rule that Bazaar adds no code path for an older .NET runtime.

## 2. Decided inputs

- Owner direction, 2026-11-19: adopt everything from Resibo that benefits Bazaar; overengineer; where a Resibo idea conflicts with Bazaar's design, make it a separate module; account for everything. DECIDED as direction.
- Convergence decision 24 ("Test kit": union of both kits, owner already decided a testing kit repo) and Resibo-to-Bazaar items B11 and B12 (`/mnt/project-files/convergence/CONVERGENCE.md` sections 4 and 8). Row 1 there also says Resibo's file-size ceiling and one-concern-per-file rule move into the testing kit. These are working notes, not DECISIONS-LOG entries.
- Doc 34 (all of it): xUnit v3 first, real PostgreSQL, the server verifies and agent self-reports never count, no skip, no retry-to-green, the verification gate, `test.*` settings, Resource Economy rejected (so no money-gate tests).
- CLAUDE.md: every feature is its own module on Bazaar.Core with an `.Abstractions` project; Bazaar.Server and Bazaar.Client.Core gain no feature code; every PR carries `Docs section this implements: <doc path and section>`; forward-facing rule (newest .NET, no code path for an older runtime); UI changes get an Avalonia.Headless test; real GC transcripts and Soul data never enter the public repo.
- Owner shared-library rule (2026-11-19 direction, via the convergence work): shared libs and modules hold contracts and logic only, with no third-party dependencies; providers are separate modules.
- `Principal` (BAZAAR-COMPONENTS section 4.2, owner 2026-11-19) is the actor type for every record of who acted.
- Configurability rule (DECISIONS-LOG round 13): undecided values are operator settings with safe defaults; DECIDED items and security invariants are not settings; settings may tighten, never loosen below the floor.
- CI facts (doc 34 Question 1, DECIDED 2026-11-18; `docs/pr-flow.md`): required jobs run on the owner's self-hosted runners only for the owner's own branches; fork PRs get no secrets; required check names must never change; checks have no paths filter.

## 3. What this doc does not decide

- The module kernel, manifest schema and health contract (BAZAAR-COMPONENTS, doc 42, doc 46). This doc proposes one optional manifest field (section 6.7) and marks it OPEN.
- The verification gate of doc 34 (clean clone, test counts, new `Skip`, and so on). It stays as is. This doc adds PR-level and repo-level checks next to it (section 11.2 states the difference).
- Whether a Proving Grounds or Dependencies Warden module exists (B2, B3 in the convergence notes). None was found in `docs/design` on 2026-11-19. If one is written, it consumes the runners in section 8 and does not re-implement them.
- Performance targets. Bench records and trends them (`test.perf.gate_mode`, doc 34); thresholds wait for spike numbers.
- Which fuzzing engine, if any, beyond the seeded in-house mutator (OPEN, section 16).
- Any change to CLAUDE.md, the README, the DECISIONS-LOG or existing docs. The main session makes those (section 15 lists what needs it).

## 4. What exists, and what is reused (overlap statement)

Nothing here duplicates the following. Each is reused or extended as stated.

| Existing | Where | How this doc uses it |
|---|---|---|
| `Bazaar.Testing`: `FakeClock`, `DeterministicIds`, `TestSeed`, `SeededRun` (logs seed and clock on failure) | `src/Bazaar.Testing` | Every new module takes its randomness and time from these. Fuzz, Load and FailureInjection never read the wall clock or `Random.Shared`. |
| `PgFixture`, `PgTemplate`, `PgTestDatabase` (database named `<prefix>_<guid>`) | `src/Bazaar.Testing/Postgres` | The only database any runner may reset (section 8.4). Bench and Load use the same fixture. |
| `ReplaySanitizer`, `ReplayFixtureLoader` | `src/Bazaar.Testing/Replay` | The hygiene check "fixtures are sanitizer fixed points" (section 11.7). |
| `LiveEvalCredentialGuard` (findings carry field path and rule, never the value) | `src/Bazaar.Testing/LiveEval` | Its finding shape is the model for the corpus guard (section 9). The key-prefix rules are reused by the no-real-data check. |
| Doc 34 L7 property, L10 performance, WP12 "Benchmarks: graph, gateway overhead, replay of 10k" | `34-testing-strategy.md` | L10 and WP12 are implemented by `Bazaar.Testing.Bench`. Settings `test.perf.gate_mode`, `test.flake.*`, `test.cassette.record_mode` stay owned by doc 34. |
| Existing benchmark projects (`tests/Bazaar.Constellation.Bench`, `tests/Bazaar.Constellation.Vulkan.Bench`) and planned harnesses (doc 16 WP11, doc 17 WP13, doc 31 WP12) | docs 16, 17, 19, 31 | They become workload providers on the Bench contract (section 5.1). Moving the Constellation projects is OPEN (section 16). No second harness is built. |
| `Bazaar.Architecture.Tests`: `ArchitectureRules`, `SeededViolationTests`, `ModuleStructureTests` with `architecture-baseline.txt` (ratchet: a new violation fails, a listed file that disappears must be removed, so the list only shrinks) | `tests/Bazaar.Architecture.Tests` | The ratchet and the seeded-violation pattern are the template for every hygiene gate (section 11.1). The file-size and one-concern gates reuse the same baseline mechanism with their own baseline file. |
| `Bazaar.Kodo.Testing` (`ModuleHealthContractTests<TModule>`) and Kodo chaos mode (doc 46 section 17.3: kill a module, slow a dependency, drop frames, fill a disk, skew the clock) | doc 46 | FailureInjection supplies the fault primitives. Kodo's chaos catalogue consumes them through `Bazaar.Testing.FailureInjection.Abstractions`. Kodo keeps its own signed catalogue, hypothesis and resilience score; none of that is duplicated here. Adoption by Kodo is OPEN. |
| Accessibility gate and headless harness (`tests/Bazaar.Accessibility.Gate.Tests`, `Headless/FrameCapture.cs`, `TestAppBuilder`) | `tests` | The walker's Avalonia provider reuses them, and `FrameCapture` moves into a shared project (WP10). |
| Rubedo view validator and golden corpus (41c section 8), command registry and `SkippedServerCommands`, pane registry, `MnemonicTable`, `MnemonicValidatorTests` | docs 41, 41c; `src/Bazaar.Client.Core` | The walker calls the validator and the live registries. It adds cross-resolution against what is running and a render pass; it does not re-validate view syntax. |
| `.github/pull_request_template.md`, `pr-sponsor.yml`, `ci.yml`, `docs/pr-flow.md` | `.github` | The existing template becomes the shared header of every category template. The sign-off check is untouched. |
| `docs/FORWARD-FACING.md` (dependency inventory, dual-version rule, canary, Dependabot, Renovate, adoption rule) | `docs` | Kept as the dependency inventory. Section 12 adds the code-level pattern and its checks. |
| Doc 43 fuzz line (seeded generator, redaction residual scan) and doc 46 17.4 (journal decoder and manifest parser fuzzing) | docs 43, 46 | Both become `IFuzzTarget`s on `Bazaar.Testing.Fuzz`. |

Overlap found that the owner should know: `Bazaar.Testing` references Npgsql and Testcontainers directly. That conflicts with the rule that shared libs hold no third-party dependencies. The new modules avoid it by depending only on `Bazaar.Testing.Abstractions`. Splitting the Postgres fixture into a provider (`Bazaar.Testing.Postgres`) is proposed as WP1b and left OPEN, because it moves an existing project.

## 5. Design: module map

All names are PROPOSED. Test-kit libraries are build-time and never load into the server host. The two runtime modules are `Bazaar.Server.TestOrchestration` (and its optional client half). Third-party packages appear only in the provider modules marked P.

| Module | Kind | Third-party deps | Holds |
|---|---|---|---|
| `Bazaar.Testing.Abstractions` | lib | none | `TestOutcome`, `CaseResult`, `RunSummary`, `CorpusRef`, `Boundary` labels, `IDoubleRegistry` |
| `Bazaar.Testing.Bench` + `.Abstractions` | lib | none | Real-pipeline benchmark runner, per-case process isolation, resource classification |
| `Bazaar.Testing.FailureInjection` + `.Abstractions` | lib | none | Fault scopes and primitives: crash, timeout, disk full, clock skew, connection kill, corrupt write |
| `Bazaar.Testing.InterfaceWalker` + `.Abstractions` | lib | none | Walk sources for commands, panes, Rubedo views, mnemonics |
| `Bazaar.Testing.InterfaceWalker.Avalonia` (P) | provider | Avalonia.Headless | Render pass and key-driven traversal |
| `Bazaar.Testing.Fuzz` + `.Abstractions` | lib | none | Seeded structure-aware mutators, targets, crash corpus |
| `Bazaar.Testing.Load` + `.Abstractions` | lib | none | Virtual users, scenarios, latency histogram, percentile report |
| `Bazaar.Testing.Conformance` + `.Abstractions` | lib | none | Discovery of doubles, "double passes the real contract tests" rule |
| `Bazaar.Testing.Hygiene` + `.Abstractions` | lib | none | PR checklist parser, size ceiling, one-concern, naming, non-finding, forward-compat scanner, module guide check, review-comment parser |
| `Bazaar.Testing.Hygiene.Cli` | exe | none | Runs the gates in CI and locally, emits GitHub annotations |
| `Bazaar.Testing.Xunit` (P) | provider | xunit.v3 | Attributes (`[Guarantee]`, `[PinsNonFinding]`, `[ForwardCompat]`, `[Corpus]`), conformance base classes, trait wiring |
| `Bazaar.Server.TestOrchestration` + `.Abstractions` | feature module | none (uses the above) | Runner registry, run storage, agent-callable test runs |
| `Bazaar.Client.TestOrchestration` | client module | none | Optional Rubedo view of runs (section 6.6) |

Rule: no production project references any `Bazaar.Testing.*` except `Bazaar.Server.TestOrchestration` (and its client half). An architecture rule with a seeded violation enforces it.

### 5.1 Bench (`Bazaar.Testing.Bench`)

Taken from Resibo `docs/testing/TOOLKIT.md` ("Bench suite") and `docs/PRINCIPLES.md` section 3.2.

```csharp
// Bazaar.Testing.Bench.Abstractions
public interface IBenchWorkload
{
    string Id { get; }                         // "gateway.overhead", "gc.append", "constellation.layout.10k"
    string OwnerModule { get; }                // the module that ships the workload
    BenchBoundaries Boundaries { get; }        // which external edges are real, which are labelled stand-ins
    ValueTask<BenchCase[]> EnumerateAsync(CancellationToken ct);
    ValueTask RunCaseAsync(BenchCase c, IBenchContext ctx, CancellationToken ct);  // runs inside the child process
}

public sealed record BenchCase(string WorkloadId, string CaseId, IReadOnlyDictionary<string, string> Parameters);

public enum CaseOutcome { Passed, Failed, Crashed, TimedOut, Skipped, Refused }

public sealed record BenchSample(
    string CaseId, CaseOutcome Outcome, TimeSpan Wall, TimeSpan CpuTime, long PeakWorkingSetBytes,
    int Gen0, int Gen1, int Gen2, IReadOnlyDictionary<string, double> Metrics,
    IReadOnlyList<string> Boundaries,           // labels of anything that was not the real thing
    string? Reason, int? ExitCode);

public enum ResourceClass { CpuBound, MemoryBound, IoBound, Mixed }
```

Rules:

1. **Real pipeline, never mocked.** The measured graph is built from the real implementations. `IDoubleRegistry` (section 10) knows which types are doubles; if any instance of one is reachable in the measured graph, the case is `Refused` with reason `double-in-measured-graph`. This is Resibo's "never mocked" rule, enforced mechanically.
2. **Labelled boundaries (a deliberate difference from Resibo).** Bazaar cannot call a paid model on every bench run (doc 34: live evals are private and non-gating). A workload may therefore declare an external edge (for example the model upstream) as a labelled boundary served by a local scripted server. The sample carries `Boundaries: ["scripted:gateway-upstream"]`, and trend charts never mix labelled and unlabelled series. The strict mode (`test.bench.allow_boundaries=false`) refuses any workload with a boundary, for runs where the owner wants the pure real pipeline.
3. **Per-case process isolation.** The runner spawns one child process per case (`Process.Start`, BCL only) from the workload's own assembly. A native crash, a stack overflow, an out-of-memory kill or a hang is recorded as `Crashed` or `TimedOut` for that case. The run continues. This is Resibo's "CRASHED, not taken down with the whole run". Exit code and the last 8 KB of stderr (scrubbed by the doc 23 scrubber) are kept.
4. **Resource classification.** From wall, CPU time and peak working set the runner labels each workload `ResourceClass` (ratio thresholds are settings, section 6.2). Doc 16's VM sizing and doc 31's device tiering read these labels instead of inventing their own measurement.
5. **Concurrent-user phase.** After the single-case phase, a workload may run a second phase through `Bazaar.Testing.Load` (section 5.5) at N virtual users against the same real pipeline. The two phases are reported together. This is the "concurrent-user phase" the owner asked for; Bench owns orchestration, Load owns the generator, so the generator exists once.
6. **Reasoned, then measured.** A default that was derived from a bench result carries a `[BenchDerived("workload-id")]` marker beside the constant (the attribute lives in `Bazaar.Testing.Bench.Abstractions`). A hygiene check (section 11.6) lists markers whose workload id does not exist, so a "bench-derived" default cannot silently lose its bench. Changing such a constant without re-running its workload is caught in review by the PR checklist item in section 11.3. This is Resibo's PRINCIPLES section 5 rule, adapted.
7. **Trend, not gate, by default.** `test.perf.gate_mode` is doc 34's setting and stays `trend` until spike numbers exist. Samples go to `bench_series` (section 6.3).

### 5.2 FailureInjection (`Bazaar.Testing.FailureInjection`)

Taken from Resibo `TOOLKIT.md` ("Failure-injection mode") and the crash-isolation runner in `docs/apis/v3-deepdive-56-test-orchestration.md` section 3.2. Resibo only had a process kill; Bazaar's list is wider because Kodo's chaos list (doc 46 17.3) already names the cases.

```csharp
// Bazaar.Testing.FailureInjection.Abstractions
public interface IFault
{
    string Id { get; }                              // "crash.process", "timeout.stream", "disk.full", "clock.skew", ...
    FaultSafety Safety { get; }                     // InProcess, ChildProcess, SandboxOnly
    ValueTask ArmAsync(FaultTarget target, FaultPlan plan, CancellationToken ct);
    ValueTask DisarmAsync(CancellationToken ct);    // idempotent; also called by scope disposal
}

public sealed record FaultPlan(TestSeed Seed, TimeSpan? After, double? Probability, int? Count, TimeSpan MaxDuration);

public sealed record FaultTarget(string ModuleId, string? ProcessRef, string? Resource);   // never a bare process name

public sealed record FaultExpectation(                      // the hypothesis, as in Kodo's catalogue
    string ExpectedState, TimeSpan RecoverWithin, bool DataLossAllowed /* always false */, string[] ExpectedAlerts);

public sealed class FaultScope : IAsyncDisposable { /* arms on enter, disarms on dispose, records every action */ }
```

Fault set (each is its own small class, one concern per file):

| Fault id | Safety | How |
|---|---|---|
| `crash.process` | ChildProcess | Kills a named child process by `ProcessRef` handed out by the component host or the bench runner. Never `pkill` by name (Resibo section 3.2: "never a blind pkill"). Verifies the parent module stayed healthy, that the next request reloads cleanly instead of hanging, and that no other module was affected. |
| `crash.inproc` | InProcess | A wrapper throws at a named seam (for example `IGcWire.AppendAsync`), after N calls. |
| `timeout.stream` / `timeout.call` | InProcess | Delegating wrappers that stall past the caller's budget; time comes from `FakeClock`, so no test waits on wall time. |
| `disk.full` | InProcess / SandboxOnly | In-process: an `IFileSystem`-style wrapper raises the platform's out-of-space error after a byte budget. Sandbox: a size-limited volume (provider, not in the lib). |
| `clock.skew` | InProcess / SandboxOnly | `TimeProvider` offset, jump forward, jump back, repeated local time. Real OS clock change only inside the sandbox VM. |
| `db.connection_kill` | InProcess | `pg_terminate_backend` on a pooled connection of the test database. |
| `net.drop` / `net.slow` | InProcess | Drops or delays a percentage of frames on the in-process Kestrel test host (L3). |
| `write.partial` / `write.corrupt` | InProcess | Truncates or flips one byte of a write at a named seam, to prove the checksum or chain catches it. |

Hard rules: a fault is armed only inside a `FaultScope` (disposal always disarms); `SandboxOnly` faults refuse unless the process is flagged as a test sandbox; every fault takes its randomness from the logged `TestSeed`; every arm and disarm is recorded as an audit-shaped event (section 6.4); `DataLossAllowed` is always `false` (matches Kodo: any data-loss invariant failure is a finding).

Resibo's convention that every async orchestration point has its own failure-injection case in its design doc's testing hooks becomes a module obligation: a module with out-of-process parts, queues or schedulers lists at least one fault scenario in its Testing section and in its test project. The optional manifest field `testing.faultScenarios` (OPEN, section 6.7) would let Kodo list them.

### 5.3 InterfaceWalker (`Bazaar.Testing.InterfaceWalker`)

Taken from Resibo `docs/PRINCIPLES.md` section 3.2 and `docs/templates/new_menu_item.md` ("CI test: check_menu_data_integrity", "this is also the interface walker"). The key property is kept: the walker reads what the app actually renders from, never a separately maintained fixture, so it cannot go stale.

Resibo's menu data becomes a Rubedo view (convergence row 2), so Bazaar's walker walks Rubedo view declarations, the command registry, the pane registry and the mnemonic table.

```csharp
// Bazaar.Testing.InterfaceWalker.Abstractions
public interface IWalkSource
{
    string Id { get; }                                   // "commands", "panes", "views", "mnemonics", "settings-ui"
    IAsyncEnumerable<WalkNode> EnumerateAsync(CancellationToken ct);
}

public sealed record WalkNode(string Kind, string Id, string? Target, string[] Requires, string Origin);
//   Origin: module id and declaration path, so a failure names the module that must fix it

public sealed record WalkFinding(string Rule, string NodeId, string Message, string Origin);
```

Checks (each is a rule class with a seeded-violation test):

1. **Resolution.** Every `invoke`, `navigate` and `openPane` action (41c section 6) resolves to a registered command or pane; every `setSetting` key is in the registry; every data source named in a view is declared in the manifest with a capability that exists. This is the direct analogue of Resibo's "every target resolves to a registered RPC", and it catches a rename elsewhere that silently breaks a view.
2. **Server command skips.** A server command the client could not load (`SkippedServerCommands` in `CommandLoadResult`) must be listed with a reason; an unexplained skip fails.
3. **Reachability.** Every pane and command is reachable from a mnemonic or the palette. No orphan.
4. **Mnemonic table.** No collisions, no Ctrl chord (owner rule: mnemonic navigation, no Ctrl chords), every mnemonic has a spoken form for the screen reader (`SpokenMnemonic`).
5. **Labels and i18n.** Every `button`, `icon` and `input` has a label key that exists in the shipped language file (41c section 7), and no literal is used for a translated string.
6. **Theme tokens exist** for every token a view names (41c section 5).
7. **Render pass (provider).** `Bazaar.Testing.InterfaceWalker.Avalonia` opens each pane in Avalonia.Headless, waits for a rendered frame (never a sleep), asserts that each widget mapped to a control, runs the accessibility name check, and drives the pane with keyboard events only. A pane that throws renders `placeholder` (41c section 3), and the walker reports it as a finding, not as a pass.
8. **Unknown future widgets** render `placeholder` and are reported at `Warn`, matching doc 41 section 7 behavior.

The walker runs on every PR (doc 34 L4) and is also a local command (`dotnet test --filter Category=Walker`) after any refactor that touches how registries load, as Resibo recommends.

### 5.4 Fuzz (`Bazaar.Testing.Fuzz`)

Taken from Resibo `TOOLKIT.md` ("Format-decoding fuzz pass"): malformed and truncated input against every library that parses untrusted bytes, rerun after bumping any such dependency. Resibo's targets were image and PDF decoders; Bazaar's are different.

```csharp
public interface IFuzzTarget
{
    string Id { get; }                        // "wire.frame", "mail.mime", "vault.markdown", "tile.frame", "journal.decoder"
    IEnumerable<byte[]> Seeds();               // small valid inputs, synthetic only
    FuzzVerdict Execute(ReadOnlySpan<byte> input);   // must not throw unhandled, hang or exceed limits
    FuzzLimits Limits { get; }                 // wall per input, memory, output size
}
public enum FuzzVerdict { Accepted, RejectedCleanly, Violation }
```

Targets proposed (each is a one-file `IFuzzTarget` shipped by the owning module's test project): wire frames and `IWireCodec` JSON, MIME parsing in the mail module, Vault Markdown and `[[wiki-link]]` parsing, Constellation `TileFrameDecoder`, Kodo journal decoder and manifest parser (doc 46 17.4), the telemetry redactor residual scan (doc 43), attachment type sniffing, and archive handling for uploads. Rules: seeded structure-aware mutators only (truncate, flip, splice, repeat, nest too deep, oversize length fields, invalid UTF-8, hidden Unicode); deterministic from `TestSeed`; short fixed iteration count on every PR, long run nightly (`test.fuzz.nightly_iterations`); a failing input is minimized by bisecting length and saved as a crash case under `tests/**/FuzzCrashes/` (synthetic by construction, since seeds are synthetic and mutation never adds outside data); the rerun-after-dependency-bump rule is a PR checklist item for dependency PRs touching a parser (section 11.3). A coverage-guided engine (SharpFuzz or similar) would be a provider module and is OPEN.

### 5.5 Load (`Bazaar.Testing.Load`)

Taken from `TOOLKIT.md` ("Load and concurrency tools"): hot-path APIs "reasoned to be a fast single indexed lookup, not yet measured under realistic concurrent load". Resibo mandated no tool; Bazaar writes the small one it needs so it stays dependency-free.

```csharp
public interface ILoadScenario
{
    string Id { get; }                                   // "gc.append.fanout", "wire.session.validate", "gateway.route"
    ValueTask<IVirtualUser> CreateUserAsync(int index, TestSeed seed, CancellationToken ct);
}
public interface IVirtualUser { ValueTask<StepResult> StepAsync(CancellationToken ct); }
public sealed record LoadProfile(int Users, TimeSpan Ramp, TimeSpan Hold, double? ThinkTimeMsMean);
public sealed record LoadReport(long Steps, long Errors, double P50Ms, double P95Ms, double P99Ms, double MaxMs, double StepsPerSecond);
```

The generator uses one in-process Kestrel host and the real wire client (L3) so a "user" is a real session. Latency goes into a fixed-bucket histogram implemented in the lib (no third-party histogram package). Candidate scenarios taken from design docs: GC append and Postgres NOTIFY fan-out (docs 01, 02), session validation, gateway routing and its own overhead (doc 05), Skimmer scoring queue (doc 13). Errors are data: a run reports the error count and the first distinct error messages; it never turns an error into a pass. Coordinated omission is avoided by scheduling steps on a clock and measuring from the intended start time.

### 5.6 Conformance (`Bazaar.Testing.Conformance`)

See section 10; it is a hygiene rule and a module in one.

## 6. Design: runtime module, settings, data

### 6.1 `Bazaar.Server.TestOrchestration`

Taken from Resibo `docs/apis/v3-deepdive-56-test-orchestration.md` (Agent Control's sub-API) and the inventory entry in `/mnt/project-files/convergence/inventory/resibo-50-56.md` (56). Purpose: make tests that cannot be "run a script and check the exit code" callable by an agent or the owner, returning one structured result. It does not replace ordinary `dotnet test`, does not interpret a result (the caller does), and does not own the kit tools (it is a calling convention over them).

It is not Resibo's gRPC service. Bazaar's surface is the existing authenticated HTTP and `/ws` stream (doc 02) plus the agent tool seam (doc 06 `ToolDispatcher`, doc 15). Exact tool registration is OPEN (section 16).

```csharp
// Bazaar.Server.TestOrchestration.Abstractions
public interface ITestRunner                      // plain module providers on Core: no selector, all are listed
{
    string Id { get; }                            // "bench", "crash-isolation", "pipeline", "env-reset", "fuzz", "load", "walker"
    ValueTask<bool> IsAvailableAsync(CancellationToken ct);   // meaningless runners say so (Resibo: crash-isolation vs a remote session)
    ValueTask<TestResult> RunAsync(TestSpec spec, Principal requestedBy, CancellationToken ct);
}

public sealed record TestSpec(string RunnerId, string TargetModuleId, IReadOnlyDictionary<string, string> Args,
                              CorpusRef? Corpus, TimeSpan Timeout, TestSeed Seed);

public sealed record TestResult(Guid RunId, string RunnerId, RunState State, CaseResult[] Cases,
                                string[] Warnings, string[] Boundaries, TimeSpan Duration, TestSeed Seed);

public enum RunState { Queued, Running, Passed, Failed, Crashed, TimedOut, Refused, Cancelled }
```

Runners (each a plain provider module on Core, one project or one file group; none is selected over another):

- **bench**: front end to section 5.1. Resibo's `BenchSuiteRunner`.
- **crash-isolation**: section 5.2 `crash.process`. Resibo's `CrashIsolationRunner`. Targets come from the component host's process table, never from a name match. Test (from Resibo 8): a run that names module A must never touch module B.
- **pipeline**: Resibo's `PipelineIntegrationRunner`, translated: submit a scripted scenario through the real stack (a synthetic GC message through to a scripted-agent turn, a Kanban transition) and poll until a terminal state, returning the failing stage plus the audit and trace ids. It uses scripted agents and the fake gateway of doc 34; its boundaries are labelled.
- **env-reset**: Resibo's `EnvironmentResetRunner`, translated (section 8.4).
- **fuzz**, **load**, **walker**: thin runners over sections 5.4, 5.5 and 5.3, added because an agent should be able to ask for them the same way.

Agent tools exposed (category: test execution, own category as Resibo's `TEST_EXECUTION`, because they have real side effects): `run_test`, `get_test_result`, `list_test_runners`, `reset_test_environment`. All calls go through the normal approval ladder of doc 15; a run under hard taint needs approval, and `env-reset` needs it always. The server verifies; an agent's claim about a result never counts (DECIDED, doc 34).

### 6.2 Settings registry rows [PROPOSED]

Keys follow doc 03's registry (typed, scoped, audited on change). Doc 34's `test.*` rows stay where they are. "Approval" means a change needs owner approval.

| Key | Type | Default | Scope | Who may change | Approval |
|---|---|---|---|---|---|
| `test.orchestration.enabled` | bool | `false` (off on production installs; a dev or test install turns it on at setup) | install | operator | yes (enabling on an install that holds real company data) |
| `test.orchestration.agent_tools` | enum: `off`, `read-only`, `run` | `read-only` (list and read results only) | install | operator | yes to reach `run` |
| `test.orchestration.result_retention_days` | int | `90` | install | operator | no |
| `test.bench.isolation` | enum: `process` | `process` (the only value; in-process benches are not offered) | install | none (invariant) | n/a |
| `test.bench.case_timeout_seconds` | int | `300` | project | operator | no |
| `test.bench.allow_boundaries` | bool | `true` | install | operator | no |
| `test.bench.cpu_bound_ratio` / `test.bench.memory_bound_mb` | number | `0.85` / `1024` | project | operator | no |
| `test.bench.concurrent_phase_users` | int | `32` | project | operator | no |
| `test.fault.sandbox_only` | bool | `true` (a fault marked `SandboxOnly` refuses outside a sandbox; the value `false` is not accepted on an install that holds real data) | install | operator | yes |
| `test.fault.max_duration_seconds` | int | `60` | install | operator | no |
| `test.fuzz.pr_iterations` | int | `2000` per target | project | operator | no |
| `test.fuzz.nightly_iterations` | int | `200000` per target | project | operator | no |
| `test.fuzz.max_input_bytes` | int | `1048576` | project | operator | no |
| `test.load.max_virtual_users` | int | `500` | install | operator | no |
| `test.load.error_rate_fail_pct` | number | `1.0` (a run above it reports `Failed`) | project | operator | no |
| `test.corpus.root` | path | unset (no local corpus is configured) | machine (not install-wide) | operator | no |
| `test.walker.warn_on_unknown_widget` | bool | `true` | project | operator | no |

Not settings (invariants, per the configurability rule): real corpora are never read in CI (section 9); a runner never resets a database that `PgTestDatabase` did not create; faults always disarm on scope disposal; `DataLossAllowed` is always `false`.

Repo-level constants (hygiene limits) are not server settings, because they gate the repository and not an install. They live in `docs/hygiene-limits.json` (PROPOSED path), each with a reason field, read by the Hygiene CLI. Section 11.4 lists the PROPOSED numbers. Tightening a limit is a normal PR; loosening one needs the owner's approval line in the PR (a hygiene check fails a PR that raises a limit unless the body carries `Owner approval:` with a date).

### 6.3 Data tables

Migration namespace: `src/Bazaar.Server.TestOrchestration/Data/Sql/test-orchestration/NNNN_name.sql` (BAZAAR-COMPONENTS section 5). Numbers are local. Retention answers Resibo's open question ("long-running test result storage"): results live in Postgres, not in memory.

```sql
-- 0001_create_test_run.sql
CREATE TABLE test_run (
    run_id        uuid PRIMARY KEY,
    runner_id     text        NOT NULL,
    target_module text        NOT NULL,
    requested_by  text        NOT NULL,                 -- Principal canonical text, 'agent:charles'
    state         text        NOT NULL CHECK (state IN ('queued','running','passed','failed','crashed','timed_out','refused','cancelled')),
    spec          jsonb       NOT NULL,                 -- TestSpec without any corpus path (only the opaque CorpusRef id)
    seed          bigint      NOT NULL,
    started_at    timestamptz NOT NULL,
    finished_at   timestamptz,
    warnings      text[]      NOT NULL DEFAULT '{}',
    boundaries    text[]      NOT NULL DEFAULT '{}'
);
CREATE INDEX test_run_started_idx ON test_run (started_at DESC);

-- 0002_create_test_case_result.sql
CREATE TABLE test_case_result (
    run_id   uuid NOT NULL REFERENCES test_run(run_id) ON DELETE CASCADE,
    case_id  text NOT NULL,
    outcome  text NOT NULL,
    wall_ms  integer, cpu_ms integer, peak_ws_bytes bigint,
    metrics  jsonb  NOT NULL DEFAULT '{}',
    reason   text,
    PRIMARY KEY (run_id, case_id)
);

-- 0003_create_bench_series.sql
CREATE TABLE bench_series (
    workload_id text        NOT NULL,
    case_id     text        NOT NULL,
    boundaries  text        NOT NULL,                  -- sorted join; labelled and unlabelled series never mix
    host_class  text        NOT NULL,                  -- machine fingerprint class, not a hostname
    sdk_version text        NOT NULL,
    commit_sha  text        NOT NULL,
    measured_at timestamptz NOT NULL,
    wall_ms     double precision NOT NULL,
    p95_ms      double precision,
    PRIMARY KEY (workload_id, case_id, boundaries, host_class, measured_at)
);
```

No table stores corpus content, only the opaque `CorpusRef` id and aggregate numbers. `test_run` rows are not part of the GC log and are pruned by retention; the audit rows are not pruned.

### 6.4 Audit events

Written through the existing `IAuditLog` (`AuditEntry(Ts, Actor, Kind, Payload)`); `Actor` is the canonical `Principal` text (for example `agent:charles`, `user:dom`, `service:test-orchestration`, `system:setup`).

| Kind | Actor | Payload (JSON) |
|---|---|---|
| `test.run.requested` | requester | `runId`, `runnerId`, `targetModule`, `spec` hash, `taint` state |
| `test.run.finished` | `service:test-orchestration` | `runId`, `state`, `cases` count, `duration` |
| `test.run.refused` | requester | `runId`, `reason` (`double-in-measured-graph`, `not-a-test-install`, `corpus-in-ci`, `approval-missing`, `runner-unavailable`) |
| `test.fault.armed` / `test.fault.disarmed` | requester | `faultId`, `target`, seed, plan |
| `test.env.reset` | requester | database name, row counts before and after, approval id |
| `test.corpus.used` | requester | `corpusId` (opaque hash), runner, aggregate result; never a path or a content hash of a single file |
| `test.hygiene.limit_changed` | the PR author's `Principal` | limit name, old, new, owner approval reference (written by the CI job that merges, OPEN) |

### 6.5 Kodo health contract for the module

`Bazaar.Server.TestOrchestration` implements `IModuleHealth` (doc 46 section 10) and inherits `ModuleHealthContractTests<TModule>`. The kit libraries are build-time only and exempt; an architecture rule keeps them out of the host (section 5). Confirming this exemption with Kodo's loader is OPEN.

- Module id `test-orchestration`; hard dependencies: `audit`, `settings`; soft: `kodo`, `agents`.
- `Liveness`: no I/O.
- `Readiness`: `Pass` when disabled by setting (nothing to serve), otherwise `Warn` if no runner reports available, `Fail` if the run table is unreachable.
- `Dependencies`: as above.
- `SelfTestAsync(Quick)`: guard checks only (the corpus guard refuses a synthetic CI environment; the reset guard refuses a database not created by `PgTestDatabase`). `Full`: runs a no-op bench case and a crash-isolation run against a throwaway child process the self-test starts itself, inside the sandbox scope, and reports `LeftNoResidue`. It never targets a real module.
- Probes: `test_orchestration.stuck_runs` (a run `Running` past its timeout plus grace), `test_orchestration.runner_availability`, `test_orchestration.guard_selftest`.
- A mutation probe (doc 46 10.3 item 7) breaks each guard once and requires the self-test to fail.

### 6.6 Client half

`Bazaar.Client.TestOrchestration` (optional, last work package) ships one Rubedo view, `bazaar.tests.runs`, from the 13-widget set (41c section 3): a `list` of runs with a `badge` per state, a `tabs` node for cases and warnings, and an `invoke` action for a read-only "re-run". It is a view document with no code. Headless test per head. Keyboard-first and mnemonic rules apply; its mnemonic is PROPOSED in the pane registry, not here.

### 6.7 Optional manifest field (OPEN)

`testing: { faultScenarios: [...], benchWorkloads: [...], fuzzTargets: [...] }` in the module manifest, so Kodo and the walker can list what a module ships. Ownership of the manifest is doc 42 and BAZAAR-COMPONENTS; this is a proposal to them, not a change.

## 7. Real data: opt-in local corpora, never CI, never git

Resibo's rule (`docs/PRE_STABLE_BENCH_VALIDATION.md`, as described in `v3-deepdive-56`): the bench uses real receipt scans, supplied locally, because synthetic images hide real behavior; the bench runner flags directories that look generated. Bazaar's equivalent keeps the benefit and removes the risk. CLAUDE.md already forbids real GC transcripts, Soul data, secrets and private-company material in the public repo, and doc 34 keeps real transcripts in the private repo.

1. **Opt-in, local, outside the repo.** A corpus is a directory the operator names in `test.corpus.root` (machine scope) or in the `BAZAAR_TEST_CORPUS` environment variable. The directory must be outside any git working tree, or inside one but ignored by it. A path under the repo checkout that is not ignored is refused.
2. **Never in CI.** A corpus-backed case checks `CI`, `GITHUB_ACTIONS` and `RUNNER_NAME` and refuses (`corpus-in-ci`, outcome `Refused`, never `Skipped`). This holds on hosted and self-hosted runners alike, because doc 34 Question 1 puts the self-hosted runner on the owner's PC, which is also where local corpora would sit. This is an invariant, not a setting.
3. **Aggregate output only.** A corpus run's result holds counts, timings and pass/fail per case id. It never holds file contents, file names derived from content, or a path. The corpus id is an opaque hash of the root path and a random per-machine salt, so a result cannot be reversed to a file.
4. **Findings name rules, not values.** The corpus guard follows `LiveEvalCredentialGuard`'s shape: `CorpusFinding(Rule, Where)` with no matched text.
5. **Synthetic-looking corpus warning.** Resibo's heuristic (real camera metadata and file-size variance against a uniform generated signature) is domain-specific. Bazaar's version is a warning, never a block, as in Resibo (a false positive must not block real work): a corpus of transcripts or documents whose files are all the same size, all share one timestamp to the second, or all carry the sanitizer's placeholder names is flagged `corpus-looks-synthetic` in `TestResult.Warnings`. Calibration against real and synthetic corpora is OPEN (Resibo records the same open item).
6. **Repo check.** The hygiene gate "no real data" (section 11.7) fails a PR that adds a path matching the corpus ignore patterns, a file the sanitizer would change (so every committed fixture is a fixed point of `ReplaySanitizer`), or any key-shaped string (reusing the credential guard's rules). The existing secret scan job stays; this adds the data-shaped rules.
7. **Tests committed to the repo use synthetic data only**: fictional names, RFC 2606 domains (doc 34), generated by seeded builders.

## 8. Run semantics

### 8.1 Isolation and ordering

Runs are queued per runner; at most `1` bench run at a time per host (timing is meaningless under contention, and the runner records host load before each case); fuzz and walker runs may overlap; a load run excludes all others on the host. These are code constants, not settings.

### 8.2 Process isolation details

Child processes start with a minimal environment (no secrets, no gateway keys), a working directory under a per-run temp folder, and a kill-on-parent-exit job (Windows job object, or the process group on Linux; both behind a provider seam so the lib stays OS-agnostic). The first supported host is Windows.

### 8.3 Boundaries

Every result lists its `Boundaries`. Any list other than empty is shown wherever a number is shown.

### 8.4 env-reset

Resibo's runner wipes a designated test tenant and has a hard, checked guard against a production tenant. Bazaar has no tenants, so the guard is by origin: the runner resets only a database that `PgTestDatabase` (or a `PgTemplate` clone) created in this process family, checked by the creation record the fixture keeps and by the `<prefix>_<guid>` name shape, and never a database whose name matches the install's configured production database. Vault test repositories are reset only under a path the fixture created. Tests (from Resibo 8): reset against database A leaves database B untouched, and an attempt against the production database name is refused and audited.

### 8.5 Retention and trend

`bench_series` keeps all points; `test_run` and `test_case_result` are pruned after `test.orchestration.result_retention_days`.

### 8.6 What is deliberately different from Resibo

Resibo's runners are Python and gRPC, and its bench never uses any double. Bazaar's are .NET 11, over HTTP and `/ws`, and its bench allows labelled boundaries (section 5.1 rule 2). Resibo's `TEST_EXECUTION` category is kept as a category name only.

## 9. Security and privacy

- Test runs can kill processes and reset databases, so `test.orchestration.enabled` is off on an install that holds real company data unless the owner turns it on, and `run` mode needs approval (section 6.2).
- Agent text carries no owner authority (doc 15): an agent message saying "the owner approved the reset" grants nothing.
- Child processes get no secrets; stderr kept in a result is scrubbed (doc 23) and capped.
- Faults and runners refuse when the target is not a test target (section 8.4, `SandboxOnly`), and refusals are audited.
- Results never hold corpus content (section 7). Run specs are stored without paths.
- Fuzz crash cases are synthetic by construction and are reviewed before commit like any fixture.
- No new network egress. No new third-party dependency in any shared lib; providers carry theirs, subject to the licence check of doc 47 and the SBOM.
- A corpus run on the owner's PC could leak through logs. The hygiene CLI redacts findings to rule names (section 7 item 4), and CI log output of the kit never prints an input byte.

## 10. Doubles must conform (`Bazaar.Testing.Conformance`)

Rule (Resibo's "provider checks" idea and doc 34's "fake contract" test, generalised): a fake, in-memory or scripted double of an abstraction must pass the same contract tests as the real implementation, or its tests prove nothing.

- **Contract test base.** For each abstraction `IFoo` there is an abstract class `FooContractTests` in the module's test project (a provider-side base lives in `Bazaar.Testing.Xunit`). It has one abstract `CreateSubject()`. The real implementation and every double each derive once and implement `CreateSubject()`.
- **Discovery.** `IDoubleRegistry` finds doubles by convention and by attribute: a type named `Fake*`, `InMemory*`, `Scripted*`, `Stub*` or `Memory*`, or marked `[Double]`, that implements an interface from an `*.Abstractions` assembly. Existing examples this rule would catch: `InMemoryOwnerNonceStore` (Bazaar.Approvals, against `PostgresOwnerNonceStore`), `MemoryProfileCache` (against `FileProfileCache`), `FakePostureSource` (against `WindowManagerPostureSource`), the scripted gateway of doc 34.
- **Gate.** The `doubles-conform` check fails if a discovered double has no derived contract-test class in the same solution, unless it carries `[ConformanceExempt("<reason and doc section>")]`. The exemption list is printed in the CI summary. Existing doubles enter a baseline file (`conformance-baseline.txt`, ratchet: it only shrinks), so nothing breaks on day one.
- **Doc 34's recorded-shape check** (one recorded real provider response compared in shape with the scripted one) is registered as the contract test for provider adapters; it is not replaced.
- **Bench link.** A double that is not conformant is, by definition, unfit for a measured graph; Bench's `Refused` rule (5.1 rule 1) uses the same registry.

## 11. Repository hygiene

### 11.1 Mechanism

Every rule is a gate in `Bazaar.Testing.Hygiene`, run by `Bazaar.Testing.Hygiene.Cli` in CI and locally, and each follows the architecture-test pattern:

- A **baseline ratchet** file per rule (`<rule>-baseline.txt` next to `architecture-baseline.txt`): existing violations are listed, a new one fails, a listed one that no longer exists must be removed, so the list only shrinks.
- A **seeded-violation test** per rule: a planted violation must be flagged, and a clean control must pass.
- **Always reports.** No gate is path-scoped and none passes vacuously. Each prints how many items it inspected (`inspected 412 files, 0 new violations`), and a gate that inspected zero items when it should have inspected some fails with `nothing inspected`. This applies Resibo's lesson in `docs/MAINTENANCE.md` section 7.1: a path-scoped required check that never reports leaves PRs pending forever, and a check that no-ops with a warning "reads as enforcement while verifying nothing". Bazaar's own `docs/pr-flow.md` records the same pending-forever failure with CodeQL.
- **Stable names.** The CI job is named `Repo hygiene` (PROPOSED); once required, it is never renamed (`docs/pr-flow.md`).

### 11.2 How this differs from the doc 34 verification gate

The verification gate checks an agent's work product in a clean clone (build, test count, skips, test edits). Hygiene checks the shape of the change and the repository (PR body, file size, naming, structure). They run in different places and both are required for agent PRs; neither replaces the other.

### 11.3 Per-category PR templates with a parsed checklist

Taken from Resibo `docs/templates/*.md`, `.github/PULL_REQUEST_TEMPLATE/`, `docs/PHASE_1_COMPLETE.md` (checklist enforcement) and `docs/MAINTENANCE.md` section 7.

Template files live under `.github/PULL_REQUEST_TEMPLATE/<category>.md` and each starts with the common block of today's `.github/pull_request_template.md` (summary, docs section, linked design doc, tests, docs updated, DCO, sponsor). The existing file stays as the default and as the `general` category. Because agents open PRs through the API, the category is a marker line in the body: `<!-- bazaar-pr-category: new-provider -->`. The checker uses the marker to find the template.

Categories (PROPOSED; Resibo's eight mapped to Bazaar's structure):

| Category | Resibo template it comes from | Bazaar-specific items (beyond the common block) |
|---|---|---|
| `new-module` | `new_core_api.md`, `new_sub_api.md` | Server and client are two modules (BAZAAR-COMPONENTS 2.1); `.Abstractions` project; manifest with `health` block and Kodo contract tests inherited; migration namespace; no code in Bazaar.Server or Bazaar.Client.Core; architecture tests updated; threshold for "own module" explained |
| `new-provider` | `new_provider.md` | Implements the existing contract with no parallel interface; is a plain module (no selector); degrades to "unavailable" without crashing the host; the third-party dependency stays in this provider and not in a shared lib; contract tests (section 10) derived |
| `new-dependency` | `new_dependency.md` | Licence check (doc 47) and SBOM; row added to the `docs/FORWARD-FACING.md` table; day-0 and dual-version note; if the dependency parses untrusted bytes, a fuzz target and a rerun note |
| `wire-contract` | `new_grpc_endpoint.md` | Type added to `Bazaar.Contracts`; JSON round trip through `IWireCodec`; compatibility with an older client; GC append-only and edit-version effects stated |
| `new-setting` | `new_config_key.md` | Registry row with type, scope, safe default, who may change, approval; tighten-only rule; audit event; not an invariant |
| `ui-declaration` | `new_menu_item.md` | Rubedo view or command, not C# code, unless the exception is justified (41c); walker passes; labels, i18n keys, mnemonic; Headless test |
| `schema-migration` | `new_taxonomy_type.md` (closest), plus Bazaar's migration rules | Per-component migration namespace; forward-only; real PostgreSQL test (doc 34 L2); no table in `common/` |
| `docs-only` and `tooling` | none | The docs-section line may be `none (no design change)` only here |

Parser rules (`check_pr_checklist`, rewritten in C# in the Hygiene lib):

1. Every checklist item `- [ ]` must be `- [x]`. An unchecked box fails, and an item that does not apply is ticked with a reason (Resibo: "check it and say why").
2. **Substance floor.** Items tagged `[why]` in the template (Forward-compatibility, Security or taint effect, Backward effects, Not-applicable ticks, Docs-first, and "Pins a non-finding") need author-written text after the template's own wording. The floor is `25` alphanumeric characters, taken from Resibo's 25-character floor (`docs/PHASE_1_COMPLETE.md`). PROPOSED, with the number as a constant in `docs/hygiene-limits.json`. The template wording is discounted by reading the live templates, as Resibo's checker does after the bug recorded in `docs/PHASE_1_COMPLETE.md` where a template's own long prompt satisfied the length check.
3. Two further substance checks Resibo lacks: the same justification text appearing in three or more items fails (`copy-paste`), and a justification that is the item's own prompt reworded by a known filler list (`n/a`, `not applicable`, `none`, `ok`, `done`, `see above`) fails whatever its length.
4. **Missing checklist fails.** Bazaar's checker fails a PR with no checklist, except one labelled `docs-only` or `tooling` whose diff supports it. A bypass-by-omission is the exact hole the rule exists to close. PROPOSED (Bazaar's design, not from Resibo).
5. Runs on `opened`, `edited`, `synchronize`, `reopened` and `ready_for_review`. On a draft it reports annotations and passes (same pattern as the sign-off check in `docs/pr-flow.md`: drafts are not merged, so they are not blocked); on a ready PR it fails. The job runs with no secrets and reads only the PR body, the templates and the base-branch tree.

### 11.4 The docs-section rule and the checklist, as one check

CLAUDE.md requires a line `Docs section this implements: <doc path and section>` in every PR body. Resibo's checklist verifies a human ticked boxes. Together they become one `docs-first` step with structural verification, not just presence:

1. The line appears exactly once and has the exact label.
2. The path resolves to an existing file under `docs/`. For a code PR it must exist on the **base** branch, not only on the head, which makes "docs first, code second" mechanical: a code PR that cites a doc added in the same PR fails (the docs land as their own PR first, per CLAUDE.md).
3. The section part (`section 5`, `§5`, `5.2`, or a heading text) must match a heading in that file. A stale or invented section fails.
4. `none (no design change)` is accepted only in `docs-only` and `tooling` PRs, and the diff must contain no change under `src/` other than tooling paths listed in the limits file.
5. Status warning (not failure, mode `hygiene.pr.status_gate = warn`, PROPOSED): if the cited section carries `PROPOSED` or `OPEN` tags and the body has no `Owner approval:` reference, the checker annotates "do not build on PROPOSED without asking" (CLAUDE.md). Whether to make this a failure is OPEN.
6. The item "Decisions relied on (status tags)" in the common block is parsed: every listed tag must be one of the five statuses.

### 11.5 File size: soft guideline, CI hard ceiling

Taken from Resibo `docs/PRINCIPLES.md` section 1.1 and 3.1 and its CLAUDE.md ("soft target about 300 to 400 lines per file, CI-enforced hard ceiling"), whose reason is that long files degrade a model-assisted session's output, which is also Bazaar's development model (CLAUDE.md: Haiku and Sonnet sub-agents).

PROPOSED numbers (constants in `docs/hygiene-limits.json`, each with a reason; the owner confirms or changes them):

| Kind | Soft (warn) | Hard ceiling (fail) | Note |
|---|---|---|---|
| `.cs` (non-test) | `400` lines | `800` lines | Resibo's soft range is 300 to 400; the ceiling is not stated in the sources I read, so `800` is Bazaar's proposal |
| `.cs` (tests) | `600` | `1200` | Tests carry setup and data tables |
| `.axaml` | `300` | `600` | Views are declared, and Rubedo is meant to replace large hand-written views |
| `.sql` migration | `300` | `600` | One concern per migration |
| `.md` design docs, `.json` data, generated files (`*.g.cs`, files with `<auto-generated>`), lock files | exempt | exempt | Design docs are long by nature |

Lines are physical lines. The soft limit annotates; the hard ceiling fails. **Existing files over the ceiling enter `file-size-baseline.txt`** (ratchet, shrinks only), so the gate is adoptable without a mass refactor. Waiving a file needs a `[FileSizeWaiver("reason, doc section")]` comment header and counts as a loosening (owner approval). A PR may not grow a file that is already over the ceiling. A commit cap (Resibo: 99 commits per PR, tied to its `pp` version segment) is not adopted, because Bazaar's `0.MM.PP` display has no padding (CLAUDE.md, versioning) and "small PRs" is already a checklist item; a changed-lines advisory (warn above `1500` changed lines) is PROPOSED instead.

### 11.6 One concern per file; tests named for guarantees; pinned non-findings

**One concern per file.** Mechanical form: at most one public top-level type per file, and the file is named for it; nested and private helper types may share it. Exempt: generated code, `AssemblyMarker`-style files, and a file holding one type plus its small companion `enum` or `record` declared as `[Companion]`. Existing examples that would be listed in the baseline: files that bundle several top-level records, such as the `SkippedCommand` and `CommandLoadResult` records declared in `CommandRegistry.cs` (observed by grep; the baseline generator decides what is actually listed). Resibo's wording is "one file per distinct concern" (`PRINCIPLES.md` 1.1); the public-type rule is Bazaar's checkable approximation, and the rule is not about moving logic.

**Tests named for guarantees.** A test name states the guarantee, not the method: `Subject_does_or_refuses_X_when_Y`, as the existing tests already do (`Seeded_windows_registry_reference_is_flagged`, `Repo_root_is_found_from_nested_build_output`). The gate fails a new test method whose name has fewer than 3 words, or matches a denylist (`Test`, `Test1`, `Works`, `Basic`, `HappyPath`, `ShouldWork`, `Smoke`), or ends in a bare method name. The optional `[Guarantee("doc 15 section X")]` attribute links a test to the design sentence it guards; the Hygiene CLI can then print a table of design sentences with no guarding test (a report, never a gate). Test names also state refusals, because "refuses" tests are the ones that prove an invariant.

**Pin non-findings.** When a review, an audit or an investigation concludes "this is not a problem" or "nothing of that kind exists", that conclusion is pinned by a test, so the finding cannot silently reverse. Example already in the repo: `docs/FORWARD-FACING.md` records that the repo has no synchronous `Open()` calls on connections, which matters for Npgsql 11 (grep of `src` and `tests`); that statement becomes an architecture test `No_synchronous_connection_open_exists`. A test that pins one carries `[PinsNonFinding("source reference")]`, and the gate checks that the source reference (a doc path and section, or a PR number) exists. The PR checklist item (tag `[why]`) reads: "If this change or its review concluded that something is not a problem, a test pins it".

### 11.7 No real data in the repo

Described in section 7 item 6. In short: ignored corpus paths, sanitizer fixed-point check on fixtures, key-shape scan, and a path rule that forbids any file under `tests/**/Corpus*` outside an explicit synthetic-fixture folder.

### 11.8 Module guide file

Taken from Resibo `docs/CLAUDE_MD_GUIDE.md`: each API folder has a short `CLAUDE.md` naming what it owns, what it does not (the most valuable part, "what looks like it should live here but doesn't"), a forward-compatibility line (required even when the answer is "not applicable"), and real gotchas. It never holds open questions (they stay in the design doc), the full data model (the contracts project is the source), decisions history, or a copy of a global rule.

Adopted as an optional-then-required `CLAUDE.md` beside each module's `.csproj`, with those four headings. The filename is OPEN (worktree sub-agents do not load the root CLAUDE.md; whether a nested `CLAUDE.md` is loaded for them is unverified, so the agent definitions in `.claude/agents/` still repeat the essential rules). The gate checks the headings, rejects an "Open questions" heading, and fails a PR that changes a module's public contract without touching its guide (heuristic: `*.Abstractions` files changed, guide unchanged, `[why]` item unticked). Baselined for existing modules. Resibo notes its deep-dive corpus is temporary and `CLAUDE.md` files survive; Bazaar's design docs are permanent, so the guide is a short pointer plus the does-not-own list, not a replacement for the doc.

### 11.9 Review comment format

Taken from Resibo `docs/REVIEW_COMMENT_FORMAT.md`: reviews of design docs are written in a separate file per reviewer, never by editing the reviewed doc; each comment sits under `## FILE: <exact filename>` and `### §<section>` (or `(general, no specific section)`) with one tag first: `[Q]`, `[CORRECTION]`, `[FLAG]`, `[IDEA]`, `[OK]`. The format makes comments machine-matchable to the exact document and section. Bazaar already collects owner answers on cards; this is a file format for bulk reviews by the owner, a human sponsor or an agent. The tags are unrelated to the doc status tags (`DECIDED`, `PROPOSED`, and so on) and the parser keeps them in a separate namespace. `Bazaar.Testing.Hygiene` ships `ReviewCommentParser` (path resolve, section resolve, tag validation, unmatched comments reported). Location (`docs/reviews/REVIEW_<name>.md`) is OPEN. An `[OK]` is kept as an explicit record that a section was read and accepted; it does not make anything DECIDED, because only the owner decides (CLAUDE.md), and a reviewer file by anyone else cannot set a status.

## 12. Forward-compatibility: Resibo's pattern merged with `docs/FORWARD-FACING.md`

Resibo's pattern (`docs/PRINCIPLES.md` section 3.3, `docs/MAINTENANCE.md` section 8, including `nox -s forward_compat` in section 8.1): (1) conditional dependency install by environment marker, (2) feature detection over version checks, (3) one centralized shim per capability, (4) graceful degradation to the existing safe behavior, (5) lazy imports for optional heavy dependencies; plus a `forward_compat` test marker run against two interpreters by a `nox` session that fails loudly when an interpreter is missing, and a manual interpreter override.

`docs/FORWARD-FACING.md` already carries the Bazaar equivalent at the dependency level (day-0 and dual-version rule, canary, Dependabot, Renovate, a dated audit table, an adoption rule). It has no code-level pattern. The merge:

| Resibo point | Bazaar form | Where it applies |
|---|---|---|
| Environment-marker install | MSBuild `Condition` on the package reference by target framework or a named feature flag property; a package that a newer runtime makes redundant is removed in one change with the pin bump | NuGet packages; the shared-framework note in `docs/FORWARD-FACING.md` |
| Feature detection over version checks | Probe the capability, never compare a version: `IsSupported`-style runtime feature properties, `Type.GetMethod` or an overload probe for an API shape, `pg_available_extensions` or a one-row catalog probe for a PostgreSQL feature, a Vulkan capability query for GPU features, an API-level capability check for Android | PostgreSQL 18 and 19, Valkey, Avalonia, Vulkan, Android API levels, NPU providers |
| One shim per capability | A `[CapabilityShim("pg.pg_plan_advice")]` class per capability, in the module that needs it, in its own file; it exposes `IsAvailable`, the new path and the fallback path; call sites never branch on a version | every module |
| Graceful degradation | Missing capability gives the safe existing behavior and an `Unavailable` status Kodo can read, never a crash (matches Resibo `new_provider.md` item 2) | every provider |
| Lazy import of optional dependency | `Lazy<T>` or deferred provider load for a heavy optional dependency (ONNX Runtime, native engines), loaded on first use | NPU, local inference, engines |
| `forward_compat` marker and two-interpreter session | `[ForwardCompat]` trait on tests that validate version-sensitive behavior; run in the normal suite on the pinned SDK, and again by the canary on the newest preview SDK; a dual-database lane runs the same tests on PostgreSQL 18 and 19 | `.github/workflows/canary.yml`, the PG lane |
| "Fail loudly if the interpreter is missing" | The dual-version lane fails when a declared second version is not present on the runner, instead of skipping silently (no silent skip: doc 34) | CI |

**Conflict, resolved by scope, not by choosing.** CLAUDE.md says: never pin to an older runtime or add code paths for one; target the newest .NET. Resibo's pattern, read naively, adds fallback paths for older Python. Bazaar's two rules apply to different things. For the .NET runtime itself there is no fallback path: the code targets `net11.0` and the canary tests the next preview. For dependencies and platforms whose versions Bazaar does not control (PostgreSQL 18 and 19, Valkey 9.x, Avalonia, Android API 35 to 37, Vulkan 1.4 hardware), `docs/FORWARD-FACING.md` already requires both versions at once with the newest features and a graceful fallback; the shim pattern is how that is written. If the owner wants the two to be separate modules for traceability, the shim registry (below) is its own module project and can be split without change.

**Checks** (Hygiene gates, baselined):

1. `forward-compat-scan`: flags version comparisons used for control flow outside a `[CapabilityShim]` file (`Environment.Version`, `RuntimeInformation.FrameworkDescription`, `OperatingSystem.IsAndroidVersionAtLeast` used as a feature test, `server_version_num` comparisons, `Version.Parse` on a dependency's reported version). A file that must compare a version (for a documented reason) is marked `[VersionCheckWaiver("reason")]`.
2. `shim-registry`: every shim declares a capability id, the probe, the fallback, and a `RemoveWhenMinimum` note; the CI summary lists shims whose removal condition is already true (so a raised minimum simplifies only the shim, Resibo's stated goal for a centralized shim).
3. `fc-applicability`: the module guide (11.8) must state its forward-compatibility line, "not applicable" included.
4. A PR item, tagged `[why]` and checked as one pass (Resibo: "Forward-Compatibility Hygiene checked as ONE pass, not split apart"): does this change add a dependency, shim or version-sensitive code, and was `docs/FORWARD-FACING.md` and the canary updated in the same change.

The mechanism text for dual-version testing stays in `docs/FORWARD-FACING.md`; the main session adds a pointer from it to this doc section (section 15).

## 13. Interaction with existing modules

- **Kodo (46):** Orchestration implements the health contract (6.5). FailureInjection offers primitives to chaos mode. Bench's `ResourceClass` is available to Kodo's baselines. Kodo contract tests are inherited by Orchestration, and the kit libraries are exempt (build-time).
- **Settings (03):** rows in 6.2 register through the existing registry; no new settings mechanism.
- **Audit (15, 48):** events in 6.4 via `IAuditLog`, `Principal` as actor.
- **Agents and harness (06, 17):** agent tool category and approval ladder; the verification gate of 34 and 17 is unchanged. The benchmark task set runner (17 WP13) is a Bench workload provider.
- **Client (27, 41, 41c):** the walker reads the live registries; the optional view is a Rubedo document.
- **Approvals and taint (15):** `run` mode and `env-reset` use the approval ladder; a hard-tainted session cannot start them without approval.
- **Postgres (04):** one migration namespace; tests on real PostgreSQL with the existing fixture.
- **CI and PR flow (`docs/ci.md`, `docs/pr-flow.md`):** adds one required job (`Repo hygiene`) that needs a ruleset change by the owner (Cowork session: repository settings are not changed by this doc or by agents). No change to existing required check names.
- **Telemetry (43):** the fuzz targets of 43 move to the shared module.
- **Backup and restore (33):** Resibo's disaster-recovery restore test is a real consumer of its test orchestration. Bazaar's weekly restore (46 17.2) stays Kodo's; a `restore` runner could be added later (OPEN, not proposed now).

## 14. Testing

xUnit v3, Avalonia.Headless for UI, real PostgreSQL where data is touched (doc 34). No test sleeps; time is `FakeClock`; randomness is `TestSeed`.

- Every gate has a seeded-violation test and a clean-control test (section 11.1). A test that proves a gate that matched nothing fails.
- Bench: a case that crashes the child (exit code, stack overflow simulated by a self-terminating case) is `Crashed` and the run continues; a hanging case is `TimedOut`; a double reachable in the graph is `Refused`; labelled and unlabelled samples never share a `bench_series` key.
- FailureInjection: each fault arms and disarms; disposal during an exception disarms; a `SandboxOnly` fault refuses outside a sandbox; a crash aimed at module A never changes module B (Resibo's wrong-process test); the same seed reproduces the same fault timing.
- InterfaceWalker: a planted dangling `openPane`, a Ctrl chord, a mnemonic collision, an unlabelled button, a missing token and an unreachable pane are each found; a clean app passes; a pane that throws is reported, not passed.
- Fuzz: a planted parser bug (unbounded recursion) is found within the PR iteration budget at a fixed seed; the minimizer shrinks it; a clean target passes.
- Load: histogram percentiles against a known distribution; a scenario with an injected error rate above `test.load.error_rate_fail_pct` reports `Failed`; coordinated omission test (a stalled server does not hide latency).
- Conformance: a planted fake that returns a different result than the real class fails its derived contract test; an undiscovered double fails the gate; an exempt double prints its reason.
- Hygiene: the PR checker passes a good body and rejects an unchecked box, an empty `[why]` answer, a template-wording-only answer, three copies of one answer, a missing checklist, a docs-section path that does not exist on base, a section that does not match a heading, and `none` in a code PR. A synthetic PR-body corpus with about 40 cases is shipped; each case's expected result is data, not code.
- Corpus guard: refuses with `CI=true`, with `GITHUB_ACTIONS=true`, and with a corpus path under the repo and not ignored; the finding never prints a path or content; the synthetic-looking warning appears for a uniform corpus and not for a varied one (calibration is OPEN).
- Orchestration module: health contract tests (inherited), mutation probe on each guard, approval required for `run`, agent text claiming approval grants nothing, audit written for refusals.
- Pins: `No_synchronous_connection_open_exists` is the first `[PinsNonFinding]` test.

CI mapping (additive to doc 34): PR (hosted and self-hosted per Question 1): hygiene gates, walker, conformance, in-process faults, short fuzz. Nightly (owner's PC, private): Bench with the trend store, Load, long fuzz, `SandboxOnly` faults. Corpus runs: local only, never in any CI.

## 15. Edits needed elsewhere (for the main session; not made here)

1. `README.md` and `docs/design/README.md` index: add 78.
2. `docs/design/DECISIONS-LOG.md`: record the 2026-11-19 owner direction as it applies to the testing kit and hygiene; propose entries for the module names and the PR check once the owner approves them.
3. Doc 34 "Used by" and WP12 note: Bench implements L10; and its Conflicts list should note the `Bazaar.Testing` third-party reference (section 4).
4. `docs/FORWARD-FACING.md`: add a pointer to section 12 here.
5. `docs/pr-flow.md` and `docs/ci.md`: add the `Repo hygiene` job and the category templates once the owner approves; the owner must add the required check (repository setting).
6. `.github/PULL_REQUEST_TEMPLATE/` and `docs/hygiene-limits.json`: created by WP14 and WP15.
7. Doc 46: list FailureInjection as an optional fault source for chaos mode (17.3).
8. `CLAUDE.md`: no change proposed; the `Docs section this implements` line is already there and section 11.4 only verifies it.

## 16. Open questions

1. **Size numbers.** Soft `400`, hard `800` (non-test C#) and the other rows of 11.5 are PROPOSED. Resibo's hard ceiling number was not found in the sources read. Owner to confirm.
2. **Substance floor.** `25` characters, adopted from Resibo; confirm or change.
3. **Postgres fixture split.** Move `Bazaar.Testing`'s Npgsql and Testcontainers use into a provider module (WP1b)? It changes an existing project.
4. **Existing benches.** Move `tests/Bazaar.Constellation.Bench` and `Vulkan.Bench` onto the Bench contract, or leave them? Proposed: move, as workload providers, in a later PR.
5. **Fuzz engine.** In-house seeded mutator only, or add a coverage-guided provider (candidate: SharpFuzz; unverified for .NET 11 and Windows).
6. **Agent tool seam.** How `run_test` and friends register with the harness (doc 06 `ToolDispatcher`, doc 17); exact names and category label.
7. **Kodo exemption** for build-time test projects, and whether the manifest `testing` block (6.7) is wanted.
8. **Module guide filename** (`CLAUDE.md` or another) and whether worktree agents load it.
9. **Status gate** in 11.4 item 5: warn or fail.
10. **Required check.** Owner adds `Repo hygiene` to the ruleset; draft behavior (annotate, pass) to confirm.
11. **Review file location** (`docs/reviews/`) and who may write one.
12. **Corpus heuristic calibration**, as in Resibo `v3-deepdive-56` section 9, and whether the opaque corpus id salt should be per machine or per install.
13. **Limit loosening approval** is checked by the PR body line `Owner approval:`; whether a CI job can verify it against the owner's review instead is OPEN.
14. **Scope of faults on Windows hosts** (job objects, quota-limited volumes) needs a spike on the owner's PC before WP5 is final.
15. **Proving Grounds and Dependencies Warden** (convergence B2, B3) have no Bazaar doc yet; this doc leaves a hook (runners, `IBenchWorkload`) and no more.

## 17. Work packages

Each is its own project or file group with tests, small, and independent where the order column allows. IDs are local to this doc.

| WP | Content | Depends on |
|---|---|---|
| 1 | `Bazaar.Testing.Abstractions`: outcomes, summaries, `CorpusRef`, boundary labels, `IDoubleRegistry` contract | none |
| 1b | (OPEN) split `Bazaar.Testing.Postgres` provider out of `Bazaar.Testing` | 1 |
| 2 | `Bazaar.Testing.Xunit` provider: attributes, traits, contract-test base classes | 1 |
| 3 | `Bazaar.Testing.Conformance` + `.Abstractions`: discovery, gate rule, baseline writer | 1, 2 |
| 4 | `Bazaar.Testing.Load` + `.Abstractions`: virtual users, histogram, report | 1 |
| 5 | `Bazaar.Testing.FailureInjection` + `.Abstractions`: scope, in-process faults | 1 |
| 5b | Process, disk and clock faults for a sandbox (Windows first) | 5 |
| 6 | `Bazaar.Testing.Bench` + `.Abstractions`: process-isolated runner, sample store contract, classification | 1, 3 |
| 6b | Concurrent-user phase (links Bench to Load) | 4, 6 |
| 7 | `Bazaar.Testing.Fuzz` + `.Abstractions`: mutators, minimizer, crash corpus | 1 |
| 7b | Fuzz targets for wire frames, MIME, Vault Markdown, tile frames, journal decoder, redactor (one file per target, in the owning module's test project) | 7 |
| 8 | `Bazaar.Testing.InterfaceWalker` + `.Abstractions`: sources, resolution, reachability, mnemonic, label rules | 1 |
| 9 | Walker sources for commands, panes, Rubedo views | 8 |
| 10 | `Bazaar.Testing.InterfaceWalker.Avalonia` provider; move `FrameCapture` into a shared project | 8 |
| 11 | `Bazaar.Testing.Hygiene` + `.Abstractions`, core: baseline ratchet, annotations, always-report rule | 1 |
| 12 | Hygiene rules: file size, one concern, test names, non-finding pins (with the first pinned test) | 11, 2 |
| 13 | Hygiene rules: forward-compat scan, shim registry, module guide check | 11 |
| 14 | PR checker: parser, templates under `.github/PULL_REQUEST_TEMPLATE/`, category marker, synthetic body corpus | 11 |
| 15 | Docs-section verification and `docs/hygiene-limits.json`; `Bazaar.Testing.Hygiene.Cli`; the `Repo hygiene` workflow (not required until the owner sets it) | 12, 13, 14 |
| 16 | No-real-data rules and corpus guard (`ReplaySanitizer` fixed point, key rules, ignore patterns, CI refusal) | 11, 1 |
| 17 | `ReviewCommentParser` | 11 |
| 18 | `Bazaar.Server.TestOrchestration` + `.Abstractions`: contracts, migrations, settings rows, audit, health contract | 1, 6, 5 |
| 19 | Orchestration runners as plain modules: bench, crash-isolation, pipeline, env-reset, fuzz, load, walker | 18, 4, 7, 8 |
| 20 | Agent tools and approval wiring | 18, 19 |
| 21 | `Bazaar.Client.TestOrchestration` Rubedo view and Headless test | 18, 8 |
| 22 | Move existing benches to workload providers | 6, owner answer to Q4 |

Hygiene gates (WP12 to 17) go in as report-only first, with baselines generated by the first run, then are made required one at a time, so no existing PR is blocked by a rule it could not have known.
