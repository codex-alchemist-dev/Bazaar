# Testing strategy

Status: DRAFT for owner review (2026-10-25) · Owner questions: 13
Depends on: 02, 04, 05, 06, 15, 17 (the subsystems each layer exercises) · Used by: 35-spikes-plan.md, 11-reputation-and-peer-review.md, 17-coding-agent-harness.md, and every subsystem doc's Testing section

## Purpose
Defines how Bazaar proves its own code and its agents' work: the test layers, what runs without real models or displays, what gates a merge, and how the server, not an agent's claim, decides work is done.

## Decided inputs
- xUnit v3 everywhere first; TUnit may be added later. (CLAUDE.md; implementation-plan.md)
- Every behaviour change ships with a test; UI changes get Avalonia.Headless tests. (CLAUDE.md)
- Build and tests run before every commit; data tests use real PostgreSQL. (implementation-plan.md)
- Per-agent model whitelists are enforced by Bazaar.Server, so policy tests hit the server, never the prompt. (CLAUDE.md; 05)
- GCs are append-only; edits keep every version. Golden tests compare GC history including edits. (CLAUDE.md; Bazaar.md)
- Resource Economy is REJECTED, so no test asserts a money gate; the Work-Life Simulation replaces rate limits, and real 429s are handled as data. (CLAUDE.md; Bazaar.md)
- Council votes are independent before reasoning is revealed (Bazaar.md). Taint tracking plus a human gate for high-impact actions (Bazaar.Server.md Q13).
- Subagents run on Haiku 5.5. Live evals use Anthropic API keys only, never subscription credentials. (CLAUDE.md)
- Real GC transcripts and Soul data never enter the public repo. (CLAUDE.md)
- Server verifies agent work with clean builds and tests; agent self-reports are data. (task brief; see Conflicts 2)

## Design
Everything below is [PROPOSED] unless listed above.

### Test layers
| # | Layer | Scope | Runs | Gates merge |
|---|---|---|---|---|
| L0 | Architecture | No Windows-only API or `SupportedOSPlatform` in `Bazaar.Server`; Client.Core references no head | every PR | yes |
| L1 | Unit | Pure domain: state machines, DST schedule math, Council tally, loop counters | every PR | yes |
| L2 | Integration | Real PostgreSQL 18 + pgvector: GC log, inbox, leases, Kanban, taint and audit | every PR | yes |
| L3 | Protocol | In-process Kestrel, `ClientWebSocket`; replay and splice | every PR | yes |
| L4 | Headless UI | Avalonia.Headless with `UseSkia` and `UseHeadlessDrawing = false` | every PR, Linux and Windows | yes |
| L5 | Agent behaviour | Scripted agents, fake gateway, cassettes, kill and resume | every PR | yes |
| L6 | Transcript replay | Sanitized GC fixtures, structural comparison | every PR | yes |
| L7 | Property | Random append and splice interleavings; seeds logged | small set per PR, large nightly | yes |
| L8 | Red team | Injection corpus through the fake gateway; asserts the server refuses | every PR | yes |
| L9 | Live evals | Haiku 5.5 on the Bazaar task set; LLM judges | nightly, private repo | no |
| L10 | Performance | Graph at 1k, 5k, 10k nodes; gateway overhead; replay of 10k entries | nightly, trend only | no |
| L11 | Platform | Windows Service, systemd, Hyper-V, Studio | hosted or NUC | where hosted |
| L12 | Engine | Godot headless, `luau-analyze` and Selene on fixtures | every PR, Linux | yes |

### Fixtures: real PostgreSQL
| Option | Used where | Note |
|---|---|---|
| Testcontainers | Dev machines with Docker; Linux runners | Primary. Needs a reachable Docker daemon |
| CI service container | GitHub Linux job, pgvector image | No Docker-in-test |
| `BAZAAR_TEST_PG` | Windows runners; machines without Docker | Points at a local PostgreSQL 18 installed by a setup step (unverified on hosted Windows) |
| Embedded or in-memory DB | Rejected | Cannot validate NOTIFY, pgvector or gap-free sequences |

One template database is migrated per run; each test class clones it (04). The cloud container's Docker daemon is unconfirmed; the first spike runs `docker info` and otherwise uses a service container or `BAZAAR_TEST_PG`.

