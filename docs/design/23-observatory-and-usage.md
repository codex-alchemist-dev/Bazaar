# Observatory, Usage and Telemetry

Status: DRAFT for owner review (2026-10-25) · Owner questions: 14
Depends on: 01-gc-data-model.md (message ids), 02-wire-protocol-and-sync.md (`presence` stream, status endpoint), 03-server-host-and-providers.md (host, pub/sub provider, listeners), 04-postgres-schema-and-migrations.md (NOTIFY, partitioning), 05-model-gateway.md (`gw_usage`, `gw_quota_state`), 06-agent-runtime.md (turn events), 09-work-life-simulation.md (availability) · Used by: 21-github-integration.md (build health), 27-client-architecture.md, 28-windows-client.md (start screen), 29-android-client.md, 33-backup-dr-and-export.md (retention), 34-testing-strategy.md

## Purpose
The Observatory shows live what every agent is doing and which model lanes are near a limit. Usage accounting records every provider call so the dashboard and GC cost chips can show it. Telemetry gives engineers traces and metrics for debugging Bazaar itself; it is kept apart from product data.

## Decided inputs
- Resource Economy REJECTED: no money budgets. Cost is display and analysis only (Bazaar.md §4; 05; 09).
- Observatory is in v1 scope (Bazaar.md §4, 2026-10-24).
- Events serve GCs and never replace them. The Observatory is driven by Bazaar's own events and Postgres, not by telemetry (Bazaar.md §2).
- Windows start screen: unread GCs and mentions, PRs and decisions, agents and Kanban, build health and usage (Bazaar.Windows.md §1).
- Postgres holds state. Pub/sub is Valkey on Linux or Garnet on Windows behind one provider (Bazaar.Server.md §3).
- Quota state is per (provider key, model class) (05). The Work-Life Simulation is the schedule, not the rate limit (09).

## Design

### Activity and presence [PROPOSED]
Each agent has two inputs. **Availability** comes from 09 (ON_SHIFT, DRAINING, BREAK, OFF, VACATION, with overlays THROTTLED and COVERED). **Activity** comes from the current turn in 06: `idle`, `model_call`, `tool`, `waiting` (reply, approval or Kanban prerequisite), `drafting`. Presence is a pure function of both:

| Availability | Current activity | Presence label |
|---|---|---|
| VACATION | any | on vacation |
| OFF or BREAK | idle | sleeping |
| ON_SHIFT or DRAINING | turn in a Council or vote GC | in meeting |
| ON_SHIFT or DRAINING | tool, research tool class | researching |
| ON_SHIFT or DRAINING | tool, review tool class | reviewing |
| ON_SHIFT or DRAINING | model_call | thinking |
| ON_SHIFT or DRAINING | tool, edit or build class | working |
| ON_SHIFT or DRAINING | turn in another project's GC | helping another project |

The custom-status line is an enumerated activity kind plus a model-written label of at most 80 characters, passed through the scrubber in the Telemetry section.

