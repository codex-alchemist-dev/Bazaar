# Skimmer Bouncer

Status: RECONCILED with DECISIONS-LOG (2026-10-25) · Owner questions: 13
Depends on: 01-gc-data-model.md (GC message log and versions), 04-postgres-schema-and-migrations.md (NOTIFY, migrations), 05-model-gateway.md (gateway, model policy, ModelEvent seam), 06-agent-runtime.md (wake reasons, `inbox_item`, loop guards), 09-work-life-simulation.md (shifts, curfew, availability), 15-security-taint-and-approvals.md (reader sessions, taint, quota table) · Used by: 28-windows-client.md (what-changed dashboard), 29-android-client.md (wake-ups), 26-fun-systems.md (Lucky run override)

## Purpose
A cheap server-side gatekeeper that reads each GC message an agent belongs to and decides whether it wakes the agent's turn now or waits in that agent's digest. It never hides a message from the record, other agents or the owner, and it never drops an inbox item.

## Decided inputs
- GCs are append-only and never purged; gating must not change the record. (01; C09)
- GCs are the coordination system; each agent has its own timezone and Work-Life schedule. (Bazaar.md §2, §4; 09)
- Per-agent model whitelists, enforced by Bazaar.Server's own gateway. Claude lane: API key plus FreeLLMAPI free tokens by availability; Pro/Max logins never route. (05; C14)
- Wake reasons and the single `inbox_item` table belong to 06; one digest, one cadence. (06; C04, C08)
- An unmentioned message may wake an agent immediately when the Skimmer scores it high, under a cap; the scored wake ships only after the shadow gates. (C08)
- Taint: external content taints hard, agent text softly; Tier 2 gates on hard taint. Reader sessions are a v1 primitive in 15. (15; C06, C18)
- Shared limits sit in 15's quota table; the scored-wake cap is 13's own limit. (C36)
- Personal fields are per-person encrypted payloads; erasure destroys the key. (01, 04; C01)
- No NPU on the server or clients for this gate (17; C17). Skimmer is v1 scope; its miss rate is measured first (Bazaar.md §4).

## Design
Placement [PROPOSED]:
1. **Append first.** A message commits to the GC log with a Postgres NOTIFY (C07). The gate never blocks a write.
2. **Fan-out.** One consumer per agent reads `gc_message` rows in seq order.
3. **Rules stage** (synchronous, deterministic). Its result is final for the rules below.
4. **Scoring stage** (async) for the remainder, against the agent's subscriptions, through a reader session and the gateway. Call unit: see Cost.
5. **Record** every decision in `wake_decisions`. **Wake** creates or promotes an `inbox_item` with `due_at` = now and the wake reason from 06. **Defer** enqueues a `gc_digest` item due at the digest time.

| Path | Rule [PROPOSED] | Bypasses scorer | Action |
|---|---|---|---|
| mention | @mention of the agent or its handle | yes | wake (`@mention or DM`) |
| dm | Two-member GC with the agent | yes | wake (Q2) |
| critical | Blocked, review requested, decision called, owner message | yes | wake |
| quarantine | Sender quarantined (22) | n/a | defer, tagged |
| scored | Relevance at or above threshold for a subscription (GC, tag, path, Kanban card) | no | promote early, wake reason `Skimmer-promoted` (06), under cap |
| default | Anything else | no | defer |

- **Scored cap:** at most 20 scored wakes per agent per hour (Q7). Mention-class wakes are exempt. Loop guards (06) cover ping-pong; a trip sends the agents involved to the Psych Ward (26; C34).
- **Modes:** `off` (every message wakes), `shadow` (decisions recorded, delivery unchanged), `defer` (enforced). New agents start in `shadow`. Promotion from `shadow` is gated by the measurements below.
- **Interrupts** (owner interrupts, build-broken, security events) use 24's push contract and do not pass through this gate (C35).
- **Availability** (shifts, curfew, vacation) is read from 09, which owns it.

**Digest** [PROPOSED]: one `gc_digest` kind on 06's `inbox_item`. Payload: `gc_id`, `seq`, `category`, `reason`. No message text is copied: the quoted excerpt is read from the message at display time and marked untrusted, so erasure needs no second copy (Q12). Cadence is 06's single cadence (Q4). Pending items join the next turn.

**Feedback** [PROPOSED]: agent tools `flag_missed(seq)` and `flag_false_wake(seq)` write `gate_labels` with source `agent_flag`. Reasons are stored, never forwarded. Flags never change thresholds automatically.

## Cost
The call unit is open. Figures are arithmetic on the research note's formula (about 13,500 messages a month, six agents), not measurements.