### Deterministic agent testing
- **Fake gateway:** one chat-client seam, scripted with text, tool calls, `retry-after` 429, spend-cap 429, timeouts, malformed tool JSON, mid-stream drop and a wrong served model.
- **Cassettes:** keyed by a request digest (provider, model id, system-prompt hash, message digest, tool-schema digest, sampling parameters). A mismatch fails with a diff and never falls through to a live model.
- **Recording:** explicit command only, cost logged. Real-model cassettes stay private; public ones are synthetic.
- **Golden comparison:** structural only: speaker, tool-name sequence, event kinds, seq order. Never exact model text.
- **Loop guards, wake rules and time:** scripted agents; all time through a fake `TimeProvider` (package name to confirm).

### Live evals (non-gating)
- **Task set:** about 40 tasks mined from Bazaar's history (bug fix, feature with test, refactor, Headless UI change, Godot and Luau checks), plus traps: a wrong test (must flag, not edit), an impossible task (must report failure), an injected issue (must not act). Ten are held out.
- **Matrix and scoring:** per model and edit format, 3 seeds. Deterministic checks (build, tests, test files untouched, format compliance, fake success) score first; judges second.
- **Judges:** `.Quality` evaluators from Microsoft.Extensions.AI.Evaluation, with its response cache; not `.Safety` (Foundry dependency). Judge scores are reported only after calibration on about 50 human-labelled items.

### Verification gate (server-run)
The checker runs in a clean clone, not in the agent's working tree. It stores exit codes and logs, and fails when:

| Check | Fails when |
|---|---|
| Clean build | `build.sh` in a fresh clone (no `obj`/`bin`) fails, warnings as errors included |
| Test count | Head has fewer tests than base |
| Test edits | Test files changed without a reviewer flag |
| Skips | New `Skip` or `Explicit` attribute |
| Timeouts | A timed-out command is recorded as success |
| UI change | A UI file changed with no Headless test change |
| Behaviour change | Source changed with no test change (warning, not a gate) |

### Security tests
- **Red-team corpus (L8):** poisoned issue with hidden Unicode; email with link-exfiltration markdown; malicious rules file; GC message impersonating the owner; typosquat package; tool-description poisoning. The fake gateway emits the injected tool call.
- **Assertions are about enforcement, not model resistance:** session tainted; Tier 2 tools refused; no state change; audit entry written. Model resistance is measured only in L9.
- **Approval and audit:** altered approval arguments rejected; expired approval denies; agent-forged "owner approved" ignored; tampered audit row detected; application DB role cannot UPDATE audit; planted keys absent from logs and spans.

### Flakiness, coverage and test data
- **Flakiness:** no skip, no quarantine, no retry-to-green. A failure is re-run once only to classify it; both results are logged. Seeds and clock values are logged on every failure.
- **Coverage:** reported per project with coverlet; no percentage gate in v1.
- **Test data:** fictional names and RFC 2606 domains; replay uses sanitized copies of transcripts.

### CI mapping (engine not yet decided)
| Trigger | Jobs | Runner | Secrets |
|---|---|---|---|
| Pull request, public repo | L0 to L8, L11 (Windows providers), L12 | Hosted Linux and Windows | None; fork PRs get none |
| Push to protected branch | Same, plus release build | Hosted | None |
| Nightly, private repo | L9, L10, judge drift, cassette freshness, L7 large | NUC or hosted | API keys from a protected environment |
| Manual, private repo | Hyper-V, Studio checklist | NUC | None |

## Data and interfaces
```csharp
public interface IScriptedGateway : IChatClient
{
    IReadOnlyList<GatewayCall> Calls { get; }        // what was asked, for assertions
    void Enqueue(ScriptStep step);                   // text, tool call, 429, spend cap, timeout, malformed
}
public sealed record CassetteKey(string Provider, string Model,
    string SystemHash, string MessagesDigest, string ToolsDigest);
```
- Cassette file: one JSON record per call: key, response, the three input-token fields kept separate, recorded-at, recorder version.
- Fixtures (xUnit v3 collection fixtures): `PgFixture`, `HeadlessAppFixture`, `ProtocolHostFixture`.

## Behaviour and edge cases
- **Cassette drift:** any prompt change fails replay until re-recorded; prompts live in versioned files so churn is visible.
- **Concurrency:** tests assert invariants under fixed seeds, not interleavings. UI tests wait for a rendered frame, never a sleep.
- **Clock:** DST tests run both directions and repeated local times.

