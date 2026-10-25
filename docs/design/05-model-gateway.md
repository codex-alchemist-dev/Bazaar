# Model Gateway (Bazaar.Server's own)

Status: RECONCILED with DECISIONS-LOG (2026-10-25) · Owner questions: 14
Depends on: 04-postgres-schema-and-migrations.md (conventions, migrations), 14-identity-service-and-passport.md (`ISecretStore`), 15-security-taint-and-approvals.md (shared quota table). Used by: 06-agent-runtime.md, 07-soul-system.md, 08-memory-system.md, 09-work-life-simulation.md, 13-skimmer-bouncer.md, 17-coding-agent-harness.md, 23-observatory-and-usage.md, 34-testing-strategy.md, 35-spikes-plan.md.

## Purpose
The single path through which every agent model call leaves Bazaar.Server. It enforces per-agent model policy itself, treats every provider as an interchangeable upstream, tracks quota and usage per provider key, and hands quota facts to the Work-Life Simulation. No upstream is trusted to enforce policy.

## Decided inputs
- Bazaar.Server has its own gateway that enforces per-agent policy. No global `auto/*` (CLAUDE.md; Bazaar.Server.md §3).
- The gateway layer is pluggable per install. FreeLLMAPI has priority support: guided setup, usage and health on the dashboard, a default free-lane preset, manual and custom dynamic routing. OmniRoute, direct provider keys and custom proxies are supported too. Operators may add reverse proxies and local runtimes (Bazaar.md §3).
- **Claude lane (DECIDED round 4, C14):** the Claude member uses both the owner's Anthropic API key and FreeLLMAPI's free Claude tokens, by availability. This supersedes "Anthropic API keys only" in CLAUDE.md. Usage and cost records keep an `identity` label, `native` or `pooled`. Pro/Max and other subscription logins stay off every route.
- Haiku-heavy budget for the Claude lane (CLAUDE.md).
- Secrets: provider records hold references only. Resolution goes through doc 14's server-side `ISecretStore`, which holds provider keys from the start (C05, round 2).
- Quota state and model policy are owned here (C04). Shared rate buckets live in doc 15's table (C36); this doc owns provider and model limits only.
- `gw_usage` is the one usage table (C33). `ModelEvent` is the model-call seam, with an `IChatClient` adapter shipped in v1 (C29).
- One embedding profile per install, chosen at setup, stored as `halfvec` (C39).
- Money budgets are REJECTED; the cost chip is display only (CLAUDE.md, Resource Economy).
- Nothing here may require a paid component: the signed build is OSI-only (C23).
- Free-tier data exposure is the operator's choice; setup shows a warning.
- Raw Npgsql, SQL-first; xUnit v3; no Protobuf tooling yet.

## Design
**Pipeline per call** [PROPOSED]: the agent runtime submits `ModelRequest` (agent id, route or model id, messages, tools) → resolve the route to candidates → policy check on every candidate → drop unhealthy or quota-blocked candidates → call via adapter → classify the outcome → fall back only to the next policy-allowed candidate → write one `gw_usage` row → emit quota events. Embedding calls use the same pipeline with an embedding model class.

**Provider kinds** [PROPOSED]: `first_party`, `aggregator`, `gateway` (FreeLLMAPI, OmniRoute, LiteLLM), `reverse_proxy` (any OpenAI-compatible URL), `local` (Ollama, LM Studio, llama.cpp, vLLM). Each is a `gw_provider` record plus one adapter.

**Adapters** (DECIDED families): `openai_chat` (default), `anthropic_messages`, `gemini_generate_content`. The Anthropic and Gemini OpenAI shims are not used: they drop caching, strict tools and thinking. Ollama's native API is optional, later. Adapters translate to and from `ModelEvent` (`TextDelta`, `ThinkingDelta`, `ToolCallStart/ArgsDelta/End`, `Usage`, `Done(stopReason)`, `Error`); differences stay at the adapter edge.

**IChatClient adapter (v1, first wave).** A thin `IChatClient` (Microsoft.Extensions.AI) implementation maps `ModelEvent` to and from the gateway, so Microsoft libraries can plug in. Tests in docs 06, 17 and 34 may use it.

**Policy** [PROPOSED]. Deny by default. Entries are provider-qualified (`anthropic-direct:claude-haiku-5-5`) or a named route, and are checked at every attempt, including fallbacks. Stored in Postgres and edited through the admin panel. Who may edit is open (Question 5, Conflicts 2). Until it is answered, no agent edits policy. New catalogue models never join a whitelist automatically.

**Routes and fallback.** A route is an ordered candidate set with a strategy (`priority`, `round_robin`, `least_loaded`). Fallback sets are per route and intersected with the agent's policy. A fallback never leaves the agent's allowed set or its model-family class.

