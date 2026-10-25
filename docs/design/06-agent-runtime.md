# Agent Runtime

Status: DRAFT for owner review (2026-10-25) · Owner questions: 14
Depends on: 01-group-chats (GC log, @mentions, sequence numbers), 03-model-gateway, 04-soul-memory, 05-work-life (file names are placeholders; those docs are written in parallel) · Used by: Kanban, Council, Observatory, Skimmer Bouncer, Interrupt System docs

## Purpose
The runtime inside Bazaar.Server that runs persistent agents: it decides when an agent runs, builds what the model sees, runs the turn loop, executes tools on the agent's behalf, and survives crashes. It coordinates nothing itself. Coordination is what agents say in GCs; the runtime only delivers messages and enforces limits.

## Decided inputs
- No master orchestrator; GCs are the coordination system; events and software reactions serve GCs, never replace them (Bazaar.md §1, §2).
- GCs are append-only; edits keep every version (Bazaar.md §2). Email is external-only.
- Each agent owns private memory, shared project memory, working memory; no shared brain (Bazaar.md §5). Soul data lives outside the VM disk (Bazaar.Soul §2).
- Work-Life Simulation: per-agent timezone and schedule, async inbox, auto-coverage (Bazaar.md §4). Resource Economy REJECTED: limits are quotas plus the simulation, not money budgets.
- Council votes run independently first, reasons revealed together, at most one follow-up round, ties to the owner (Bazaar.md §5).
- Bazaar.Server's own gateway enforces per-agent model whitelists; no global `auto/*`; Claude lane uses API keys only (CLAUDE.md, Bazaar.Server §3).
- Taint tracking plus a human gate on high-impact actions after untrusted input (Bazaar.Server item 13).
- Every subagent and worker runs on Haiku 5.5 (CLAUDE.md model policy). This governs the build process; whether runtime subagents inherit it is Question 8.
- Postgres is the system of record; `BackgroundService` plus durable tables is the researched baseline; Orleans gets a one-agent spike (Bazaar.Server §4).
- Skimmer Bouncer is v1-planned but starts defer-only until its miss rate is measured (Bazaar.md §4).
- Owner (relayed in this assignment, date to confirm): agents may see each other's thinking.
- xUnit v3; every behaviour change has a test; fake gateway behind one chat interface.

## Design

### Lifecycle [PROPOSED]
| State | Meaning | Entered by |
|---|---|---|
| Created | Agent row, Soul reference, model whitelist, schedule, tool grants | owner or an authorised agent via admin API |
| Onboarding | Reads Constitution, role brief, project GCs; writes first memories; no tool grants beyond read | automatic after Created |
| Active | Eligible to wake | onboarding checklist passes (owner sign-off, Question 1) |
| Resting | Off-shift, vacation or quota-exhausted; inbox accumulates | work-life schedule or `RateLimited`/`SpendCapped` gateway state |
| Retired | Never wakes; Soul, memory, GC history kept read-only | owner decision |

### Wake model [PROPOSED]
Wake reasons, in priority order: **interrupt**, **@mention or DM**, **assignment** (Kanban card to the agent, review requested), **digest**, **scheduled** (shift start, reminder). Nothing else wakes an agent: a message without a mention is only stored. Each agent has a durable inbox row per pending item. A wake takes the highest-priority pending items and starts one turn.
- Digest: the runtime batches unmentioned GC activity from GCs the agent belongs to into one periodic digest item (interval per agent, default in Question 4). Nothing is hidden, only deferred.
- Interrupt: reserved for owner messages, build-broken, security events; preempts at the next tool boundary, not mid-model-call. Full Interrupt System design is a separate doc; this doc exposes `Interrupt(agentId, reason)`.
- Skimmer Bouncer, when built, is a pre-filter that may only move an item from "wake now" to "digest", never drop it.

### Turn loop
1. Claim: lease the agent (single active turn per agent, see concurrency), mark inbox items `claimed`.
2. Assemble context (below).
3. Call gateway (streaming). Persist each model response and tool result as a turn step before acting on it.
4. If tool calls: dispatch through the server (below), append results, repeat; stop on final message, step limit, token limit, or interrupt.
5. Post outputs: GC messages are appended through the normal GC API, so mention parsing and loop guards apply to agents and humans alike.
6. Commit: mark inbox items done, write memory proposals, release lease.