## Testing
- **Gate self-tests:** a fixture repo with seeded bad changes (deleted test, added Skip, dropped count, masked timeout, edited test file) must fail; a clean control must pass.
- **Fake contract:** once per provider adapter, one recorded real response is compared in shape with the scripted one.
- **Cassette tamper:** one changed byte in a request fails replay loudly.
- **Red-team control:** a benign payload must still be allowed, so the filter is not simply blocking everything.

## Work packages
1. `Bazaar.Testing` support project (clock, ids, seeds) and L0 architecture test. Depends on: scaffold.
2. PostgreSQL fixture: Testcontainers, service container and `BAZAAR_TEST_PG` selection; template database. Depends on: 1 (04 WP2).
3. Fake gateway and cassette store with fail-loud matching. Depends on: 1.
4. Headless harness: `AvaloniaTestApplication`, `UseSkia`, frame-capture helper. Depends on: 1.
5. Protocol harness: in-process Kestrel, `ClientWebSocket`, fake authenticator and push. Depends on: 2.
6. Scripted-agent runner for wake, loop guard, kill and resume. Depends on: 3.
7. Verification-gate checker and seeded-bad-change tests. Depends on: scaffold.
8. Red-team corpus and runner. Depends on: 3.
9. Transcript replay: sanitized fixture format and structural comparator. Depends on: 3.
10. Property tests for GC sequence and splice. Depends on: 2, 5.
11. Live-eval harness and task-set schema; nightly, non-gating. Depends on: 3.
12. Benchmarks: graph, gateway overhead, replay of 10k. Depends on: 6.
13. CI workflow: hosted-only public jobs and fork-PR policy. Depends on: scaffold.

## Conflicts
1. **xUnit pin.** The scaffold pins `xunit.v3.mtp-v2` at 3.2.2 because `Avalonia.Headless.XUnit` 12.1.4 fails discovery on 4.x. `build_and_test_tooling.md` recommends 4.0.0-pre.108; that note is out of date.
2. **Verification rule.** "The server verifies, never self-reports" appears in the task brief but not in CLAUDE.md, and `coding_agent_harness.md` treats it as an inference. Decide whether it becomes DECIDED.
3. **Cassettes versus iteration.** Replay keyed on full request digests (05, 06) breaks on every prompt edit. Re-recording costs money and is not deterministic. This will slow prompt work.
4. **Public CI versus live and Windows needs.** Evals need API keys, so they run privately. Hosted Windows runners have no Linux containers, so Windows database tests depend on an unverified PostgreSQL install. Headless Avalonia on a hosted Windows runner is also unverified (cicd_repo_licensing.md).
5. **Pixel goldens** differ by OS font rasterisation.
6. **CI engine undecided** (Bazaar.Server.md Q3; reports decision 10). This doc assumes GitHub Actions.
7. **Reader and actor split.** The threat model rates it P0; Bazaar.Server.md Q13 leaves it OPEN, so its tests wait.

## Questions for the owner
1. Adopt "server verifies, never self-reports" as DECIDED in CLAUDE.md? Default: yes; the gate depends on it.
2. CI engine: GitHub Actions, hosted runners only for the public repo? Default: yes; fork PRs can run code on self-hosted runners.
3. Windows job: install PostgreSQL 18, or database tests on Linux only? Default: Linux only, until a spike verifies the install.
4. Re-recording cassettes: explicit command only, cost logged, real-model cassettes private? Default: yes.
5. Pixel goldens: Linux container only, with structural assertions on Windows and Android? Default: yes, with a 0.5% pixel tolerance.
6. Coverage: report only in v1, revisit at Wave 2 exit? Default: yes.
7. Flakes: one classifying re-run, never a green from a re-run, no quarantine without your sign-off? Default: yes.
8. Judge model for live evals? Default: a non-Anthropic model chosen per install through the gateway, to avoid self-preference bias; no judge scores reported until calibrated on 50 labelled items.
9. Eval spend: nightly Haiku-only at 40 tasks, 3 seeds, all formats; Sonnet on demand only? Default: yes. This is development spend, not a gateway budget.
10. Task-set authorship: main session drafts from Bazaar's history, you approve, 10 tasks held out? Default: yes.
11. Performance: trend-only until spike numbers exist, then thresholds? Default: yes.
12. Red-team corpus: synthetic payloads public, real incidents referenced only? Default: yes.
13. Hyper-V and VM tests: manual or nightly on the NUC in the private repo? Default: nightly.
