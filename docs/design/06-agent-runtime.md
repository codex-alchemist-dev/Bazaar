# Agent Runtime

Status: RECONCILED with DECISIONS-LOG (2026-10-25) · Owner questions: 11
Depends on: 01-gc-data-model.md, 04-postgres-schema-and-migrations.md, 05-model-gateway.md, 07-soul-system.md, 08-memory-system.md, 09-work-life-simulation.md, 14-identity-service-and-passport.md, 15-security-taint-and-approvals.md, 16-agent-vms-and-environments.md, 24-company-systems-core.md · Used by: 10-council-and-voting.md, 12-kanban-and-dependencies.md, 13-skimmer-bouncer.md, 17-coding-agent-harness.md, 23-observatory-and-usage.md, 25-company-systems-knowledge.md, 26-fun-systems.md

## Purpose
The runtime inside Bazaar.Server that runs persistent agents: it decides when an agent runs, builds what the model sees, runs the turn loop, executes tools for the agent, and survives crashes. It coordinates nothing itself; agents coordinate in GCs. It owns the agent, turn, step, lease and `inbox_item` tables (C04).

## Decided inputs
- No master orchestrator; GCs are the coordination system; events and reactions serve GCs (CLAUDE.md, Bazaar.md §1–2).
- GCs are append-only (`gc_event`, `gc_message`, `gc_message_version`; 01; round 1, C09). Email is external-only.
- Memory lives in the Vault; Postgres only indexes it (08; round 11, C38).
- Limits are quotas plus the Work-Life Simulation; Resource Economy is REJECTED (CLAUDE.md).
- Each agent has a Windows VM and a Linux VM; the runtime sees only the guest channel (16; round 1, C03).
- Claude lane: owner's Anthropic key and FreeLLMAPI free tokens, by availability; subscription logins never route (round 4, C14).
- Model calls use the `ModelEvent` seam with an `IChatClient` adapter in v1 (05; round 8, C29). Runtime never sees provider keys.
- Taint: external content taints hard; agent text taints softly and only from a tainted source session; Tier 2 is gated on hard taint (15; round 2, C06).
- Reader/actor split is v1 for external input (round 6, C18; Question 11). Actor turns never read external content directly.
- Interrupts use 24's per-agent stack (depth 2); only an owner `emergency` interrupt bypasses vacation (round 9, C35).
- A loop-guard trip sends the agents involved to the Psych Ward (26; round 9, C34).
- Skimmer may promote an unmentioned message to wake-now under a cap; it never drops one (13; round 3, C08).
- Promotions, hiring and retirement run as Council votes; split or tied votes on bigger changes go to the owner (10; round 9, C32).
- Credentials come from 14's `ISecretStore`; no secret enters a context or a VM (round 2, C05).
- Personal fields are per-person encrypted from the first migration (round 2, C01).

## Design

### Lifecycle [PROPOSED]
| State | Meaning | Entered by |
|---|---|---|
| Created | Agent row, Soul version, model policy, grants | owner, or an approved Council hiring vote (10) |
| Onboarding | Reads Constitution, role brief and project GCs; writes first memories; read-only grants | automatic after Created |
| Active | Eligible to wake | onboarding checklist passes (Question 1) |
| Resting | Off-shift, vacation or quota-exhausted; inbox accumulates | 09 schedule, or `RateLimited`/`SpendCapped` from 05 |
| Retired | Never wakes; Soul, memory and history read-only | owner, or a Council vote (10) |

Presence labels (09, C43) are derived from this state and the schedule; they are not stored here.

### Wake model
| Reason | Source | Rule |
|---|---|---|
| Interrupt | 24 stack | Pushed at the next tool boundary; `emergency` bypasses vacation |
| Mention or DM | 01 | Wakes immediately |
| Assignment | 12 | Card assigned, or review requested |
| Scheduled | 09 | Shift start, reminders |
| Skimmer-promoted | 13 | Moves a digest item to wake-now early, under a cap; never drops |
| Digest | 06 | Batches unmentioned GC activity; cadence [PROPOSED] every 2 simulated working hours and at shift start |

