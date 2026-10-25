# Observatory, Usage and Telemetry

Status: RECONCILED with DECISIONS-LOG (2026-10-25), cross-doc pass done · Owner questions: 2
Depends on: 01-gc-data-model.md (message ids, GC types), 02-wire-protocol-and-sync.md (`presence` stream, `GET /status` format), 03-server-host-and-providers.md (cache provider, live and ready probes, host, settings registry), 04-postgres-schema-and-migrations.md (conventions, NOTIFY), 05-model-gateway.md (`gw_usage`, `gw_quota_state`), 06-agent-runtime.md (agent and turn tables, turn events), 09-work-life-simulation.md (availability, overlays, `presence.idle_label`), 12-kanban-and-dependencies.md (Kanban state changes), 14-identity-service-and-passport.md (`ISecretStore`) · Used by: 02-wire-protocol-and-sync.md (status contents), 21-github-integration.md (build health), 27-client-architecture.md, 28-windows-client.md, 29-android-client.md, 33-backup-dr-and-export.md (retention), 34-testing-strategy.md

## Purpose
The Observatory shows live what every agent is doing and which model lanes are near a limit. Usage accounting records every provider call so the dashboard and GC cost chips can show it. Telemetry gives engineers traces and metrics for debugging Bazaar itself; it is kept apart from product data.

## Decided inputs
- Resource Economy REJECTED: no money budgets. Cost is display and analysis only (Bazaar.md §4; 05; 09).
- Observatory is in v1 scope (Bazaar.md §4, 2026-10-24).
- Events serve GCs and never replace them. The Observatory is driven by Bazaar's own events and Postgres, not by telemetry (Bazaar.md §2).
- Windows start screen: unread GCs and mentions, PRs and decisions, agents and Kanban, build health and usage (Bazaar.Windows.md §1).
- Pub/sub (DECISIONS-LOG round 1, C07): GC fan-out is Postgres NOTIFY; Valkey (Linux) or Garnet (Windows) serve cache and presence. Garnet waits for spike S2. Postgres is the truth.
- Table ownership (round 1, C04; C16): this doc owns `agent_activity`, `price_table` and `usage_hour`. 06 owns `agent` and `turn`; 09 owns `availability`; 05 owns `gw_usage` and `gw_quota_state`.
- Presence: eight labels plus overlays (round 11, C43).
- Source label: every provider call carries `identity`, `native` or `pooled` (round 4, C14; CLAUDE.md 2026-10-25).
- One gateway-owned `gw_usage` table with 5-minute and 1-hour cache-write columns; `usage_record` is dropped (round 9, C33).
- Secrets: one server-side `ISecretStore` owned by 14; the Identity Service holds provider and GitHub App keys (round 2, C05).
- Status: 02 owns the format and protocol version, 03 the live and ready probes, this doc the status page contents (round 12, C40).
- Push: bare wake hints; opt-in quota alerts are the only Observatory push (round 5, C41).
- Quota state is per (provider key, model class) (05). The Work-Life Simulation is the schedule, not the rate limit (09).
- No time-based table partitioning (round 1, C10; 04). `gw_usage` and `usage_hour` are unpartitioned in v1 [PROPOSED] (04); revisit after volume is measured.

## Design

### Activity and presence [mapping PROPOSED; labels DECIDED, C43]
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
| ON_SHIFT or DRAINING | idle or drafting | `presence.idle_label` (default `working`) [PROPOSED] |

**Overlays [PROPOSED display]:** `on break` (BREAK), `waiting for approval` (turn waiting on approval), `throttled` (THROTTLED), `covered` (COVERED), `ward` (WARD, 26), and `idle` (shown beside `working` when `presence.idle_label` is `working`). An overlay is shown beside the base label. Other waits (reply, Kanban prerequisite) use the idle rule until 09 defines them [PROPOSED].

The custom-status line is an enumerated activity kind plus, when `observatory.activity_label_mode` allows it, a model-written label of at most 80 characters, passed through the scrubber in the Telemetry section.

