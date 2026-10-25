# Work-Life Simulation

Status: RECONCILED with DECISIONS-LOG (2026-10-25) · Owner questions: 13
Depends on: 04-postgres-schema-and-migrations.md, 05-model-gateway.md, 06-agent-runtime.md, 15-security-taint-and-approvals.md, 18-vault.md, 23-observatory-and-usage.md, 24-company-systems-core.md, Bazaar.md (§2, §4, §5), Bazaar.Server.md (§3, §13), implementation-plan.md (Wave 2) · Used by: 06-agent-runtime.md, 10-council-and-voting.md, 12-kanban-and-dependencies.md, 23-observatory-and-usage.md, 24-company-systems-core.md, 26-fun-systems.md, 28-windows-client.md, 29-android-client.md

## Purpose
Paces agents with shifts, breaks, vacations and curfew instead of rate limits. When an agent or a provider key is unavailable, the team sees an explained absence and an automatic cover, not a failed HTTP call. Provider limits still happen; this doc consumes them as signals and decides what they mean for availability.

## Decided inputs
- Shifts, breaks, vacations and curfew replace rate limiting; async inbox with auto-coverage (Bazaar.md §4).
- Each agent has its own simulated timezone and schedule (Bazaar.md §4, 2026-10-24).
- Resource Economy REJECTED: limits are quotas plus this simulation. Cost is display and analysis, never a gate (Bazaar.md §4).
- Company Calendar is in v1 scope. `calendar_event` is owned by doc 24; this doc consumes it (DECISIONS-LOG round 12, C45).
- Table ownership (DECISIONS-LOG round 1, C04): this doc owns `agent_schedule` and `availability`. Doc 06 owns `inbox_item`, the agent row and turns; doc 05 owns `gw_quota_state` and model policy; doc 23 owns `agent_activity`.
- GCs are append-only and are the coordination system; events serve GCs and never replace them (Bazaar.md §2).
- Each agent's persistent memory is stored in the Vault; each agent owns private memory; no shared brain (Bazaar.md §5; DECISIONS-LOG round 11, C38).
- Claude lane uses both the owner's Anthropic API key and FreeLLMAPI's free tokens, switched by availability. Subscription (Pro/Max) logins stay off every route (DECISIONS-LOG round 4, C14).
- Bazaar.Server's own gateway enforces per-agent model whitelists; providers are interchangeable upstreams (Bazaar.Server.md §3).
- Postgres NOTIFY carries GC fan-out; Valkey (Linux) or Garnet (Windows) carries cache and presence, with Garnet gated by spike S2 (DECISIONS-LOG round 1, C07). Scheduled work such as shift changes runs on the server queue (Bazaar.Server.md §3).
- Loop guards are a Wave 2 item (implementation-plan.md). A loop-guard trip sends the agents involved to the Psych Ward on any trip (DECISIONS-LOG round 9, C34; doc 26).
- Interrupts use doc 24's per-agent stack (depth 2). An owner interrupt bypasses vacation only when tagged `emergency` (DECISIONS-LOG round 9, C35).
- Presence is eight labels plus overlays (DECISIONS-LOG round 11, C43). Its mapping and projection belong to doc 23.
- Wake reasons, the digest and the inbox belong to doc 06 (DECISIONS-LOG round 3, C08).

## Design
Components [PROPOSED]:
- **Schedule calculator**: pure function from agent schedule, calendar events and time to an availability window.
- **Quota consumer**: applies the provider signals from doc 05 (`QuotaSignal`, `AgentCapacityChanged`) to availability. Classification of provider responses stays in doc 05.
- **Availability machine**: one per agent, single writer.
- **Drain and handover**: drain checklist, handover note, daily plan. The inbox itself is doc 06's.
- **Cover resolver**: picks the cover for an unavailable agent.
- **Presence input**: this doc supplies availability and overlays to doc 23's presence projector. It publishes no presence of its own.