An unmentioned message is stored and reaches the agent through the digest unless 13 promotes it. Each pending item is one `inbox_item` row; one digest table and one cadence serve all subsystems (C08). A wake takes the highest-priority pending items and starts one turn. Priority order [PROPOSED]: interrupt, mention/DM, assignment, scheduled, Skimmer-promoted, digest. Wake signal uses Postgres NOTIFY with a poll fallback [PROPOSED] (C07).

### Turn loop
1. Claim: take `agent_lease` (one active turn per agent); mark inbox items `claimed`.
2. Assemble context (below).
3. Call the model through the 05 seam, streaming. Persist each response and tool result as a `turn_step` before acting on it.
4. Dispatch tool calls, append results, repeat until a final message, step cap (40), token ceiling, wall clock or interrupt.
5. Post outputs through the GC API (`AppendMessage`), so mention parsing and loop guards apply to agents and humans alike.
6. Commit: mark inbox items done, submit memory proposals, release the lease.

A **reader turn** (`turn.role = reader`) holds external content, has no write tools, and emits a schema-validated hand-over (15). Actor turns receive only the validated fields, tagged with the 15 provenance enum (C20).

### Context assembly [PROPOSED]
| Section | Source | Rule |
|---|---|---|
| System | role, Constitution excerpt, Soul personality, tool schemas | fixed, cache-friendly prefix |
| Memory | Vault notes via 08, ranked by 08's index | top-k with provenance ids |
| Task state | Kanban card, blockers, PR status | current only |
| Vault | wiki-linked docs for the task | by link and search, capped |
| GC backread | trigger thread in full at its current `gc_message_version`; other GCs summarised | never skip the trigger |
| Inbox | what woke the agent; reader hand-overs | verbatim; hand-overs carry validated fields only |

Budgets are in Question 3. The manifest stores ids, version ids and hashes, not text, so a turn is reproducible.

### Tool dispatch
`ToolDispatcher` (15) evaluates agent, session taint, tool, arguments and rate state, and returns Allow, RequireApproval or Deny. Credentials are injected host-side from `ISecretStore`; the model never sees secrets or host paths. VM calls go through 16's guest channel. Under hard taint, Tier 2+ calls require approval; soft taint alone does not gate. Each call is logged with arguments hash, exit code and duration. Per-call usage goes to `gw_usage` (05, C33), not `turn_step`.

### Subagents [PROPOSED]
A subagent is a child turn loop with fresh context, a tool allowlist that is a subset of the parent's, and a model from the parent's whitelist (Question 5). Limits, owned here (C36): depth 2, 3 concurrent children, a step and token ceiling per child. Children never post to a GC; only the parent does (Question 6). The child returns a summary and a trace id; the full trace is stored for the parent and reviewers.

### Loop guards [PROPOSED]
Counters are server-side and keyed by thread. On a trip the runtime stops waking the agents involved, posts a system notice in the GC, and admits them to the Psych Ward (26). Trips are logged to `guard_event`.
| Guard | Default |
|---|---|
| Ping-pong: two agents alternate N messages with no human or tool result between | 6 |
| Nested @mention chain depth | 4 |
| Per-agent wakes per hour (shared cap, Question 10) | 30 |
| Agent messages in a thread without a human message | 20 |
| Identical tool call repeated | 3 |

### Thinking GCs
Each agent has a Thinking GC nested under each GC it works in (01; C11). Per-turn reasoning is posted as steps. The owner always reads; peers read on request; thinking is never injected into another agent's context. Vote reasoning is withheld until the tally is revealed. 25 and 26 do not publish Thinking GC text.