**Delegated routes (`custom/*`).** When FreeLLMAPI is installed, Charles and the junior swarm default to a `custom/*` route that resolves to a FreeLLMAPI custom-mode profile (automatic switching over a whitelist of cheaper code-capable models). FreeLLMAPI picks the model, so Bazaar cannot block a wrong pick before the call. [PROPOSED] mitigation: (1) the route declares an expected member set; (2) guided setup tells the operator to configure the same set in FreeLLMAPI; (3) Bazaar checks the served model and `X-Fallback-Trail` against the set after every call; (4) a violation suspends the route and raises an admin alert. Without FreeLLMAPI, Charles and the juniors use an explicit pinned cheap whitelist.

**Claude lane.** Candidates: `anthropic-direct` (identity `native`, the owner's key) and FreeLLMAPI's Claude models (identity `pooled`). [PROPOSED] order: native first; pooled when native is `RateLimited`, `SpendCapped` or `Down`, or when no native key is configured (Question 3). Every FreeLLMAPI Claude entry is `pooled`; no probe promotes it to `native`. Subscription-backed routes are excluded: no Claude Code tier or subscription login can be configured or accepted, credential kinds are `api_key`, `bearer` and `none` only, and the expected-set audit flags any served route outside the set. Recorded caveats are in Conflicts 1.

**Quota and the vacation simulation.** State is tracked per (provider key, model class), never per agent, because agents share one key.

| State | Trigger | Retry | Source |
|---|---|---|---|
| `Available` | default | n/a | n/a |
| `RateLimited(until)` | 429 with `retry-after`, or header `reset` | after `until` | Anthropic headers verified |
| `SpendCapped(until)` | 429 `enforced_spend_limit_reached`, no `retry-after` | none until 00:00 UTC on the 1st | Anthropic, verified |
| `DailyExhausted(until)` | local counter hit a published RPD/TPD | reset time per provider | limits per catalogue; reset times unverified |
| `Down` | breaker open | half-open probe | passive plus active health |

A 429 with no `retry-after` and no spend-cap code gets jittered backoff. The gateway emits `AgentCapacityChanged(agentId, available, until, reason)` when all of an agent's allowed candidates are blocked. The Work-Life Simulation (doc 09) decides whether that becomes "on vacation"; the gateway reports facts only.

**Health.** Active probes use cheap list endpoints, never paid completions. Passive health comes from real-call errors.

**Secrets.** Provider records hold `secret://bazaar/providers/<id>/<name>` references only, resolved by doc 14's `ISecretStore`. Keys are scrubbed from logs and spans, with a planted-key test.

**FreeLLMAPI guided setup** [PROPOSED]:
1. "Connect existing". Bazaar links the install docs and does not run the `curl | bash` installer.
2. Warnings, each acknowledged and stored with a timestamp: single-user, trusted-network, personal-experimentation scope; some free tiers train on prompts; provider terms are the operator's duty; pin the version, keep the `127.0.0.1` bind, back up the database and `ENCRYPTION_KEY`; no subscription credentials.
3. Base URL (`http://localhost:3001/v1` default) and the unified key, stored as a secret reference.
4. Probe `/v1/models`, `/v1/usage`, then one test message. Show the exact URLs used.
5. Pick the free-lane preset: tool-capable models from the list, ticked by the operator.
6. Pick routing: manual (Bazaar pins model ids) or custom (`custom/*`, above).
7. Self-test: compare served models with the expected set.

Dashboard: `/v1/usage` monthly requests and tokens by platform, probe latency, error and fallback rates, expected-set violations.

**Reverse proxies and local runtimes.** Add-provider form: base URL, auth, extra headers, body include/exclude lists, model-list probe, test message, final request URL shown, validation on save. Local runtimes: `none` auth, low concurrency cap, 60-second health. "Base URL override" and "Outbound network proxy" are separate concepts; `socks5` waits for the .NET 10 spike. Outbound proxy and multi-key rotation exist in the schema only and are hidden in the UI (Question 6).

## Data and interfaces
```csharp
interface IModelAdapter {
  AdapterKind Kind { get; }
  IAsyncEnumerable<ModelEvent> StreamAsync(ProviderEndpoint ep, ModelRequest req, CancellationToken ct);
  Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ProviderEndpoint ep, CancellationToken ct);
  Task<HealthResult> ProbeAsync(ProviderEndpoint ep, CancellationToken ct);
  RateLimitObservation? Observe(HttpResponseMessage r);
}
interface IModelGateway { IAsyncEnumerable<ModelEvent> CallAsync(ModelRequest r, CancellationToken ct); }
// IChatClient adapter over IModelGateway: v1, first wave.
```

Tables [PROPOSED]. Doc 05 owns these six; agent and turn ids reference doc 06's tables.
- `gw_provider(id, kind, adapter, base_url, auth_kind, secret_ref, headers, proxy_ref, terms_ack_at, enabled)`
- `gw_model(provider_id, model_id, family, identity native|pooled, caps jsonb, status, first_seen, last_seen, source)`
- `gw_route(id, strategy, members jsonb, expected_set jsonb)`
- `agent_model_policy(agent_id, allowed jsonb, default_route, version, changed_by, changed_at)`
- `gw_quota_state(provider_id, key_ref, model_class, state, until, observed jsonb)`
- `gw_usage(id, ts, agent_id, turn_id, provider_id, key_ref, model_id, served_model, identity, input, cache_write_5m, cache_write_1h, cache_read, output, thinking, latency_ms, status, error_code, retry_after_s, estimated, price_version)`. `thinking` is a subset of `output`. The two cache-write columns are priced separately (1.25x and 2x multipliers, verified). `usage_record` is dropped (C33). Cost is computed from a dated price table, not stored as truth. No prompt or completion text is stored.

**Default catalogue** (shipped as a secret-free file; each entry carries `freeTier`, `termsRisk`, `trainsOnFreeData` and `verification` fields. V = primary page read; P = partly; U = unverified; re-check raw pages before release):

| Source | Adapter | Base URL | Status |
|---|---|---|---|
| Anthropic | anthropic_messages | `https://api.anthropic.com` | P: limits and prices V; native path carried, not re-read |
| Google Gemini API | gemini_generate_content | `https://generativelanguage.googleapis.com/v1beta` | P: native path and free limits not read |
| OpenAI | openai_chat | `https://api.openai.com/v1` | U: free-tier claim needs a raw check |
| OpenRouter | openai_chat | `https://openrouter.ai/api/v1` | P: limits and `/key` V; chat path U |
| Hugging Face router | openai_chat | `https://router.huggingface.co/v1` | V |
| Cerebras | openai_chat | `https://api.cerebras.ai/v1` | P: limits V; base is an assumption |
| Groq, NVIDIA NIM, Mistral, xAI | openai_chat | to confirm | U: NIM's ~40 RPM is forum-only |
| Cloudflare Workers AI | openai_chat | account-specific | P: 10,000 neurons/day V; endpoint U |
| DeepSeek | openai_chat | to confirm | P: prices V; `deepseek-v4-flash` is retired |
| FreeLLMAPI (gateway) | openai_chat | `http://localhost:3001/v1` | P: API reference and MIT V; code unread |
| OmniRoute (gateway) | openai_chat | operator-set | U |
| LiteLLM (gateway) | openai_chat | operator-set | V: OpenAI-compatible, MIT |
| Ollama | openai_chat | `http://localhost:11434/v1` | V (2024 post) |
| LM Studio / llama.cpp | openai_chat | `http://127.0.0.1:1234/v1` / `:8080/v1` | P: OpenCode docs only |
| vLLM, custom OpenAI-compatible | openai_chat | operator-set | U / n/a |

Not shipped: GitHub Models (retired 2026-07-30), and any Copilot, Claude Pro/Max, ChatGPT or Gemini CLI subscription login.

## Behaviour and edge cases
- Streaming: usage may arrive only at stream end. On abort, record partial usage with `estimated=true`. No retry or fallback once any event reached the agent; a mid-stream failure surfaces the partial result and the error.
- Haiku 5.5 prompts over 100K tokens cost 5x (scope unverified). The gateway emits a warning event; agent runtime owns compaction.
- Catalogue refresh (24h default, local 1h): a vanished model becomes `missing` and routes skip it. No whitelist is edited.
- Policy changes apply to the next call; in-flight calls finish.
- Telemetry goes through one mapping layer pinned to a GenAI semconv version; content capture off.
- Outbound requests only to configured provider URLs. No SSRF to arbitrary hosts from agent input.
- Terms: multi-account use, key sharing and free-tier pooling need counsel and provider-terms review before enabling (needs counsel).

## Testing
A `FakeProvider` adapter is scripted: text, tool calls, thinking, `retry-after` 429, spend-cap 429, timeouts, malformed tool JSON, mid-stream drop, wrong served model. Cassette replay for adapter wire tests; changed requests fail loudly. Real Postgres for policy, quota and usage. Key cases: deny by default; fallback never leaves policy; no fallback after the first event; `retry-after` versus spend-cap states; `AgentCapacityChanged` fires only when all candidates are blocked; delegated-route violation suspends the route; Claude lane falls back from `native` to `pooled` only when native is blocked, and each `gw_usage` row carries the right identity; no subscription or OAuth credential kind is loadable; planted key absent from logs; catalogue-refresh diff leaves whitelists intact; guided-setup probe against a fake FreeLLMAPI; `IChatClient` adapter round-trips every `ModelEvent`.

## Work packages
1. Contracts: `ModelRequest`, `ModelEvent`, `gw_usage` record, quota events. (none)
2. `FakeProvider` and the test harness. (1)
3. Postgres migrations and repositories for the six tables. (1)
4. `openai_chat` adapter with SSE and tool calls. (1, 2)
5. `anthropic_messages` adapter. (1, 2)
6. `gemini_generate_content` adapter. (1, 2)
7. Policy engine and route resolver. (1, 3)
8. Quota tracker and the `AgentCapacityChanged` bridge. (3, 4)
9. Health probes and breakers. (4)
10. Usage accounting and the dated price table. (3)
11. Catalogue loader, refresh and diff. (3, 4)
12. Secret-ref resolution through doc 14's `ISecretStore`, and scrubbing. (14's interface)
13. FreeLLMAPI provider: usage and health, and the expected-set auditor. (4, 7, 8)
14. Guided-setup service for the admin panel. (13)
15. Reverse-proxy and local-runtime add-provider validation. (4, 11)
16. Spike: .NET 10 socks5 through `SocketsHttpHandler`. (none)
17. `IChatClient` adapter over `IModelGateway`, first wave. (1, 7)