Schedule model [PROPOSED], stored in `agent_schedule`:
```json
{
  "agent": "example-agent",
  "tz": "Region/City",
  "shifts": [{"days": "Mon-Fri", "start": "09:00", "end": "17:00"}],
  "breaks": {"afterTokens": 0, "windowHours": 2, "lengthMin": 20},
  "curfew": {"start": "23:00", "end": "07:00"},
  "cover": "example-cover-agent"
}
```
Times are in the agent's IANA zone. Shifts may cross midnight. Precedence: vacation beats curfew, curfew beats shift. `afterTokens: 0` means token-driven breaks are disabled until measured.

Availability states: ON_SHIFT, DRAINING, BREAK, OFF (outside shift or curfew), VACATION. Overlays: THROTTLED (until a time) and COVERED (cover is answering). Presence overlays shown by doc 23 are on break (during BREAK), waiting for approval (turn state from doc 06), throttled and covered.

| From | Event | To |
|---|---|---|
| ON_SHIFT | shift end, break due, vacation starts, token break | DRAINING |
| DRAINING | drain checklist complete | BREAK, OFF or VACATION |
| BREAK / OFF | schedule start or break end | ON_SHIFT; inbox drained in `seq` order |
| any | RateLimited(until) | overlay THROTTLED; state unchanged |
| any | SpendCapped on a source | doc 05 reroutes the agent to another allowed source; if none remains, VACATION (reason: provider cap) |
| any | owner `emergency` interrupt | ON_SHIFT now, bypassing vacation; does not clear provider state |

Every transition is idempotent, keyed by (agent, event, scheduledAt).