### Concurrency and recovery
One active turn per agent, via `agent_lease` (expiry and heartbeat). Mentions arriving mid-turn queue; interrupts are checked at tool boundaries. 05's quota state gates model calls: `RateLimited` or `SpendCapped` moves agents to Resting rather than failing turns. Each step is a row written before the next action. On restart the expired lease is recovered: completed tool results are reused; an in-flight model call is re-issued; an in-flight tool call is re-issued only if marked idempotent, otherwise recorded as `unknown` and the model is told. Nothing relies on shutdown hooks. Personal data in `turn_step` payloads is stored as per-person encrypted payloads (C01).

### Base choice [PROPOSED]
Start with a plain `BackgroundService` plus Postgres inbox and lease (`FOR UPDATE SKIP LOCKED`), behind `IAgentScheduler` so Orleans can replace it. Adopt Orleans only if it removes lease and recovery code without storing state outside Postgres or losing on idle memory and Windows hosting. Microsoft Agent Framework is cherry-picked for libraries only; its speaker-selecting orchestration conflicts with the no-orchestrator rule.

## Data and interfaces
```sql
agent(id, name, soul_version_id, state, model_policy_id, created_at)  -- policy -> 05; schedule and tz -> 09
inbox_item(id, agent_id, kind, gc_id, gc_seq, priority, state, due_at, claimed_by, claimed_at)  -- kinds added by 09, 12, 13, 24
turn(id, agent_id, role /*actor|reader*/, parent_turn_id, depth, wake_reason, status, started_at, ended_at, context_manifest jsonb)
turn_step(turn_id, n, kind /*model|tool|post|memory*/, request_hash, response jsonb, idempotent bool)
agent_lease(agent_id, holder, expires_at)
guard_event(id, gc_id, kind, counters jsonb, created_at)
```
```csharp
interface IAgentScheduler { Task WakeAsync(AgentId id, WakeReason r, CancellationToken ct); }
interface IContextAssembler { Task<ContextPlan> BuildAsync(Turn t, TokenBudget b, CancellationToken ct); }
interface IToolDispatcher { Task<ToolResult> InvokeAsync(Turn t, ToolCall c, CancellationToken ct); }
interface ILoopGuard { GuardVerdict Check(GcMessage m, ThreadStats s); }
// Interrupts: 24's PushAsync/PopAsync. No flat Interrupt method.
```
Contracts: the runtime calls GC `AppendMessage` and reads the current `gc_message_version` by `(gc_id, seq)` (01). It calls 05 with `(agentId, request)` and receives `ModelEvent`s, usage and `RateLimited(until)`/`SpendCapped`. It calls 08 `Search` and `Propose`; writes are validated server-side and land through VaultWriter. It emits `TurnStarted`, `TurnEnded` and `GuardTripped` to 23.

## Behaviour and edge cases
- Evidence on multi-agent cost and coordination is mixed (Anthropic reports about 15x tokens for multi-agent work; debate studies show more rounds can hurt). Guards and metrics are not optional.
- External content arrives by polling; the home host opens no inbound connections (C30).
- Edited messages: context uses the current version; the manifest records the version id seen.
- An agent's claim of "done" never counts; the server runs clean builds and tests (C37).
- Cost is a per-turn chip, never a gate (Question 8).

## Testing
Fake gateway (scripted, cassette replay keyed by request hash, injected 429, spend cap, timeout, malformed tool calls), fake clock, injectable ids, real Postgres for inbox, lease and recovery. Key cases: a mention wakes exactly one turn; an unmentioned message reaches only the digest unless promoted, and promotion never drops; a ping-pong trip admits the agents to the Psych Ward; a kill between steps resumes without repeating a completed tool; a non-idempotent in-flight tool is marked `unknown`; two simultaneous mentions give one active turn; interrupt push and pop at depth 2; an actor turn's context never contains raw external text; hard taint blocks Tier 2 and soft taint does not; vote reasoning is absent from Thinking GCs before reveal; replay reproduces the same steps. LLM-judge evals run nightly and do not gate.

