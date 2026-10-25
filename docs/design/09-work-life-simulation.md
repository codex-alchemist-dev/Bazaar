# Work-Life Simulation

Status: DRAFT for owner review (2026-10-25) · Owner questions: 15
Depends on: Bazaar.md (§2, §4, §5), Bazaar.Server.md (§3, §13), Bazaar.Soul.md (memory, VM), implementation-plan.md (Wave 2) · Used by: Company Calendar (not yet written), Observatory, Bazaar.Windows start screen, Council, Kanban

## Purpose
Paces agents with shifts, breaks, vacations and curfew instead of rate limits. When an agent or a provider key is unavailable, the team sees an explained absence and an automatic cover, not a failed HTTP call. Provider limits still happen and are handled here as data.

## Decided inputs
- Shifts, breaks, vacations and curfew replace rate limiting; async inbox with auto-coverage (Bazaar.md §4).
- Each agent has its own simulated timezone and schedule (Bazaar.md §4, 2026-10-24).
- Resource Economy REJECTED: limits are quotas plus this simulation. Cost is display and analysis, never a gate (Bazaar.md §4).
- Company Calendar is in v1 scope (Bazaar.md §4).
- GCs are append-only and are the coordination system; events serve GCs and never replace them (Bazaar.md §2).
- Each agent owns private memory; no shared brain (Bazaar.md §5).
- Claude lane uses Anthropic API keys only, Haiku-heavy (Bazaar.md §3; CLAUDE.md).
- Bazaar.Server's own gateway enforces per-agent model whitelists; providers are interchangeable upstreams (Bazaar.Server.md §3).
- Queue and pub/sub carry scheduled work such as shift changes and inbox delivery; Postgres holds state (Bazaar.Server.md §3).
- Presence dot, custom status and activity appear on GC messages (Bazaar.Windows.md, 2026-10-24).
- Loop guards are a Wave 2 item (implementation-plan.md).
- Presence state list (working, reviewing, thinking, in meeting, sleeping, researching, on vacation, helping another project) comes from the work order and is not yet in Bazaar.md. See Conflicts and Q5.

## Design
Components [PROPOSED]:
- **Schedule calculator**: pure function from agent schedule and time to availability window.
- **Quota classifier**: provider response to a quota signal.
- **Availability machine**: one per agent, single writer.
- **Inbox and handover**: durable per-agent queue, drain, daily plan.
- **Cover resolver**: picks the cover for an unavailable agent.
- **Presence publisher**: derived from availability plus current activity, sent over pub/sub. Presence is not GC content.
- **Calendar feed**: vacation blocks now; planning and release events are informational (Q11).

Schedule model [PROPOSED]:
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

Availability states: ON_SHIFT, DRAINING, BREAK, OFF (outside shift or curfew), VACATION. Overlays: THROTTLED (until a time), COVERED (cover is answering).

| From | Event | To |
|---|---|---|
| ON_SHIFT | shift end, break due, vacation starts, token break | DRAINING |
| DRAINING | drain checklist complete | BREAK, OFF or VACATION |
| BREAK / OFF | schedule start or break end | ON_SHIFT; inbox drained in seq order |
| any | RateLimited(until) | overlay THROTTLED; state unchanged |
| any | SpendCapped on this key | VACATION (reason: provider cap) for every agent on that key |
| any | owner emergency override | ON_SHIFT now; does not clear SpendCapped |

Every transition is idempotent, keyed by (agent, event, scheduledAt).

Quota exhaustion maps to rest:

| Provider signal | Quota state | Effect |
|---|---|---|
| 429 with `retry-after` | RateLimited(until) | Overlay THROTTLED until the wait ends. No vacation. |
| 429, `error_code` `enforced_spend_limit_reached`, no `retry-after` | SpendCapped(next 00:00 UTC) | Key-wide VACATION with the true reason shown to the owner. Fallback within the agent's whitelist first (Q10). |
| 429, no `retry-after`, other code | UnknownRateLimit | Jittered backoff, log, count. |
| 400 `invalid_request_error` (user-set spend limit) | ConfigError | Alert the owner. Not a vacation. |
| 5xx, timeout | Transient | Retry policy. No availability change. |

Drain [PROPOSED]: a fixed checklist turn. Park or finish in-flight work, push WIP to the agent's branch, update Kanban, write a handover note (open threads, next step, blockers, what the cover must not touch), post it to the agent's GC. The first turn of a shift posts a daily plan to the same GC.