### Context assembly [PROPOSED]
Ordered, each section with a token budget set per agent and model (numbers in Question 5):
| Section | Source | Rule |
|---|---|---|
| System | role, Constitution excerpt, Soul personality, tool schemas | fixed, cache-friendly prefix |
| Memory | private + shared project memory (pgvector + recency) | top-k with provenance ids |
| Task state | Kanban card, blockers, PR status | current only |
| Vault | wiki-linked docs relevant to the task | by link and search, capped |
| GC backread | triggering thread in full; other GCs as summaries | recency + thread, never skip the trigger |
| Inbox items | what woke the agent | verbatim |
Principle from the research: smallest high-signal set, just-in-time reads through tools for the rest, compaction by clearing old tool results first. Context-builder output is stored (ids and hashes, not text) per turn so a turn is reproducible.

### Tool dispatch
The model emits tool calls; the runtime validates them against the agent's grants, injects credentials server-side, executes (in-process Bazaar tools, or calls into the agent's VM guest agent), and returns a bounded result. The model never sees secrets or host paths. Bazaar's own capabilities (post to GC, memory edit, Kanban, Vault) are one in-process tool surface, exposable as MCP. Tainted sessions route high-impact tools to the human gate. Every call is logged with arguments hash, exit code and duration. Edit-format choice per model is a gateway/tool setting, not runtime logic (research: unmeasured for Haiku 5.5).