## Work packages
1. Schema and repositories: agent, inbox_item, turn, turn_step, agent_lease (04 conventions).
2. Inbox and wake service: priorities, digest batching (1).
3. Lease and crash-recovery job (1).
4. Context assembler with budgets and manifest (1; 08 and 01 read APIs).
5. Turn loop and step persistence over the 05 seam with a fake (1, 3).
6. Tool dispatcher integration: grants, taint gate, logging (5; 15).
7. Loop guards, `guard_event`, Psych Ward admission (2; 26).
8. Subagent runner with depth and tool limits (5, 6).
9. Thinking GC writer and visibility rules (5; 01).
10. Record and replay harness, scenario tests (5; 34).
11. Orleans spike behind `IAgentScheduler` (2, 3).
12. Pilot metrics: vote flips, guard trips per 100 threads, tokens per outcome, fake-success rate, Skimmer miss rate (5, 7).

## Conflicts
- 07 lists Soul states active, paused and retired; this doc's lifecycle replaces them (C04).
- 09 still defines a 15-minute break and "mandatory walk" (C34), its own `inbox_item` and schedule tables (C04), and an owner override that must use 24's `emergency` tag (C35).
- 13 has a 30-minute digest, its own `digest_items` table and a 20-per-hour scored-wake cap; all must move to this doc's inbox and cadence and 15's shared cap table (C08, C36).
- 17 gives runtime subagents depth 1 and a Haiku default; subagent limits are owned here (C36).
- 05 and 04 must use the C04 names (`agent_model_policy`, `quota_state`); 04's `agent_model_allow` is dropped.
- 15 must define the reader hand-over schema, the shared quota table (C36) and the session role used here as `turn.role`.
- 24 must define the `PushAsync`/`PopAsync` contract (C35).
- 01 must use the `gc_event`/`gc_message`/`gc_message_version` names and the 15 trust enum (C09, C20).
- 08 must be redrafted to Vault-backed memory (C38).
- 16 names the guest interface `IGuestChannel`; 17 names `IWorkspace`. They must be one interface.

## Questions for the owner
1. Onboarding to Active: owner each time, or automatic once the checklist passes? Default: owner for the first agents, automatic later.
2. Digest cadence: every 2 simulated working hours and at shift start; 13 adopts it (C08). Acceptable?
3. Per-turn token budgets (system / memory / task / Vault / backread): 4k / 4k / 2k / 4k / 12k for Haiku-class, doubled for Sonnet-class. Acceptable?
4. Guard defaults (ping-pong 6, nesting 4, 30 wakes per hour, 20 agent messages per thread without a human, repeated tool call 3). Adjust?
5. Runtime subagents: parent's whitelist, or always Haiku 5.5? Default: parent's whitelist, cheapest allowed model.
6. Subagent limits (depth 2, 3 concurrent, 40 steps): acceptable? May a subagent post to a GC? Default: no.
7. Base: approve the plain hosted service behind `IAgentScheduler`, with the Orleans spike and criteria above?
8. Cost as a per-turn chip only, with stops only from quotas and guards? Default: yes.
9. Retention of `turn_step` bodies: keep 90 days, then prune bodies and keep hashes and GC posts. Counsel to review against the C01 erasure window.
10. Skimmer-promoted wakes: share the 30-per-hour wake cap, or have their own? Default: share one cap from 15's table.
11. Confirm the reader/actor split as the v1 mechanism for external input (C18, Claude's reading). Default: yes, starting with email triage.

ANSWERED: only Skimmer promotion wakes an agent for an unmentioned message (DECISIONS-LOG round 3, C08).
ANSWERED: no "mandatory walk"; a loop-guard trip admits the agents to the Psych Ward (round 9, C34).
ANSWERED: Thinking GC visibility and withheld vote reasoning (round 3, C11).
ANSWERED: interrupts use 24's stack; vacation bypass needs the `emergency` tag (round 9, C35).