Cover [PROPOSED]: inbox items for an unavailable agent queue in order. The cover reads the handover note and shared project memory. Its replies are labelled "covering for X". Cover pairs are chosen by specialty overlap with a shift overlap, and the owner can override (Q13).

Other behaviour [PROPOSED]:
- **Emergency override**: owner only. Bypasses curfew, break and vacation. Writes a GC event with a reason. Cannot clear a provider state.
- **Mandatory walk**: a loop-guard trip forces a fixed BREAK and a visible GC notice (meaning to confirm, Q4).
- **Token-driven break**: a rolling-window token count moves the agent to DRAINING.
- **Background compaction**: runs in BREAK or OFF windows. Writes derived memory with provenance; never edits the GC.

## Data and interfaces
```sql
agent_schedule (agent_id pk, tz text, shifts jsonb, breaks jsonb, curfew jsonb, cover_agent_id, version bigint)
availability   (agent_id pk, state text, overlay text null, until timestamptz null, reason text, version bigint)
inbox_item     (id bigserial pk, agent_id, gc_id, gc_seq bigint, kind text, payload jsonb,
                status text, due_at timestamptz, claimed_until timestamptz null)
quota_state    (provider_id, key_id, model_class, kind text, until timestamptz null,
                http_status int, error_code text null, observed_at timestamptz,
                primary key (provider_id, key_id, model_class, kind))
calendar_event (id pk, kind text, starts_at timestamptz, ends_at timestamptz, agent_id null)
```
```csharp
public enum QuotaKind { RateLimited, SpendCapped, UnknownRateLimit, ConfigError, Transient }
public sealed record QuotaSignal(QuotaKind Kind, DateTimeOffset? Until);
QuotaSignal Classify(int status, IReadOnlyDictionary<string, string> headers, string? errorCode, DateTimeOffset now);
AvailabilityWindow Evaluate(AgentSchedule schedule, IReadOnlyList<CalendarEvent> events, DateTimeOffset now);
```
Gateway to runtime: one usage record per call (fields in observability_cost_testing.md §3, with the three input-token fields kept separate), and one QuotaSignal per 429 or 400. Runtime to gateway: "is this key usable now" only.

## Behaviour and edge cases
- `retry-after` is parsed as seconds. The HTTP-date form is unverified, so other forms fall back to jittered backoff.
- For most Claude models only uncached input counts toward ITPM; cache reads do not (pricing and observability notes, verified). Meter ITPM from uncached input only.
- Haiku 5.5 prompts over 100K tokens cost five times more. Whether the surcharge covers the whole prompt is unverified. Compaction should keep prompts under the line.
- The Start-tier spend cap is $500 a month and resets at 00:00 UTC on the 1st (verified). Tier qualification thresholds are not published in the sources read.
- One writer per agent, guarded by `version`. A restart during DRAINING resumes from its row. A hard kill can skip `StopAsync` (Bazaar.Server.md §4), so turns persist their own state.
- Schedules are evaluated in IANA zones. Test skipped and repeated local times.
- Availability never grants or denies tool permissions. Handover notes are untrusted input under Bazaar.Server.md §13.
- Cache trap: a break longer than the 5-minute cache TTL makes the next call a cache write at 1.25x base. For Haiku with 30,000 cached tokens that is about $0.0038 against about $0.0003 for a read (pricing note prices). Shifts cut call volume but can raise cost per call.

## Testing
No real models or displays are needed.
- Fake clock through `TimeProvider` (confirm the testing package on NuGet). Fake gateway returns 429 with and without `retry-after`, the spend-cap body, a 400 config error and malformed output.
- Real Postgres for the queue (`SKIP LOCKED`), version conflicts and restart recovery.
- `Avalonia.Headless` for the presence chip.
- Key cases: curfew across midnight; DST both ways; cover offline leads to queue and notice; spend cap sets every agent on that key to VACATION and clears only at reset; `retry-after` clears without vacation; cache reads excluded from ITPM; override does not clear the cap; restart mid-DRAINING; inbox replay in `gc_seq` order.