| Unit | Calls per month | Tokens per call | Cost (note's formula) |
|---|---|---|---|
| Per (message, agent) | about 81,000 | about 1,000 | about $10.13 |
| One batched call per message, all subscribing agents | about 13,500 | about 3,000 | about $4.39 |

The batched unit [PROPOSED default] returns one closed result per agent in the same call. Shadow mode scores every pair before any promotion, so v1 pays full scoring cost regardless of promotion. FreeLLMAPI free tokens may change the figures; that is unmeasured.

## Alternatives
| Option | Strength | Weakness |
|---|---|---|
| Mention-only plus digest | No model risk | Misses unmentioned relevant work |
| Rules (keywords, paths, tags) | Deterministic | Brittle wording |
| Hosted Haiku 5.5 scorer | Best judgement | GC text sent to a vendor |
| Hosted Gemini 2.5 Flash-Lite | Cheap | Free tiers may use prompts |
| Small CPU model (Qwen3 0.6B to 1.7B, Gemma 4 E2B) | No vendor | Latency and accuracy unmeasured |

Recommendation [PROPOSED]: rules plus digest first. Add the scorer only after shadow data exists.

## Data and interfaces
```
gate_config(agent_id PK, mode, threshold, scored_wake_cap_per_hour, subscriptions jsonb)
wake_decisions(id, gc_id, seq, agent_id, path, decision, shadow, score_bp, category,
               model_id, prompt_version, created_at, UNIQUE(gc_id, seq, agent_id, prompt_version))
gate_labels(id, decision_id, label, source, labeller, created_at)
gate_run_override(run_id, agent_id, expires_at, max_wakes, wakes_used, PK(run_id, agent_id))  -- [PROPOSED], Q11
```
Table ownership (C04, C16): this doc owns the four tables above. `inbox_item` and its wake reasons belong to 06. The former `digest_items` table is removed. Doc 06 must add a unique key on (agent_id, gc_id, seq, kind) so duplicate enqueues are idempotent. `digest_minutes` is removed from `gate_config`; cadence is 06's.

```csharp
public interface IWakeGate {
    ValueTask<WakeDecision> DecideAsync(GcMessageRef msg, GateConfig agent, CancellationToken ct);
}
public interface IRelevanceScorer {
    // Calls through 05's ModelEvent seam (C29); one or many agents per request.
    Task<IReadOnlyList<RelevanceResult>> ScoreAsync(ScoreRequest request, CancellationToken ct);
}
public sealed record ScoreRequest(GcMessageRef Message, IReadOnlyList<AgentProfile> Agents);
public sealed record RelevanceResult(AgentId Agent, Category Category, int ScoreBasisPoints);
public enum Category { Question, Review, Blocked, Decision, Chat, Other }
```
Scorer output is a closed object per agent, e.g. `{"agent":"…","category":"question","relevance_bp":7200}`. Any extra key or out-of-enum value is a parse failure and takes the fallback path.

## Behaviour and edge cases
- **Invariant:** every (message, member agent) pair is delivered, or sits as an undelivered `gc_digest` item, until delivered. Nothing is dropped.
- **Ordering:** within one GC, a wake delivers pending items in seq order. Across GCs, 06's priority applies (Q5).
- **Idempotency:** keyed by (gc, seq, agent, prompt version); a duplicate NOTIFY makes no second wake.
- **Edits:** a new `gc_message_version` (C09). Re-decide only if the edit adds a mention, DM target or critical category.
- **Concurrency:** one consumer per agent; bounded queue; above a depth limit, rules only.
- **Failure:** timeout 5 s (Q3), error, parse failure or gateway down: rules stand, unmatched items stay in the digest, and an item pending 60 minutes forces a wake (Q3, Q13).
- **Run override:** while a `gate_run_override` row is live, that agent's scored cap does not apply for that run, and it wakes at most `max_wakes` times (Q11). The override never bypasses the record or the invariant.
- **Restart:** `gc_message` is the source of truth; missed seqs are re-decided on startup.
- **Privacy:** a hosted scorer sends GC text to a third party, and free pools may use prompts. Needs counsel on multi-person installs (research, not advice).

## Security
The scorer is a reader session (15; C18): no tools, no Soul, no memory, no secrets. Message text sits in a delimited data block. Output is a closed schema, and no free text reaches an agent. An injection can delay or force a wake, bounded by the cap (mentions exempt), but cannot drop a message or change config. The gate is **not** a security control: the woken agent still reads raw untrusted text, so 15's taint rules apply in full.

## Testing
- **Unit (xUnit v3):** mention beats a 0.0 score; parser rejects extra keys and bad enums; threshold edges; cap; fallback on timeout, exception and parse failure; promotion sets `due_at` to now.
- **Property:** every pair ends delivered or as an undelivered digest item.
- **Postgres (real fixture):** duplicate NOTIFY idempotent; enqueue unique key; digest seq order; edit rules; after key destruction (C01) a digest entry shows an erased placeholder.
- **Replay and adversarial (CI):** sanitized fixtures with a scripted `FakeScorer` behind `IRelevanceScorer`; the real adapter is tested through 05's ModelEvent seam (34). Injection cases: mention still wakes; config unchanged. Real transcripts stay in the private repo.
- **Live eval (offline) and Avalonia.Headless:** miss, false-wake and cost per unit on labelled data; digest list and deferred badge.

**Measurement.** Labels come from owner review, agent flags and outcomes (the agent replied to or acted on the message in the window). A **miss** is should-wake, deferred, and attended after 60 minutes or never. A **false wake** is should-defer, woken. With zero misses in n should-wake labels, the 95% upper bound is about 3/n: 150 labels give 2%, 300 give 1%. Promotion of the scored path [PROPOSED]: zero shadow misses for mention, DM and critical; at most 2% upper bound for scored categories. The scored wake ships only after these gates pass (C08).

## Work packages
1. Schema for the four gate tables, via the explicit migrate command (04; C44), with real-Postgres tests. Depends on implementation plan WP2.
2. Rules stage: pure function. Depends on Wave 0.
3. Scorer contract: prompt builder, closed parser, `FakeScorer`. Depends on Wave 0 and 15's reader-session primitive.
4. Scorer adapter via 05's gateway, with a gatekeeper policy entry. Depends on WP3 and implementation plan WP5.
5. Per-agent consumer: fan-out, ordering, idempotency, fallback, cap, modes. Depends on WP1 to WP4 and 06's inbox service.
6. Digest enqueue and promotion into `inbox_item`. Depends on WP5, 06 and 09.
7. Flag tools and label store. Depends on WP1 and the agent tool surface.
8. Replay and eval harness with adversarial fixtures. Depends on WP3 and WP5.
9. Digest view in Bazaar.Client.Core with an Avalonia.Headless test. Depends on WP6 and 27-client-architecture.md.
10. Run-override check for 26's Lucky controller. Depends on WP1 and WP5.

## Conflicts
Remaining (still open after DECISIONS-LOG):
1. **Call unit and savings.** Per-pair or batched cost, and whether defer-only saves turns, are arithmetic only. Re-model from shadow data (Q10).
2. **No measured miss rate.** Chat relevance has no source (research). The scored path stays off until shadow data passes the gates.
3. **Injection-forced wakes.** A scored wake lets injected text force a wake. The cap bounds this; it is not a control.
4. **Mention bypass and cap.** Bazaar.md says "unless @mentioned" with no cap; whether mention-class wakes count toward any cap is open (Q2, Q7).
5. **Ordering across items.** 06 wakes by priority; this doc needs seq order within a GC (Q5).

New, for other docs to fix:
6. **06:** still says the Skimmer "may only move an item from wake now to digest". It must add `Skimmer-promoted` (C08), adopt the single cadence (Q4), add the unique key, and state whether its 30 wakes per hour include scored wakes (Q7).
7. **09:** its `inbox_item` differs from 06's; drop its copy (C04).
8. **26:** the Lucky override must use `gate_run_override` once Q11 is answered.
9. **15:** add a quota row for the scored cap (C36).
10. **05:** the gatekeeper policy entry and Claude-lane wording must follow C14.

## Questions for the owner
1. **Component or agent?** Default: a platform component with no Soul, memory or tools. An agent identity adds a second surface.
2. **Do DMs always wake?** Default: yes, as mention-class wakes, exempt from the scored cap.
3. **Failure policy.** Default: rules only, 5-second timeout, force a wake after 60 minutes pending. Alternative: wake on everything during an outage (costlier, no latency risk).
4. **Digest cadence.** 06 says every 2 simulated working hours and at shift start; this doc said every 30 minutes. Ownership is 06's (C04). Default: 06's value, with scored promotions immediate. Alternative: 30 minutes.
5. **Catch-up on wake.** Default: seq order within a GC, priority across GCs (06), and the turn includes all pending items.
6. **Scorer model and lane.** Default: Haiku 5.5 through 05's gateway, from the API key or FreeLLMAPI free tokens by availability (C14). Alternatives: Gemini 2.5 Flash-Lite (free-tier data warning) or a CPU model after benchmarking.
7. **Scored cap.** Default: 20 scored wakes per agent per hour, inside 06's 30 total wakes per hour. Mentions exempt. Confirm both caps apply.
8. **Promotion.** Default: zero misses for mention, DM and critical; at most 2% upper bound for scored categories on at least 150 labels. Who approves moving an agent from `shadow` to `defer`: the owner, or a Council vote under C32?
9. **Reasons shown to agents?** Default: no. Reasons are logs only, since they are untrusted text. Who labels the offline set? Default: owner-approved, stored in the private repo, never the public one.
10. **Call unit.** Default: one batched call per message (about $4.39 a month on the note's formula), decided from shadow data. Alternative: per (message, agent), about $10.13.
11. **Lucky override.** Default: during a 20-minute run, each opted-in agent is exempt from the scored cap and wakes once at start, capped at 5 ideas (26).
12. **Digest quotes and erasure.** Default: store only a reference and read the quote at display time, so erasure needs no copy. Alternative: copy the excerpt encrypted under the message's per-person key (C01).
13. **Forced wakes in curfew or vacation.** Default: none. An overdue item waits for the next shift unless its owner interrupt is tagged `emergency` (C35).