### Event sources and subscription
- **Activity projector** (one writer per agent) consumes: availability transitions (09), turn and tool lifecycle events (06), quota changes (05), `gc` NOTIFY (04, to learn the current turn's GC) and Kanban state changes (12).
- Each accepted event upserts `agent_activity`, increments `revision`, and publishes a `presence` delta on the cache provider (C07). Events older than the last applied source time are dropped, so duplicates and reordering are harmless.
- **Clients** subscribe to `presence` (02): a snapshot of all rows, then deltas by revision. Any gap triggers a snapshot re-fetch. Presence is state, not history, so there is no replay.
- **Restart**: the projector rebuilds rows from availability and open turns. A row with no turn event for 10 minutes shows as `unknown`.

### Usage accounting
The gateway writes one `gw_usage` row per provider call (05 owns the table and its columns). This doc defines how it is read.
- Input is split into uncached input (05 names the column `input`; it excludes cache reads), `cache_read`, and cache writes split by TTL (`cache_write_5m`, `cache_write_1h`). Cache reads do not count toward input-token limits for most models, so the split keeps limit and cost views honest.
- `thinking` is a subset of `output` (provider-reported), never added to it.
- `identity` is kept on every row and on `usage_hour`, so views show which source served each call.
- Cost is computed at read time from a dated `price_table` (version, provider, model, valid range, multipliers). Repricing never edits history. A timestamp with no price row shows cost as `unknown`, never zero.
- `usage_hour` rollups (agent, provider, model, identity, hour) are rebuilt for the last two hours on each run, so late stream aborts are absorbed. Older hours are final.
- The GC cost chip joins `gw_usage` on `turn_id`. This needs `turn_id` on both `gw_usage` and the message row (contract with 01 and 06).

### Quota surfacing
Quota state comes from `gw_quota_state` (05). Its `observed` field holds the last `anthropic-ratelimit-*` remaining and limit values. A lane is **near limit** when remaining is under `observatory.near_limit_percent` of the limit on requests, input tokens or output tokens, or when its state is not OK [PROPOSED]. The state is the provider's signal. The Bazaar cost sum can differ from the provider invoice, so the dashboard never shows a spend-cap percentage.

### Telemetry [PROPOSED]
- **OpenTelemetry .NET** in Bazaar.Server.Host: traces (inbound HTTP, Npgsql, turns, gateway calls, GC appends), metrics (calls, latency, tokens by type, 429 count, quota state) and ILogger logs.
- **Export** over OTLP to the Aspire standalone dashboard on loopback. It is an engineer tool only, never a client surface. Research recommends it; it is not DECIDED.
- **Auth**: loopback with no API key by default (`telemetry.otlp_loopback_key`). Any non-loopback OTLP endpoint always requires Aspire API-key auth, or the server refuses to start with telemetry enabled. This floor is fixed, not a setting.
- **GenAI mapping**: one `ModelCallTelemetry` class is the only code that emits `gen_ai.*` names. Its attribute table is versioned and pinned to a semconv version in config. Mapping mode is `telemetry.genai_mapping`. No version is fixed yet (see Conflicts).
- **Allow-list**: an exporter processor passes only listed keys: `bazaar.agent_id`, `bazaar.gc_id`, `bazaar.provider_id`, `bazaar.model_id`, `bazaar.identity`, sizes, durations, error codes and token counts. Content capture is off and has no v1 switch (raw capture is a separate, debug-only setting below).

### Secrets, content and retention
- API keys and GitHub App keys come from 14's `ISecretStore` (C05). They are scrubbed at the HTTP handler (headers), at the exporter allow-list, and in error text, which is truncated to 200 characters after pattern redaction (provider error bodies may echo request content) [PROPOSED].
- **Content policy**: no prompt, completion, tool argument or tool output appears in telemetry or `gw_usage`. Content lives in GC messages (01). Raw provider capture is off by default (`telemetry.raw_capture`; needs counsel).
- **Retention** [PROPOSED]: telemetry in Aspire is in memory and lost on shutdown; server logs follow `logs.retention_days`; `gw_usage` and rollups are kept indefinitely (05 Q10). Backups follow 33's window. Erasure against the append-only log is OPEN and needs counsel (C01).

### Client views
Observatory and dashboard tiles share one view model in `Bazaar.Client.Core` (27):
- **Agents**: presence dot, activity line, since, GC link. Visible to the owner in v1 (Q2). DM contents never appear, only "in a DM".
- **Lanes**: provider and model class, state chip, remaining percent, until time.
- **Usage today**: calls, tokens by type, estimated cost labelled "est." (`usage.estimate_label`) for display only, with the `identity` split.
- **Build health**: last CI result per repo, from 21.
- **Android**: read-only subset; list or dashboard-only per `observatory.android_view`. No presence push; the only Observatory push is the opt-in quota alert (C41, 29).

### Status page (`GET /status`; format in 02) [PROPOSED contents]
Shows: version, protocol, uptime, readiness word from 03's `/health/ready`, provider health words, quota state words by provider display name, agent counts per presence bucket, queue depth and connected devices. Never shows agent names, GC or Vault content, key ids or workspace ids. Spend and token counts appear only if `status.show_spend_and_tokens` is set (needs counsel, because the page is public-facing).

## Settings
Registry rules are in 03. Settings may tighten floors, never loosen them.

| Key | Type | Default | Scope | Who may change | Change needs approval? |
|---|---|---|---|---|---|
| `observatory.near_limit_percent` | int 1-100 | 20 | install | operator | No |
| `observatory.activity_label_mode` | enum `kinds_only`, `kinds_and_label` | `kinds_and_label` | install | operator | No (scrubber always on) |
| `observatory.android_view` | enum `list`, `dashboard_only` | `list` | install | operator | No |
| `presence.idle_label` | enum `working`, `idle` (owned by 09) | `working` | install | operator | No |
| `usage.price_table_manual_refresh` | bool | true | install | operator | No |
| `usage.estimate_label` | bool | true | install | operator | No (off hides that totals are estimates; not recommended) |
| `telemetry.genai_mapping` | enum `bazaar_internal`, `genai_semconv` | `bazaar_internal` | install | operator | No |
| `telemetry.aspire_resume` | bool | false | install | operator (debug) | No |
| `telemetry.raw_capture` | bool | false | install | operator, time-limited | Yes, owner approval; needs counsel |
| `telemetry.otlp_loopback_key` | bool | false | install | operator | No (non-loopback key rule is fixed) |
| `logs.retention_days` | int | 14 | install | operator | Yes; needs counsel (14 is the owner-recommended default; counsel confirms it is the most protective shipped value) |
| `status.show_spend_and_tokens` | bool | false | install | operator | Yes; needs counsel (public-facing page) |

## Data and interfaces
```
-- owned by 05, read here
gw_usage(id, ts, agent_id, turn_id, provider_id, key_ref, model_id, served_model, identity,
         input, cache_write_5m, cache_write_1h, cache_read, output, thinking,
         latency_ms, status, error_code, retry_after_s, estimated, price_version)
-- owned here
price_table(version, provider_id, model_id, valid_from, valid_to, usd_per_mtok jsonb)
usage_hour(hour, agent_id, provider_id, model_id, identity, calls, input, cache_write_5m,
           cache_write_1h, cache_read, output, thinking, errors)
           PK (hour, agent_id, provider_id, model_id, identity)
agent_activity(agent_id PK, revision bigint, availability, overlay, presence, activity_kind,
               gc_id NULL, turn_id NULL, label varchar(80), since, updated_at)
               -- availability and overlay are copies of 09's row; agent_id references 06's agent
```
Indexes: `gw_usage (agent_id, ts)`, `gw_usage (turn_id)`. Enum columns are `text` with CHECK (04 conventions). `gw_usage` and `usage_hour` are unpartitioned in v1 [PROPOSED] (04).
```csharp
public static class PresenceRules { public static Presence Derive(Availability a, Overlay? o, Activity k, GcKind? gc); }
public interface IActivityProjector { ValueTask OnEventAsync(ActivityEvent e, CancellationToken ct); }
public interface ICostCalculator { CostBreakdown? Price(UsageRow u, PriceVersion v); }  // null = unknown
```

## Behaviour and edge cases
- **Ordering**: `revision` is monotonic per agent; one projector writes each agent.
- **Aborted streams**: partial usage is stored with `estimated = true`. Missing provider usage is estimated from a tokenizer and flagged the same way. Estimates are never summed silently with billed figures.
- **Over-100K prompts**: the cost chip shows a warning when input exceeds 100K tokens, because the price-step scope is unverified.
- **Pub/sub loss**: clients fall back to the snapshot; Postgres is the truth. Cache down: presence degrades, usage and GC are unaffected (03). Timestamps are UTC.
- **Privacy**: rate-limit headers carry a workspace id. It is stored internally and never leaves the server.

## Testing
- Table tests for `PresenceRules`: every row above, plus precedence (VACATION beats OFF), the `presence.idle_label` setting, and overlay display.
- Cost: fixtures with 5-minute and 1-hour writes, reads, thinking and a missing price row (expects `unknown`).
- Projector on real Postgres: scripted event sequences, duplicate and out-of-order events, restart rebuild, `unknown` after timeout.
- Rollup: a late abort inside two hours is rebuilt; older hours are unchanged; `native` and `pooled` rows stay separate.
- Quota: header sets and 429 shapes from the 05 `FakeProvider` produce the expected near-limit flags at the configured percent.
- Status page snapshot: no names, ids, or spend and tokens unless `status.show_spend_and_tokens` is set.
- Telemetry: in-memory exporter; allow-list drops unknown keys; no content keys; a planted placeholder key is absent from spans, logs, `gw_usage` and the status page.
- Loopback guard: a non-loopback endpoint without an API key refuses to start, whatever `telemetry.otlp_loopback_key` says.
- Avalonia.Headless: agent grid, lane chips and "est." chip render from fixtures; reconnect re-fetches the snapshot.

## Work packages
1. `price_table` and `usage_hour` migrations (depends on 04, 05; `gw_usage` columns come from 05).
2. Pure cost calculator and price-table loader (1).
3. `PresenceRules` and `agent_activity` migration (contracts from 06, 09).
4. Activity projector and `presence` publishing on the cache provider (3, 03).
5. Quota surfacing into `gw_quota_state` and near-limit flags (05 quota package).
6. `usage_hour` rollup job with `identity` (1, 2).
7. Telemetry bootstrap: OTel SDK, OTLP config, loopback guard, allow-list, scrubber (03).
8. `ModelCallTelemetry` mapping layer (7; waits for the semconv read).
9. Status endpoint contents (02, 03, 4, 5, 6).
10. Client view models and Headless tests (27, 4, 6).
11. Cost chip in GC message rendering (01, 2).
12. Retention jobs and the planted-secret suite (7, 1, 6).

## Conflicts
1. **GenAI semconv**: no verified release; attribute names and packages are secondary or unverified. The Aspire persistence statements disagree; the standalone page was used. The mapping layer (WP 8) waits for a read of the GenAI repo (setting `telemetry.genai_mapping`).
2. **Cost vs invoice**: the Bazaar sum can differ from the provider bill. Display only (risk, not a decision).
3. **Haiku 5.5 over-100K pricing**: whether the higher rate applies to the whole prompt is unverified, so chip cost can be wrong for large prompts.
4. **Spend-cap distance**: showing distance to the tier's monthly cap would reintroduce a money budget. Not shown (Q1).
5. **For 05**: name the uncached input column the same way here (`input`) and state in its text that it excludes cache reads. 05 already carries `identity` and `price_version`.

## Questions for the owner
1. **Spend against the tier cap** on the dashboard? Default: no; month-to-date estimated cost as a number only. (Touches the REJECTED Resource Economy, so it stays a question.)
2. **Observatory audience**: owner-only in v1, or future human members too? Default: owner-only.

MOVED TO SETTINGS: observatory.near_limit_percent, observatory.activity_label_mode, observatory.android_view, presence.idle_label, usage.price_table_manual_refresh, usage.estimate_label, telemetry.genai_mapping, telemetry.aspire_resume, telemetry.raw_capture, telemetry.otlp_loopback_key, logs.retention_days, status.show_spend_and_tokens (DECISIONS-LOG round 13)