**Measuring whether shifts save money.** No source shows shift throttling works as a cost control (multi-agent note, Key Q6 gaps). Proposed test [PROPOSED]:
1. Phase 0, two weeks, instrumentation only: log usage, cache hit ratio and gaps between calls per agent.
2. Phase 1: alternate comparable Kanban items between an always-on arm and a shift arm. Alternate by item, not by time block, to avoid time-of-day bias.
3. Outcome: cost per owner-accepted item. Guardrails: CI pass rate, reverts within 7 days, owner rejections, 429 count, cache hit ratio.
4. Decision rule: call shifts a cost control only if cost per accepted item falls with guardrails flat. Set sample size after Phase 0 variance.

## Work packages
1. Schedule calculator and DST tests (pure). Depends: Wave 0.
2. Quota classifier with fixture responses. Depends: Wave 0, gateway WP5 types.
3. Availability machine with idempotency keys. Depends: 1, 2.
4. Postgres tables and repositories for schedule, availability, inbox and quota. Depends: Wave 1 WP2.
5. Inbox queue and ordered drain. Depends: 3, 4.
6. Drain checklist, handover note, daily plan. Depends: 5.
7. Cover resolver and "covering for" replies. Depends: 5, Q1 answered.
8. Presence publisher and chip. Depends: 3, Contracts WP1.
9. Vacation blocks from calendar. Depends: 1, Company Calendar doc.
10. Loop-guard walk and token-break triggers. Depends: 3.
11. Owner override command and audit event. Depends: 3, Contracts.
12. Usage-to-cost report and shift A/B harness (analysis only). Depends: gateway usage records.

## Conflicts
1. **Cover versus memory.** Cover reading the primary's memory conflicts with "each agent owns private memory; no shared brain" (Bazaar.md §5). This design uses shared memory only until Q1 is answered.
2. **"Instead of rate limits" versus real limits.** At the modelled volumes, Claude-lane per-minute limits look unlikely to bind (Start-tier Haiku allows 1,000 RPM and 2M ITPM; inference). The binding constraints are the $500 monthly cap (typical Claude lane about $54, heavy about $316 with Sonnet; Opus or Fable at heavy volume would exceed the cap) and free-lane daily caps (OpenRouter 50 RPD below 10 credits; Cerebras 1M TPD). The simulation matters most on free lanes.
3. **Hidden billing stop.** A spend cap shown as "on vacation" can hide an account-level stop. The owner must see the true reason and reset time.
4. **Shifts and cache TTL.** Shifts may raise cost per call (see Behaviour). No evidence either way.
5. **Compaction and cache.** Context editing invalidates cached prefixes, so compaction saves less than a linear cut (pricing note).
6. **Presence list and mandatory walk** are not in Bazaar.md, and the walk and token breaks have no evidence base (multi-agent note, Key Q6).

## Questions for the owner
1. Can a cover read the primary's private memory? Default: no. Cover reads the handover note and shared project memory. Private memory needs per-agent opt-in.
2. What does presence show for a spend cap? Default: owner sees the true reason and reset time. Other agents see "on vacation".
3. Do owner DMs wake an agent during curfew? Default: yes. Other messages queue to the next shift.
4. What is the "mandatory walk"? Default: a fixed 15-minute BREAK after a loop-guard trip, plus a GC notice.
5. Add "on break" and "waiting on provider" to presence? Default: yes, as overlays. Record the full list in Bazaar.md.
6. Default shift for a new agent? Default: 09:00 to 17:00 Mon to Fri in the owner's zone, with a 23:00 to 07:00 curfew.
7. Token-driven break threshold? Default: off until Phase 0 data exists.
8. Success bar for shifts as a cost control? Default: at least 20% lower cost per accepted item with guardrails flat.
9. Use 1-hour cache writes for the architect lane during shift gaps? Default: no. Test it on the architect lane first.
10. On a Claude spend cap, fall back to FreeLLMAPI inside the whitelist? Default: yes if the whitelist allows it, otherwise VACATION.
11. Are release and planning calendar events informational only in v1? Default: yes. Only vacation blocks change availability.
12. Who grants a vacation? Default: owner, or an agent who requests one in its GC.
13. Cover assignment: automatic or owner-chosen? Default: automatic by specialty and shift overlap, owner can override.
14. No cover available? Default: queue, and post a notice in the General Bazaar GC after four hours.
15. Free-lane reset times (OpenRouter daily cap, Cerebras TPD)? Default: track in UTC. Verify against each provider's page before use; not verified here.