Quota signals and their effect here (classification is doc 05's):

| Signal from 05 | Effect |
|---|---|
| RateLimited(until) | Overlay THROTTLED until the wait ends. No vacation. |
| SpendCapped(until) | Reroute first (table above). VACATION only when no allowed source remains; the true reason and reset time are shown to the owner. |
| UnknownRateLimit | No availability change. Jittered backoff in doc 05. |
| ConfigError | Alert the owner. Not a vacation. |
| Transient | No availability change. |

Drain [PROPOSED]: a fixed checklist turn. Park or finish in-flight work, push WIP to the agent's branch, update Kanban, write a handover note (open threads, next step, blockers, what the cover must not touch), post it to the agent's GC. The first turn of a shift posts a daily plan to the same GC.

Cover [PROPOSED]: inbox items for an unavailable agent queue in `seq` order (doc 06's `inbox_item`). The cover reads the handover note and shared project areas of the Vault. It does not read the primary's private Vault area by default (Q1). Its replies are labelled "covering for X". Cover pairs are chosen by specialty overlap with a shift overlap, and the owner can override (Q10).

Other behaviour [PROPOSED]:
- **Emergency override**: an owner interrupt tagged `emergency` (doc 24). It bypasses vacation (C35). Whether it also bypasses curfew and break is Q4. It writes a GC event with a reason and cannot clear a provider state.
- **Loop-guard trip**: hands the agents involved to the Psych Ward (doc 26). This doc defines no separate walk.
- **Token-driven break**: a rolling-window token count moves the agent to DRAINING. Off until Phase 0 data exists (Q6).
- **Background compaction**: runs in BREAK or OFF windows; writes derived memory through `VaultWriter` (doc 08); never edits the GC.

## Data and interfaces
Tables owned here:
```sql
agent_schedule (agent_id pk, tz text, shifts jsonb, breaks jsonb, curfew jsonb, cover_agent_id, version bigint)
availability   (agent_id pk, state text, overlay text null, until timestamptz null, reason text, version bigint)
```
Consumed, not defined here: `inbox_item` (06), `gw_quota_state` and `gw_usage` (05; `gw_usage` carries the 5-minute and 1-hour cache-write columns, DECISIONS-LOG round 9, C33), `calendar_event` with `affects_availability` (24), `agent_activity` (23).
```csharp
AvailabilityWindow Evaluate(AgentSchedule schedule, IReadOnlyList<CalendarEvent> events, DateTimeOffset now);
Task ApplyAsync(AgentId agent, QuotaSignal signal, DateTimeOffset now);   // from 05
Task ApplyAsync(AgentId agent, AgentCapacityChanged change);            // from 05
```
Only events with `affects_availability` change availability (Q13).

## Behaviour and edge cases
- One writer per agent, guarded by `version`. A restart during DRAINING resumes from its row. A hard kill can skip `StopAsync` (Bazaar.Server.md §4), so turns persist their own state.
- Schedules are evaluated in IANA zones. Curfew across midnight and repeated or skipped local times (DST) must be handled.
- Availability never grants or denies tool permissions. Handover notes are agent text: soft taint under doc 15, never a permission grant (Bazaar.Server.md §13).
- A break longer than the 5-minute cache TTL makes the next call a cache write at 1.25x base. For Haiku with 30,000 cached tokens that is about $0.0038 against about $0.0003 for a read (pricing note, verified). Shifts cut call volume but can raise cost per call.
- The Start-tier spend cap is $500 a month and resets at 00:00 UTC on the 1st (verified). Shared rate limits reference doc 15's quota table (C36).

## Testing
No real models or displays are needed.
- Fake clock through `TimeProvider`. Fake gateway returns 429 with and without `retry-after`, the spend-cap body, a 400 config error and malformed output.
- Real Postgres for `agent_schedule`, `availability` and the drain against doc 06's `inbox_item` (`SKIP LOCKED`), version conflicts and restart recovery.
- `Avalonia.Headless` for the presence chip in doc 28, fed by doc 23's presence.
- Key cases: curfew across midnight; DST both ways; cover offline leads to queue and notice; SpendCapped reroutes to an allowed source, and VACATION occurs only when none remains, clearing only at reset; `retry-after` clears without vacation; an untagged owner interrupt does not bypass vacation, a tagged one does; the emergency tag does not clear a provider cap; restart mid-DRAINING; inbox replay in `seq` order.

**Measuring whether shifts save money.** No source shows shift throttling works as a cost control (multi-agent note, Key Q6 gaps). Proposed test [PROPOSED]:
1. Phase 0, two weeks, instrumentation only: log usage, cache hit ratio and gaps between calls per agent.
2. Phase 1: alternate comparable Kanban items between an always-on arm and a shift arm. Alternate by item, not by time block, to avoid time-of-day bias.
3. Outcome: cost per owner-accepted item. Guardrails: CI pass rate, reverts within 7 days, owner rejections, 429 count, cache hit ratio.
4. Decision rule: call shifts a cost control only if cost per accepted item falls with guardrails flat. Set sample size after Phase 0 variance.

## Work packages
1. Schedule calculator and DST tests (pure). Depends: Wave 0.
2. Availability machine with idempotency keys; signal consumers for doc 05's types. Depends: 1, doc 05 signal types.
3. Repositories for `agent_schedule` and `availability`. Depends: Wave 1 WP2 (04), doc 06's agent table.
4. Ordered inbox drain on doc 06's `inbox_item`. Depends: 2, 3, doc 06 inbox WP.
5. Drain checklist, handover note, daily plan. Depends: 4.
6. Cover resolver and "covering for" replies. Depends: 4, Q1 and Q10 answered.
7. Presence inputs into doc 23's projector, and the presence chip (doc 28). Depends: 2, doc 23 projector, Contracts WP1.
8. Vacation blocks from `calendar_event` with `affects_availability`. Depends: 1, doc 24 calendar migration.
9. Loop-guard hand-off to the Psych Ward and token-break triggers. Depends: 2, doc 26.
10. Owner `emergency` interrupt: vacation bypass, audit event. Depends: 2, doc 24's `PushAsync`.
11. Usage-to-cost report and shift A/B harness (analysis only). Depends: `gw_usage` (05), doc 23.

## Conflicts
Remaining, for this doc or the owner:
1. **Private memory and cover reads.** Bazaar.md §5 ("no shared brain") now depends on a Vault access layer that enforces per-agent private areas (C38). Docs 08 and 18 must design it. Until then, Q1 governs what a cover may read.
2. **"Instead of rate limits" versus real limits.** Anthropic per-minute limits look unlikely to bind at modelled volumes (inference). The binding limits are the $500 monthly cap and free-lane daily caps on FreeLLMAPI routes (OpenRouter 50 RPD below 10 credits; Cerebras 1M TPD).
3. **Hidden billing stop.** A spend cap shown as "on vacation" can hide an account-level stop. The owner must see the true reason and reset time.
4. **Shifts, cache and compaction.** Shifts may raise cost per call, and compaction saves less than a linear cut because it invalidates cached prefixes (pricing note). No evidence either way.
5. **Token-driven breaks** have no evidence base (multi-agent note, Key Q6).

New, for other docs to fix:
- **06:** the agent row carries `tz` and `schedule jsonb` (C04). Remove both; `agent_schedule` is canonical here.
- **05:** the pipeline writes "one `UsageRecord`" and the table is `gw_quota_state`; both must match `gw_usage` (C33) and C04 naming. Doc 05 must also state the SpendCapped reroute to the other allowed source (C14).
- **23:** the presence table has no "on break" or "waiting for approval" overlay (C43). Add them; the BREAK and idle row keeps "sleeping" with the "on break" overlay.
- **24:** the Schedule bullet lets any owner interrupt bypass curfew, break and vacation. C35 limits the vacation bypass to the `emergency` tag. Doc 24 must state the tag in that bullet.
- **08 and 18:** the Vault access layer for private areas (C38), see Conflict 1.

## Questions for the owner
1. May a cover read the primary's private Vault area? Default: no. Cover reads the handover note and shared project areas. Private areas need per-agent opt-in.
2. What does presence show for a spend cap? Default: the owner sees the true reason and reset time; other agents see "on vacation".
3. Do owner DMs wake an agent during curfew? Default: yes, as a wake reason from doc 06. Other messages queue to the next shift.
4. Does an `emergency`-tagged owner interrupt also bypass curfew and break? Default: yes for curfew and break, matching doc 24's Q9 default; provider caps are never cleared.
5. Default shift for a new agent? Default: 09:00 to 17:00 Mon to Fri in the owner's zone, with a 23:00 to 07:00 curfew.
6. Token-driven break threshold? Default: off until Phase 0 data exists.
7. Success bar for shifts as a cost control? Default: at least 20% lower cost per accepted item with guardrails flat.
8. Use 1-hour cache writes for the architect lane during shift gaps? Default: no. Test it on the architect lane first.
9. Who grants a vacation? Default: the owner, or an agent who requests one in its GC.
10. Cover assignment: automatic or owner-chosen? Default: automatic by specialty and shift overlap; the owner can override.
11. No cover available? Default: queue, and post a notice in the General Bazaar GC after four hours.
12. Free-lane reset times (OpenRouter daily cap, Cerebras TPD)? Default: track in UTC. Verify against each provider's page before use; not verified here.
13. Are release and planning calendar events informational only in v1? Default: yes. Only events flagged `affects_availability` in doc 24 (vacation blocks) change availability.

ANSWERED: Mandatory walk: a loop-guard trip sends the agents to the Psych Ward on any trip (DECISIONS-LOG round 9, C34).
ANSWERED: Presence list: eight labels plus overlays such as "on break" and "waiting for approval" (DECISIONS-LOG round 11, C43).
ANSWERED: Spend-cap fallback: the Claude lane uses FreeLLMAPI by availability, within the agent's whitelist (DECISIONS-LOG round 4, C14). Whitelist gating is doc 05's.