### Subagents and delegation [PROPOSED]
A subagent is a child turn loop with fresh context: task message, tool allowlist (subset of the parent's), model from the parent's whitelist. It returns a summary plus a trace id; the full trace is stored and readable by the parent and reviewers (addresses "share full traces"). Limits: depth 2, 3 concurrent children per agent, step and token ceiling per child, children cannot @mention humans or open GCs. Delegation to a different persistent agent happens only by GC message, never by direct call.

### Loop guards [PROPOSED]
All counters are server-side, keyed by thread. On trip: stop waking, post a visible system notice in the GC, escalate to the owner.
| Guard | Default |
|---|---|
| Ping-pong: same two agents alternate N messages with no human or tool-result in between | 6 |
| Nested @mention chain depth (mention caused by a mention caused by a mention) | 4 |
| Per-agent wakes per hour | 30 |
| Per-thread agent messages without a human message | 20 |
| Identical tool call repeated | 3 |
| "Mandatory walk": after any guard trips, every participant must go through a forced pause (leave the thread, run a digest-only turn on the next shift) before re-entering | see Question 7 |
"Mandatory walk" is not defined in any source I found; the definition above is a guess.

### Thinking GCs [PROPOSED]
Each agent has a Thinking GC (append-only, like any GC) where its reasoning for a turn is posted as steps. Per the owner, agents may read each other's thinking. Safeguards: the vote exception (below), a `thinking` visibility flag so the owner can choose per GC, and thinking is never injected into the context of another agent by default, only on explicit read (Question 9).
Council exception: an agent's private vote reasoning is withheld from Thinking GCs until the tally is revealed, to keep the DECIDED independent-first rule.

### Concurrency
One active turn per agent (Postgres lease with expiry and heartbeat). A mention arriving mid-turn queues; interrupts set a cancel flag checked at tool boundaries. Global concurrency cap on model calls from the gateway's quota state, so rate limits pause agents via Resting rather than failing turns.

### Persistence and crash recovery
Each step is a row written before the next action. On restart the lease expires; the recovery job resumes from the last completed step: completed tool results are reused, an in-flight model call is re-issued, an in-flight tool call is re-issued only if marked idempotent, otherwise recorded as `unknown` and the model is told. Hard kills may skip `StopAsync` (Bazaar.Server §4), so nothing relies on shutdown hooks.

### Base choice: custom thin layer vs MAF vs Orleans [PROPOSED]
| Option | Fit | Risk |
|---|---|---|
| Plain `BackgroundService` + Postgres inbox/lease (`FOR UPDATE SKIP LOCKED`) | Lowest operational weight; matches DECIDED stack | We write leases, timers, recovery |
| Orleans (grain per agent, reminders) | Single activation maps to one-turn-per-agent; durable reminders for shifts | Unverified: Windows deployment, NUC footprint, version; second state store beside Postgres; Orleans streams unverified |
| Microsoft Agent Framework | Libraries (MCP client, checkpointing) | Orchestration patterns assume a speaker-selecting orchestrator, contrary to a DECIDED rule; cherry-pick only |
Recommendation: start plain, with the runtime behind `IAgentScheduler` so grains can replace it. Run the Orleans spike before Wave 2 closes. Decision criteria: (1) lease/recovery code size avoided; (2) idle memory with 20 agents on the NUC; (3) clean Windows service hosting and Linux parity; (4) no duplicate state outside Postgres; (5) cold-start latency of a wake; (6) licence and version verified on NuGet; (7) testability with a fake clock. Adopt Orleans only if it wins on 1 and 4 without losing 2 or 3.

## Data and interfaces
```sql
agent(id, name, soul_ref, state, model_policy_id, tz, schedule jsonb, created_at)
inbox_item(id, agent_id, kind, gc_id, seq, priority, state, due_at, claimed_by, claimed_at)
turn(id, agent_id, parent_turn_id, depth, started_at, ended_at, status, wake_reason, context_manifest jsonb)
turn_step(turn_id, n, kind /*model|tool|post|memory*/, request_hash, response jsonb, usage jsonb, idempotent bool)
agent_lease(agent_id, holder, expires_at)
guard_event(id, gc_id, kind, counters jsonb, created_at)
```
```csharp
interface IAgentScheduler { Task WakeAsync(AgentId id, WakeReason r, CancellationToken ct); void Interrupt(AgentId id, string reason); }
interface IContextAssembler { Task<ContextPlan> BuildAsync(Turn t, TokenBudget b, CancellationToken ct); }
interface IToolDispatcher { Task<ToolResult> InvokeAsync(Turn t, ToolCall c, CancellationToken ct); }
interface ILoopGuard { GuardVerdict Check(GcMessage m, ThreadStats s); }
// Model calls: IChatClient pipeline owned by 03-model-gateway; runtime never sees provider keys.
```
Contract with other subsystems: runtime calls GC `AppendMessage` and reads by `(gc_id, seq)`; calls gateway with `(agentId, modelRequest)` and receives usage plus `RateLimited(until)`/`SpendCapped` states; calls memory `Search`/`Propose` (writes validated server-side); emits `TurnStarted/TurnEnded/GuardTripped` events for the Observatory.

## Behaviour and edge cases
- Agreement spirals, duplicated work, conflicting edits, cost multiplication are the evidenced risks (Anthropic ~15x tokens for multi-agent; debate studies show more rounds hurt). Mitigations: low guard defaults, independent-first votes, task-claim comment on a Kanban card before edits, one writer per branch/path lease, cost visible per turn (display, not a gate).
- Quota exhaustion moves an agent to Resting with a reason; coverage rules belong to 05-work-life.
- Prompt injection: GC text from outside (email, issues) is marked untrusted in context and taints the turn.
- Edited messages: context uses the latest version and notes "edited"; the turn manifest records which version it saw.
- Offline clients do not affect agents; the server keeps running (DECIDED 48-hour rule).
- Cap on turn steps (default 40) and wall clock; on hit the agent posts a status and stops.

## Testing
Fake gateway (scripted, cassette replay keyed by request hash, injected 429/spend-cap/timeout/malformed tool calls), fake clock, injectable ids. Real Postgres for inbox, lease and recovery tests. Cases: mention wakes exactly one turn; unmentioned message only reaches digest; ping-pong trips at N and writes the notice; nesting depth 5 is refused; kill between steps resumes without repeating a completed tool; non-idempotent in-flight tool is marked unknown; two simultaneous mentions give one active turn; subagent depth 3 refused; secrets planted in memory/tool env never appear in context or logs; vote reasoning absent from Thinking GC before reveal; replay of a recorded turn reproduces the same step sequence. LLM-judge evals run nightly, non-gating.

## Work packages
1. Schema and repositories: agent, inbox_item, turn, turn_step, lease (needs Wave 1 WP2).
2. Inbox and wake service with priorities and digest batching (1).
3. Lease and crash-recovery job (1).
4. Context assembler with budgets and manifest (needs memory and GC read APIs; 1).
5. Turn loop and step persistence over `IChatClient` fake (1, 3).
6. Tool dispatcher with grants, taint gate, logging (5).
7. Loop guards and GC system notices (2).
8. Subagent runner with depth and tool limits (5, 6).
9. Thinking GC writer and visibility rules (5).
10. Record/replay harness and scenario tests (5).
11. Orleans spike behind `IAgentScheduler`, plus metrics report (2, 3).
12. Pilot metrics collector (below) (5, 7).

## Pilot metrics (research-recommended, measured before trusting any default)
Vote flips after reasons are revealed; loop guard trips per 100 threads; wake count and tokens per delivered outcome; duplicated-task rate; conflicting-edit rate; Skimmer gatekeeper miss rate on a labelled sample of real GC messages (defer-only until measured); fake-success rate (claimed done vs server-run build/tests); reviewer agreement ("hivemind") rate.

## Conflicts
- No published evidence covers a GC-only (no orchestrator) software team; Anthropic and Cognition both call shared-context coding a poor fit for fan-out. Our model is untested, so guards and metrics are not optional.
- Visible peer thinking vs sycophancy and conformity evidence (debate studies): reading others' reasoning lowers accuracy; the decided independent-first vote conflicts with unrestricted thinking visibility unless the exception above holds.
- "Thinking GCs" and "mandatory walk" appear in the assignment but in no deep-dive; their definitions here are inferred.
- CLAUDE.md lists agent VM topology as OPEN, while Bazaar.Soul marks two VMs per agent as CHANGED/decided on 2026-10-24. Runtime assumes only a workspace API.
- Agent_runtime_dotnet note: Orleans version, licence on NuGet and Windows footprint unverified; Anthropic rate-limit numbers are verified, other providers' are not.

## Questions for the owner
1. Who approves Onboarding to Active: owner each time, or automatic once a checklist passes? Default: owner for the first agents, automatic later.
2. Confirm the wake rule: only @mention, DM, assignment, digest, schedule, interrupt. Default: yes.
3. Should humans' non-mention messages ever wake an agent (for example the owner posting in a project GC)? Default: no, mention required.
4. Digest interval and delivery: default every 2 simulated working hours, and at shift start.
5. Per-turn token budgets by section (system / memory / task / Vault / backread): default 4k / 4k / 2k / 4k / 12k for Haiku-class, doubled for Sonnet-class. Acceptable?
6. Guard defaults (ping-pong 6, nest depth 4, 30 wakes/hour, 20 messages/thread without a human, repeat tool 3). Adjust?
7. What exactly is the "mandatory walk"? Proposed: forced pause plus digest-only re-entry. Please define or replace.
8. Do runtime subagents always use Haiku 5.5, or the parent's whitelist? Default: parent's whitelist, cheapest allowed model.
9. Thinking GCs: is thinking visible by default to all agents, or per-GC? Is it also visible to the owner in the UI always? Is it injected into other agents' context automatically? Default: readable on request only, owner always.
10. Withhold vote reasoning from Thinking GCs until reveal (Council exception)? Default: yes.
11. Subagent limits (depth 2, 3 concurrent, 40 steps): acceptable? May a subagent ever post to a GC? Default: no, only the parent posts.
12. Base: approve the plain-hosted-service start with `IAgentScheduler` seam and an Orleans spike, and the criteria above?
13. Is turn-level cost shown as a chip only, with no automatic stop? Default: yes, stop only via quotas and guards.
14. Turn/thinking retention: keep step payloads forever, or prune turn_step bodies after N days while keeping hashes and GC posts? Default: keep 90 days; counsel to review privacy.