## Conflicts
1. **Recorded C14 caveats (not re-asked).** FreeLLMAPI maps Claude names to its own free pool and documents no pass-through of an operator's key, so native calls go direct only and FreeLLMAPI's Claude calls are always `pooled`. Its "Claude Code tiers" routing may involve subscription logins, which Anthropic's terms forbid on any route; the design excludes it. Separately, FreeLLMAPI's production fit is a real residual risk (personal-experimentation scope, one maintainer, nine releases in a week, SECURITY.md supports 0.13.x while the line is 0.16.x). The owner gives it priority; this design mitigates with warnings, pinning, audit and Bazaar-side enforcement.
2. **Policy editing (new).** This doc says policy is edited only by the owner and never by an agent. C32 says members and roles are managed by the group, and lists "changing a model whitelist" as a candidate for a "bigger change" that needs the owner's vote. Doc 07 and docs 10 and 11 must define the rule; the owner must define "bigger changes" (C32 FOLLOW-UP).
3. **Delegated routes are post-hoc.** `custom/*` cannot be hard-enforced before the call. Enforcement is audit plus route suspension, weaker than "Bazaar enforces policy itself" (Question 2).
4. **Stale lines in other docs.** Bazaar.md line 58 ("`auto/cheap` only") and line 60 (`auto/*`-routed juniors) still contradict `custom/*`. CLAUDE.md, Bazaar.md line 63 and Bazaar.Server.md item 11 still say the Claude lane is "Anthropic API keys only" or route Anthropic traffic through FreeLLMAPI; C14 governs. Bazaar.md line 55 lists Opus and Fable as whitelisted, but both are OPEN until the live model list shows them (Question 9).