### Event sources and subscription
- **Activity projector** (one writer per agent) consumes: availability transitions (09), turn and tool lifecycle events (06), quota changes (05), `gc` NOTIFY (04, to learn the current turn's GC) and Kanban state changes (12).
- Each accepted event upserts `agent_activity`, increments `revision`, and publishes a `presence` delta. Events older than the last applied source time are dropped, so duplicates and reordering are harmless.
- **Clients** subscribe to `presence` (02): a snapshot of all rows, then deltas by revision. Any gap triggers a snapshot re-fetch. Presence is state, not history, so there is no replay.
- **Restart**: the projector rebuilds rows from availability and open turns. A row with no turn event for 10 minutes shows as `unknown`.

### Usage accounting
The gateway writes one `gw_usage` row per provider call (05). This doc defines how it is read.
- Store input as three fields: `input_uncached`, `cache_read` and cache writes split by TTL (`cache_write_5m`, `cache_write_1h`). Cache reads do not count toward input-token limits for most models, so the split keeps limit and cost views honest.
- `thinking` is a subset of `output` (provider-reported), never added to it.
- Cost is computed at read time from a dated `price_table` (version, provider, model, valid range, multipliers). Repricing never edits history. A timestamp with no price row shows cost as `unknown`, never zero.
- `usage_hour` rollups (agent, provider, model, hour) are rebuilt for the last two hours on each run, so late stream aborts are absorbed. Older hours are final.
- The GC cost chip joins `gw_usage` on `turn_id`. This needs `turn_id` on both `gw_usage` and the message row (contract with 01 and 06).

### Quota surfacing
Quota state comes from `gw_quota_state` (05). Its `observed` field holds the last `anthropic-ratelimit-*` remaining and limit values. A lane is **near limit** [PROPOSED] when remaining is under 20% of the limit on requests, input tokens or output tokens, or when its state is not OK. The state is the provider's signal. The Bazaar cost sum can differ from the provider invoice (estimates, other clients on the same key), so the dashboard never shows a spend-cap percentage.

### Telemetry [PROPOSED]
- **OpenTelemetry .NET** in Bazaar.Server.Host: traces (inbound HTTP, Npgsql, turns, gateway calls, GC appends), metrics (calls, latency, tokens by type, 429 count, quota state) and ILogger logs.
- **Export** over OTLP to the Aspire standalone dashboard on loopback. It is an engineer tool only, never a client surface. Research recommends it; it is not DECIDED.
- **Auth**: loopback with no API key by default. Any non-loopback OTLP endpoint requires Aspire API-key auth, or the server refuses to start with telemetry enabled.
- **GenAI mapping**: one `ModelCallTelemetry` class is the only code that emits `gen_ai.*` names. Its attribute table is versioned and pinned to a semconv version in config. The version is not yet fixed: the GenAI conventions are Development status and no release was verified (see Conflicts).
- **Allow-list**: an exporter processor passes only listed keys: `bazaar.agent_id`, `bazaar.gc_id`, `bazaar.provider_id`, `bazaar.model_id`, sizes, durations, error codes and token counts. Content capture is off and has no v1 switch.

### Secrets, content and retention
- API keys and GitHub App keys come from `ISecretStore` (05 Q7). They are scrubbed at the HTTP handler (headers), at the exporter allow-list, and in error text, which is truncated to 200 characters after pattern redaction (provider error bodies may echo request content) [PROPOSED].
- **Content policy**: no prompt, completion, tool argument or tool output appears in telemetry or `gw_usage`. Content lives in GC messages (01). Raw provider capture for debugging is off (Q8).
- **Retention** [PROPOSED, owner decides]: telemetry in Aspire is in memory and lost on shutdown; server logs 14 days; `gw_usage` and rollups indefinite (05 Q12). Erasure against the append-only log is OPEN; needs counsel.

### Client views
Observatory and dashboard tiles share one view model in Client.Core (27):
- **Agents**: presence dot, activity line, since, GC link. Owner-only in v1. DM contents never appear, only "in a DM".
- **Lanes**: provider and model class, state chip, remaining percent, until time.
- **Usage today**: calls, tokens by type, estimated cost labelled "est." for display only.
- **Build health**: last CI result per repo, from 21.
- **Android**: read-only subset; push only on capacity or quota changes (29 decides).

### Status page (`GET /status`, 02) [PROPOSED]
Shows: version, protocol, uptime, `/healthz`, provider health words, quota state words by provider display name, agent counts per presence bucket, queue depth and connected devices. Never shows agent names, GC or Vault content, spend, token counts, key ids or workspace ids.

## Data and interfaces
```
gw_usage(usage_id uuid, ts, agent_id, turn_id, provider_id, key_ref, model_id, served_model,
         input_uncached, cache_write_5m, cache_write_1h, cache_read, output, thinking,
         latency_ms, status, error_code, retry_after_s, estimated, created_at)  -- monthly partitions
price_table(version, provider_id, model_id, valid_from, valid_to, usd_per_mtok jsonb)
usage_hour(hour, agent_id, provider_id, model_id, calls, input_uncached, cache_write_5m,
           cache_write_1h, cache_read, output, thinking, errors)  PK (hour, agent_id, provider_id, model_id)
agent_activity(agent_id PK, revision bigint, availability, overlay, presence, activity_kind,
               gc_id NULL, turn_id NULL, label varchar(80), since, updated_at)
```
Indexes: `gw_usage (agent_id, ts)`, `gw_usage (turn_id)`. Enum columns are `text` with CHECK (04 conventions).
```csharp
public static class PresenceRules { public static Presence Derive(Availability a, Overlay? o, Activity k, GcKind? gc); }
public interface IActivityProjector { ValueTask OnEventAsync(ActivityEvent e, CancellationToken ct); }
public interface ICostCalculator { CostBreakdown? Price(UsageRow u, PriceVersion v); }  // null = unknown
```

## Behaviour and edge cases
- **Ordering**: `revision` is monotonic per agent; one projector writes each agent.
- **Aborted streams**: partial usage is stored with `estimated = true`. Missing provider usage is estimated from a tokenizer and flagged the same way. Estimates are never summed silently with billed figures.
- **Over-100K prompts**: the cost chip shows a warning when input exceeds 100K tokens, because the price-step scope is unverified.
- **Pub/sub loss**: clients fall back to the snapshot; Postgres is the truth. Timestamps are UTC.
- **Privacy**: rate-limit headers carry a workspace id. It is stored internally and never leaves the server.

## Testing
- Table tests for `PresenceRules`: every row above, plus precedence (VACATION beats OFF).
- Cost: fixtures with 5-minute and 1-hour writes, reads, thinking and a missing price row (expects `unknown`).
- Projector on real Postgres: scripted event sequences, duplicate and out-of-order events, restart rebuild, `unknown` after timeout.
- Rollup: a late abort inside two hours is rebuilt; older hours are unchanged.
- Quota: header sets and 429 shapes from the 05 `FakeProvider` produce the expected near-limit flags.
- Status page snapshot: no names, spend, tokens or ids.
- Telemetry: in-memory exporter; allow-list drops unknown keys; no content keys; a planted placeholder key is absent from spans, logs, `gw_usage` and the status page.
- Loopback guard: a non-loopback endpoint without an API key refuses to start.
- Avalonia.Headless: agent grid, lane chips and "est." chip render from fixtures; reconnect re-fetches the snapshot.

## Work packages
1. `gw_usage` split-cache columns, `price_table` migration (depends on 04, 05).
2. Pure cost calculator and price-table loader (1).
3. `PresenceRules` and `agent_activity` migration (contracts from 06, 09).
4. Activity projector and `presence` publishing (3).
5. Quota surfacing into `gw_quota_state` and near-limit flags (05 quota package).
6. `usage_hour` rollup job (1, 2).
7. Telemetry bootstrap: OTel SDK, OTLP config, loopback guard, allow-list, scrubber (03).
8. `ModelCallTelemetry` mapping layer (7).
9. Status endpoint contents (03, 4, 5, 6).
10. Client view models and Headless tests (27, 4, 6).
11. Cost chip in GC message rendering (01, 2).
12. Retention jobs and the planted-secret suite (7, 1, 6).

## Conflicts
1. **04 `usage_record`** holds token counts only, with no cache split or thinking. 05 defines `gw_usage`. Proposed: one table, `gw_usage`, written by 05. 04 drops `usage_record`.
2. **05 `cache_create`** is one column. Anthropic prices 5-minute writes at 1.25x and 1-hour writes at 2x base (pricing note), so one column cannot price correctly. Split it in 05.
3. **04 `agent.status`** would mix availability with activity. Keep availability on `agent` and activity in `agent_activity`.
4. **Presence labels** come from the work order, not Bazaar.md (09 Q5). Proposal only.
5. **GenAI semconv**: no verified release, attribute names and packages are secondary or unverified. The Aspire persistence statements disagree; the standalone page was used. The mapping layer waits for a read of the GenAI repo.
6. **Cost vs invoice**: the Bazaar sum can differ from the provider bill. Display only.
7. **Haiku 5.5 over-100K pricing**: whether the higher rate applies to the whole prompt is unverified, so chip cost can be wrong for large prompts.
8. **Spend-cap distance**: showing distance to the tier's monthly cap would reintroduce a money budget. Not shown (Q3).
9. **06 Depends list** uses placeholder file names (`03-model-gateway`, `04-soul-memory`) that do not match the index. Fix in 06.

## Questions for the owner
1. **Semconv pin**: ship with Bazaar-internal names until the GenAI repo is read, then map? Default: yes.
2. **Near-limit threshold**: 20% remaining, or a per-lane setting? Default: 20%, global.
3. **Spend against the tier cap** on the dashboard? Default: no, month-to-date estimated cost as a number only.
4. **Price table**: shipped and dated with each release, with a manual refresh? Default: yes.
5. **Estimated cost chips**: marked "est."? Default: yes.
6. **Aspire mode**: in-memory only, or `Resume` for debugging? Default: in-memory; `Resume` by a debug flag.
7. **Server log retention**: 14 days? Default: 14.
8. **Raw provider capture** for debugging, local and time-limited? Default: off.
9. **Observatory audience**: owner-only in v1, or future human members too? Default: owner-only.
10. **Presence labels**: adopt the eight from the work order into Bazaar.md? Default: yes.
11. **Activity label**: model-written up to 80 characters, or only the enumerated kinds? Default: both, scrubbed.
12. **Status page spend and tokens**: show neither? Default: neither.
13. **OTLP auth on loopback**: no key by default? Default: no key on loopback, key required otherwise.
14. **Android Observatory**: a read-only list, or dashboard only? Default: read-only list.
