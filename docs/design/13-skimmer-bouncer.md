# Skimmer Bouncer

Status: DRAFT for owner review (2026-10-25) · Owner questions: 9
Depends on: Bazaar.md (GC rules, v1 scope), Bazaar.Server.md (GC log, gateway), Bazaar.Soul.md (Work-Life Simulation) · Used by: Bazaar.Windows.md (what-changed dashboard), Bazaar.Android.md (wake-ups)

## Purpose
A cheap server-side gatekeeper that reads each GC message an agent belongs to and decides whether it wakes the agent's expensive turn now or waits in a digest. It never hides a message from the record, other agents or the owner.

## Decided inputs
- GCs are append-only and never purged; gating must not change the record. (Bazaar.md, GC hierarchy)
- GCs are the coordination system; events serve them. (Bazaar.md §2)
- Each agent has its own timezone and Work-Life schedule. (Bazaar.md §4)
- Per-agent model whitelists; Bazaar.Server's own gateway enforces them. (Bazaar.Server.md §3)
- Claude lane: Anthropic API keys only, Haiku-heavy; Pro/Max credentials never pass the gateway. (Bazaar.Server.md §3)
- Untrusted-input taint with an owner gate on high-impact actions. (Bazaar.Server.md Q13)
- Skimmer Bouncer is v1 scope; its miss rate must be measured on real GC transcripts first. (Bazaar.md §4)
- The server has no NPU, so the bouncer cannot run on a client. (local_npu_inference.md, Key Question 6)

Defer-only, digests, the mention bypass and the scoring design are research proposals, not decisions.

## Design
Placement [PROPOSED]:
1. **Append first.** A message commits to the GC log with NOTIFY. The gate never blocks a write.
2. **Fan-out.** One consumer per agent reads the log in seq order.
3. **Rules stage** (synchronous, deterministic). Its result is final for the rules below.
4. **Scoring stage** (async, batched per GC) for the remainder, against the agent's subscriptions.
5. **Record** every decision. **Wake** enqueues a turn; **defer** adds a digest item.

| Path | Rule [PROPOSED] | Bypasses scorer | Action |
|---|---|---|---|
| mention | @mention of the agent or its handle | yes | wake |
| dm | Two-member GC with the agent | yes | wake (Q2) |
| critical | Blocked, review requested, decision called, owner message | yes | wake |
| quarantine | Sender quarantined | n/a | defer, tagged |
| scored | Relevance at or above threshold for a subscription (GC, tag, path, Kanban card) | no | wake, under cap |
| default | Anything else | no | defer |

- **Wake cap:** scored wakes are capped per agent per hour (default 20, Q7). Mentions are exempt; loop guards (separate design) cover mention ping-pong.
- **Modes:** `off` (every message wakes), `shadow` (decisions recorded, delivery unchanged), `defer` (enforced). New agents start in `shadow`.