## Questions for the owner
1. Does `custom/*` map one Bazaar route per FreeLLMAPI profile (`auto:<profile>`)? Default: yes.
2. Accept post-hoc audit plus route suspension for delegated routes? Default: yes.
3. Claude lane order: native first, pooled as the fallback? Default: yes; pooled also when no native key is configured.
4. Charles and the juniors without FreeLLMAPI: pinned cheap whitelist, no `auto`? Default: yes, Haiku 5.5 plus one free source.
5. Who may edit model policy: only the owner in the admin panel, or the group by vote under C32? Default: owner only until the "bigger changes" rule is defined.
6. Hide outbound proxy and multi-key rotation in v1? Default: hidden; `socks5` after the spike.
7. Record a per-provider free-tier data acknowledgement with timestamp, required to enable a free-tier provider? Default: yes.
8. Catalogue updates only with releases, plus a manual "check for newer catalogue" button? Default: yes.
9. Whitelist "Fable" only when the live model list shows it? Default: yes.
10. Keep `gw_usage` rows indefinitely (no prompt text)? Default: yes; rows hold no personal content, so C01 erasure does not reach them.
11. Haiku 100K guard: warn only, or refuse? Default: warn, with a configurable hard cap.
12. Warn when FreeLLMAPI's version changes? Default: yes, if a version endpoint exists (unverified).
13. Gemini lane default: paid key first, FreeLLMAPI otherwise? Default: yes.
14. Multiple accounts per provider: off until signed off per provider? Default: yes.

ANSWERED: Claude lane uses the owner's key and FreeLLMAPI's free tokens, by availability, labelled `native` or `pooled` (DECISIONS-LOG round 4, C14).
ANSWERED: the model-call seam is `ModelEvent`, with an `IChatClient` adapter in v1 (DECISIONS-LOG round 8, C29).
ANSWERED: provider keys are held by the Identity Service from the start, behind doc 14's `ISecretStore`; no OS-file interim store (DECISIONS-LOG round 2, C05).
