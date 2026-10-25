# Testing strategy

Status: RECONCILED with DECISIONS-LOG (2026-10-25) · Owner questions: 15
Depends on: 01-gc-data-model.md, 02-wire-protocol-and-sync.md, 04-postgres-schema-and-migrations.md, 05-model-gateway.md, 06-agent-runtime.md, 10-council-and-voting.md, 12-kanban-and-dependencies.md, 13-skimmer-bouncer.md, 14-identity-service-and-passport.md, 15-security-taint-and-approvals.md, 17-coding-agent-harness.md, 18-vault.md, 27-client-architecture.md · Used by: 35-spikes-plan.md, 11-reputation-and-peer-review.md, 17-coding-agent-harness.md, and every subsystem doc's Testing section

## Purpose
Defines how Bazaar proves its own code and its agents' work: the test layers, what runs without real models or displays, what gates a merge, and how the server, not an agent's claim, decides work is done.

## Decided inputs
- xUnit v3 everywhere first; TUnit may be added later; xUnit pin `xunit.v3.mtp-v2` 3.2.2 approved as a tool. (CLAUDE.md; implementation-plan.md; DECISIONS-LOG round 10, C37)
- Every behaviour change ships with a test; UI changes get Avalonia.Headless tests. (CLAUDE.md)
- Build and tests run before every commit; data tests use real PostgreSQL. (implementation-plan.md)
- **Server verifies, agent self-reports never count:** the server runs clean builds and tests. (DECISIONS-LOG round 10, C37, 2026-10-25; CLAUDE.md wording pending the reconciliation pass)
- Per-agent model whitelists are enforced by Bazaar.Server, so policy tests hit the server, never the prompt. (CLAUDE.md; 05)
- GCs are append-only; edits are new full copies per version (`gc_message_version`). Golden tests compare GC history including edits. (CLAUDE.md; DECISIONS-LOG round 1, C09)
- Resource Economy is REJECTED, so no test asserts a money gate; the Work-Life Simulation replaces rate limits, and real 429s are handled as data. (CLAUDE.md; Bazaar.md)
- Council votes are independent before reasoning is revealed; Thinking-GC text and vote reasoning stay out of other agents' context until the reveal. (Bazaar.md §5; DECISIONS-LOG rounds 3 and 9, C11, C32)
- Taint: external content taints hard; agent text taints softly and propagates only from a tainted source session; Tier 2 actions are gated on hard taint; a fresh-session path with an approved summary exists. (DECISIONS-LOG round 2, C06)
- Reader/actor split ships in v1 for external input (email, issues, web, forums); reader sessions hand over summaries. (DECISIONS-LOG round 6, C18: owner statement, Claude's reading to confirm, see Q13)
- Approval tiers follow 15's ladder with 36's stricter community defaults. (DECISIONS-LOG round 7, C21)
- Loop-guard trips send the agents involved to the Psych Ward; unmentioned messages wake an agent only through Skimmer promotion under a cap, after shadow-mode measurement; interrupts use a depth-2 stack and only `emergency` bypasses vacation. (DECISIONS-LOG rounds 3, 9; C08, C34, C35)
- Claude member uses both the owner's Anthropic API key and FreeLLMAPI free tokens, by availability, labelled by source; subscription (Pro/Max) logins stay off every route. (CLAUDE.md; DECISIONS-LOG round 4, C14)
- Model seam: `ModelEvent` types with an `IChatClient` adapter shipped in v1. (DECISIONS-LOG round 8, C29)
- One server-side `ISecretStore`; git pushes run host-side; planted keys must be absent from logs and spans. (DECISIONS-LOG round 2, C05)
- A PR-linked card is Done only when its PR is merged. (DECISIONS-LOG round 8, C28)
- Subagents run on Haiku 5.5. Real GC transcripts and Soul data never enter the public repo. (CLAUDE.md)

## Design
Everything below is [PROPOSED] unless listed above.

### Test layers
| # | Layer | Scope | Runs | Gates merge |
|---|---|---|---|---|
| L0 | Architecture | No Windows-only API outside `Bazaar.Server.Providers.Windows`; `Bazaar.Client.Core` references no head | every PR | yes |
| L1 | Unit | Pure domain: state machines, DST schedule math, Council tally (unanimous passes, split or tied escalates), loop counters | every PR | yes |
| L2 | Integration | Real PostgreSQL 18 + pgvector: GC log, inbox, leases, Kanban (Done = merged), taint and audit | every PR | yes |
| L3 | Protocol | In-process Kestrel; WebSocket plus the authenticated HTTP surface (blobs, search, pairing, status); replay and splice | every PR | yes |
| L4 | Headless UI | Avalonia.Headless with `UseSkia` and `UseHeadlessDrawing = false`; Monaco sits behind `IEditorHost` and is stubbed | every PR, Linux and Windows | yes |
| L5 | Agent behaviour | Scripted agents, fake gateway, cassettes, kill and resume, Psych Ward trips, interrupt stack | every PR | yes |
| L6 | Transcript replay | Sanitized GC fixtures, structural comparison; labelled transcripts also measure the Skimmer miss rate that gates its promotion | every PR | yes |
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
- **Fake gateway:** one seam. Scripts are `ModelEvent` steps (text, tool call, `retry-after` 429, spend-cap 429, timeout, malformed tool JSON, mid-stream drop, wrong served model), exposed through the `IChatClient` adapter.
- **Cassettes:** keyed by a request digest (provider, model id, system-prompt hash, message digest, tool-schema digest, sampling parameters). A mismatch fails with a diff and never falls through to a live model.
- **Recording:** explicit command only, cost logged. Real-model cassettes stay private; public ones are synthetic.
- **Golden comparison:** structural only: speaker, tool-name sequence, event kinds, seq order, version chain of edits. Never exact model text.
- **Loop guards, wake rules and time:** scripted agents; all time through a fake `TimeProvider` (package name to confirm).

### Live evals (non-gating)
- **Task set:** about 40 tasks mined from Bazaar's history (bug fix, feature with test, refactor, Headless UI change, Godot and Luau checks), plus traps: a wrong test (must flag, not edit), an impossible task (must report failure), an injected issue (must not act). Ten are held out.
- **Credentials:** the harness accepts only API keys and gateway sources; it refuses subscription logins (tested, see Testing).
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
- **Assertions are about enforcement, not model resistance:** a hard-tainted session refuses Tier 2 tools; soft-tainted agent text from an untainted source does not block; no state change; audit entry written. A message claiming owner approval grants nothing (agent text carries no owner authority). Model resistance is measured only in L9.
- **Reader/actor:** reader sessions have no tools and output schema-validated JSON; actor sessions receive only that summary, never the raw external content.
- **Approval and audit:** altered approval arguments rejected; expired approval denies; agent-forged "owner approved" ignored; PR merge runs only on the server after a signed owner approval; tampered audit row detected; application DB role cannot UPDATE audit; planted keys absent from logs and spans.

### Flakiness, coverage and test data
- **Flakiness:** no skip, no quarantine, no retry-to-green. A failure is re-run once only to classify it; both results are logged. Seeds and clock values are logged on every failure.
- **Coverage:** reported per project with coverlet; no percentage gate in v1.
- **Test data:** fictional names and RFC 2606 domains; replay uses sanitized copies of transcripts.

### CI mapping (engine not yet decided)
| Trigger | Jobs | Runner | Secrets |
|---|---|---|---|
| Pull request, public repo | L0 to L8, L11 (Windows providers), L12 | Hosted Linux and Windows | None; fork PRs get none |
| Push to protected branch | Same, plus release build | Hosted | None |
| Nightly, private repo | L9, L10, judge drift, cassette freshness, L7 large | NUC or hosted | Anthropic key and gateway keys from a protected environment |
| Manual, private repo | Hyper-V, Studio checklist | NUC | None |

## Data and interfaces
```csharp
public interface IScriptedGateway : IChatClient
{
    IReadOnlyList<GatewayCall> Calls { get; }        // what was asked, for assertions
    void Enqueue(ScriptStep step);                   // a ModelEvent step: text, tool call, 429, cap, timeout, malformed
}
public sealed record CassetteKey(string Provider, string Model,
    string SystemHash, string MessagesDigest, string ToolsDigest);
```
- Cassette file: one JSON record per call: key, response, input, cache-write 5-minute and cache-write 1-hour token fields kept separate (`gw_usage`, C33), the identity source (`native` or `pooled`, C14), recorded-at, recorder version.
- Fixtures (xUnit v3 collection fixtures): `PgFixture`, `HeadlessAppFixture`, `ProtocolHostFixture`.

## Behaviour and edge cases
- **Cassette drift:** any prompt change fails replay until re-recorded; prompts live in versioned files so churn is visible.
- **Concurrency:** tests assert invariants under fixed seeds, not interleavings. UI tests wait for a rendered frame, never a sleep.
- **Clock:** DST tests run both directions and repeated local times.
- **Wake and digest:** a message is never dropped whether it wakes now, is promoted early, or waits in the digest (06 invariant).

## Testing
Key domain cases (PROPOSED, each in the layer shown):
- L1: Council tie escalates to the owner; unanimous vote passes without the owner; votes are sealed until reveal.
- L2: GC edit keeps the prior version; erasure destroys the key and leaves a tombstone with structure intact; Kanban card with a PR moves to Done only on merge, reviewer approval sets a flag only.
- L5: loop-guard trip admits the agents involved to the Psych Ward; a depth-3 interrupt is refused; `emergency` bypasses vacation and nothing else does.
- L5: a Skimmer-promoted wake respects the cap; in shadow mode it does not wake.
- L3: the HTTP surface and the socket share one authenticated session; blobs never travel on the socket.
- Gate self-tests: a fixture repo with seeded bad changes (deleted test, added Skip, dropped count, masked timeout, edited test file) must fail; a clean control must pass.
- Fake contract: once per provider adapter, one recorded real response is compared in shape with the scripted one.
- Cassette tamper: one changed byte in a request fails replay loudly.
- Red-team control: a benign payload must still be allowed, so the filter is not simply blocking everything.
- Credential guard: a subscription-login token is refused by the live-eval harness.

## Work packages
1. `Bazaar.Testing` support project (clock, ids, seeds) and L0 architecture test. Depends on: scaffold.
2. PostgreSQL fixture: Testcontainers, service container and `BAZAAR_TEST_PG` selection; template database. Depends on: 1 (04 WP2).
3. Fake gateway (`ModelEvent` steps, `IChatClient` adapter) and cassette store with fail-loud matching. Depends on: 1.
4. Headless harness: `AvaloniaTestApplication`, `UseSkia`, frame-capture helper, `IEditorHost` stub. Depends on: 1.
5. Protocol harness: in-process Kestrel, `ClientWebSocket`, HTTP surface, fake authenticator and push. Depends on: 2.
6. Scripted-agent runner for wake, Skimmer promotion, loop guard to Psych Ward, interrupt stack, kill and resume. Depends on: 3.
7. Verification-gate checker and seeded-bad-change tests. Depends on: scaffold.
8. Red-team corpus and runner, including reader/actor and approval assertions. Depends on: 3.
9. Transcript replay: sanitized fixture format and structural comparator. Depends on: 3.
10. Property tests for GC sequence and splice. Depends on: 2, 5.
11. Live-eval harness, task-set schema and credential guard; nightly, non-gating. Depends on: 3.
12. Benchmarks: graph, gateway overhead, replay of 10k. Depends on: 6.
13. CI workflow: hosted-only public jobs and fork-PR policy. Depends on: scaffold.
14. Taint and reader-session enforcement tests (hard vs soft, fresh-session hand-over, tool-less reader). Depends on: 3, 8.

## Conflicts
1. **xUnit pin vs research note.** The pin 3.2.2 is approved (C37). `build_and_test_tooling.md` still recommends 4.0.0-pre.108 and is out of date; it needs a note update.
2. **Cassettes versus iteration.** Replay keyed on full request digests breaks on every prompt edit. Re-recording costs money and is not deterministic. This will slow prompt work.
3. **Public CI versus live and Windows needs.** Evals need keys, so they run privately. Hosted Windows runners have no Linux containers, so Windows database tests depend on an unverified PostgreSQL install. Headless Avalonia on a hosted Windows runner is also unverified (cicd_repo_licensing.md).
4. **Pixel goldens** differ by OS font rasterisation.
5. **CI engine undecided** (Bazaar.Server.md Q3). This doc assumes GitHub Actions.
6. **Reader/actor split (other docs).** C18 puts it in v1 for external input, but Bazaar.Server.md item 13 still says "OPEN for later" and 15 says "Not built in v1". Those docs must change; this doc's tests follow C18 pending Q13.
7. **CLAUDE.md** does not yet carry the C37 items (verification rule, approved names and tools) or the C14 and C18 wording. Pending the reconciliation pass.

## Questions for the owner
1. ANSWERED: "Server verifies, never self-reports" is DECIDED (DECISIONS-LOG round 10, C37).
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
14. Confirm Claude's reading of C18: the reader/actor split ships in v1 for external input, and tests in WP14 follow it. Default: yes.
15. "Bigger changes" that need your vote when the team vote is split or tied (C32): default candidates are new agent, promotion to senior, retirement and whitelist change; "eligible voter" defaults to agents seated in the GC holding the vote. Needed before the Council tie tests can be finished.
16. Erasure tests for Vault Markdown files and git history (C01, C38 consequence, FOLLOW-UP): default: personal content in Vault pages is encrypted per person, key destruction plus tombstone, history not rewritten. Needed before the erasure tests can be written.