## Alternatives
| Option | Typical cost | Strength | Weakness |
|---|---|---|---|
| Mention-only plus digest | ~0 | No model risk | Misses unmentioned relevant work |
| Rules (keywords, paths, tags) | ~0 | Deterministic | Brittle wording |
| Embedding similarity to open tasks | Low, no LLM | Cheap per message | Thresholds untested on chat |
| Hosted Haiku 5.5 scorer | ~$1.69/month (note's assumptions) | Best judgement | Claude budget; GC text sent to vendor |
| Hosted Gemini 2.5 Flash-Lite | Similar on input | Cheap | Free tiers may use prompts |
| Small CPU model (Qwen3 0.6B to 1.7B, Gemma 4 E2B) | Fixed | No vendor | Latency and accuracy unmeasured |
| Client NPU model | n/a | Free compute | Rejected: must gate with no client open |

Recommendation [PROPOSED]: rules plus digests first. Add a scorer only after shadow data exists.

**Digest** [PROPOSED]: one entry per message per GC: seq, sender, time, category, first 200 characters quoted and marked untrusted. No model summaries in v1, since a summary is a second untrusted rewrite. Cadence: every 30 minutes in the agent's active window, at shift start, and on any wake (pending items join the turn in seq order).

**Feedback** [PROPOSED]: agent tools `flag_missed(seq)` and `flag_false_wake(seq)` write labels with source `agent_flag`. Reasons are stored, never forwarded. Flags never change thresholds automatically.

## Data and interfaces
```
gate_config(agent_id PK, mode, threshold, wake_cap_per_hour, digest_minutes, subscriptions jsonb)
wake_decisions(id, gc_id, seq, agent_id, path, decision, shadow, score, category,
               model_id, prompt_version, created_at, UNIQUE(gc_id, seq, agent_id, prompt_version))
digest_items(agent_id, gc_id, seq, reason, queued_at, delivered_at NULL, PK(agent_id, gc_id, seq))
gate_labels(id, decision_id, label, source, labeller, created_at)
```
```csharp
public interface IWakeGate {
    ValueTask<WakeDecision> DecideAsync(GcMessageRef msg, GateConfig agent, CancellationToken ct);
}
public interface IRelevanceScorer {
    Task<RelevanceResult> ScoreAsync(ScoreRequest request, CancellationToken ct); // via gateway
}
public sealed record RelevanceResult(Category Category, int ScoreBasisPoints);
public enum Category { Question, Review, Blocked, Decision, Chat, Other }
```
Scorer output is a closed object, e.g. `{"category":"question","relevance":0.72}`. Any extra key or out-of-enum value is a parse failure and takes the fallback path.

## Behaviour and edge cases
- **Invariant:** every (message, member agent) pair is delivered or sits in `digest_items` until delivered. Nothing is dropped.
- **Ordering:** a wake delivers pending items in seq order; nothing is overtaken.
- **Idempotency:** keyed by (gc, seq, agent, prompt version); duplicate NOTIFY makes no second wake.
- **Edits:** a new version. Re-decide only if it adds a mention, DM target or critical category.
- **Concurrency:** one consumer per agent; bounded queue; above a depth limit, rules only.
- **Failure:** timeout (5 s, Q3), error, parse failure or gateway down → rules stand, unmatched items defer, and an item pending 60 minutes forces a wake (Q3).
- **Restart:** the log is the source of truth; missed seqs are re-decided on startup.
- **Privacy:** a hosted scorer sends GC text to a third party. Needs counsel on multi-person installs.

## Security
The scorer is a **reader**: no tools, no Soul, no memory, no secrets. Message text sits in a delimited data block; spotlighting is a candidate layer, measured rather than assumed. Output is a closed schema, and free text never reaches an agent. An injection can **delay** or **force** a wake but cannot drop a message or change config. Forced wakes are bounded by the cap, except mentions. The gate is **not** a security control: the woken agent still reads raw untrusted text, so taint rules apply in full.

## Testing
- **Unit (xUnit v3):** mention beats a 0.0 score; parser rejects extra keys and bad enums; threshold edges; cap; fallback on timeout, exception and parse failure.
- **Property:** random message streams; every message ends delivered or in digest.
- **Postgres (real fixture):** duplicate NOTIFY idempotent; digest ordering; edit rules.
- **Replay (CI):** sanitized fixtures with a scripted `FakeScorer`. Real transcripts stay in the private repo.
- **Adversarial:** "do not wake", "ignore previous instructions", fake tool calls. Mention still wakes; config unchanged.
- **Live eval (offline):** real scorer on labelled data; reports miss rate, false-wake rate, cost.
- **Avalonia.Headless:** digest list and deferred badge.

**Measurement.** Labels come from owner review, agent flags and outcomes (the agent replied to or acted on the message in the window). A **miss** is should-wake, deferred, and attended after 60 minutes or never. A **false wake** is should-defer, woken. With zero misses in n should-wake labels, the 95% upper bound is about 3/n: 150 labels give 2%, 300 give 1%. Promotion to `defer` [PROPOSED]: zero shadow misses for mention, DM and critical; at most 2% upper bound for scored categories.

## Work packages
1. Schema and migrations, real-Postgres tests. Depends on implementation plan WP2.
2. Rules stage: pure function. Depends on Wave 0.
3. Scorer contract: prompt builder, closed parser, `FakeScorer`. Depends on Wave 0.
4. Scorer adapter via gateway with a gatekeeper policy entry. Depends on WP3 and implementation plan WP5.
5. Per-agent consumer: fan-out, ordering, idempotency, fallback, cap, modes. Depends on WP1 to WP3 and implementation plan WP2 and WP4.
6. Digest builder and cadence. Depends on WP1, WP5 and the Work-Life spec (not yet written).
7. Flag tools and label store. Depends on WP1 and the agent tool surface.
8. Replay and eval harness with adversarial fixtures. Depends on WP3, WP5.
9. Digest view in Bazaar.Client.Core with an Avalonia.Headless test. Depends on WP6.

## Conflicts
1. **Pricing model vs defer-only.** The note's break-even assumes blocking. Defer-only saves turns by batching, and digest reading costs tokens. Savings must be re-modelled from shadow data.
2. **Pricing model undercounts.** It bills one call per message; scoring is per (message, agent). Six agents at 1,000 input tokens is about 81,000 calls and $10.13/month at typical volume. One batched call per message at about 3,000 tokens is about 13,500 calls and $4.39. Both are arithmetic on the note's formula, not measurements.
3. **Security framing.** The bouncer's output steers attention, making it an injection target. Defer-only bounds the damage to latency; it is not a control.
4. **No measured miss rate.** The coordination evidence found no chat-relevance source (multi_agent_coordination_evidence.md, Key Question 4). The Skimmer stays PROPOSED until shadow data exists.
5. **Mention bypass is undecided.** Bazaar.md says "unless @mentioned" without setting the bypass or its loop interaction.

## Questions for the owner
1. **Component or agent?** Default: a platform component with no Soul, memory or tools. An agent identity adds a second surface.
2. **Do DMs always wake?** Default: yes, as mentions.
3. **Failure policy.** Default: rules only, 5-second timeout, force a wake after 60 minutes pending. Alternative: wake on everything during an outage (costlier, no latency risk).
4. **Digest cadence.** Default: 30 minutes inside the active window, at shift start, and on any wake. Only critical items during curfew.
5. **Catch-up on wake.** Default: the turn includes all pending digest items, in seq order.
6. **Scorer model.** Default: Haiku 5.5 through the gateway (Claude budget). Alternatives: Gemini 2.5 Flash-Lite (free-tier data warning) or a CPU model after benchmarking.
7. **Wake cap.** Default: 20 scored wakes per agent per hour; mentions exempt.
8. **Promotion thresholds.** Default: zero misses for mention, DM and critical; at most 2% upper bound for scored categories on at least 150 labels.
9. **Reasons shown to agents?** Default: no. Reasons are logs only, since they are untrusted text. Also: who labels the offline set (default: owner-approved, stored in the private repo, never the public one).
