# Model Gateway (Bazaar.Server's own)

Status: RECONCILED with DECISIONS-LOG (2026-10-25), cross-doc pass done · Owner questions: 3
Depends on: 04-postgres-schema-and-migrations.md (conventions, migrations), 14-identity-service-and-passport.md (`ISecretStore`), 15-security-taint-and-approvals.md (shared quota table). Used by: 06-agent-runtime.md, 07-soul-system.md, 08-memory-system.md, 09-work-life-simulation.md, 13-skimmer-bouncer.md, 17-coding-agent-harness.md, 23-observatory-and-usage.md, 34-testing-strategy.md, 35-spikes-plan.md.

## Purpose
The single path through which every agent model call leaves Bazaar.Server. It enforces per-agent model policy itself, treats every provider as an interchangeable upstream, tracks quota and usage per provider key, and hands quota facts to the Work-Life Simulation. No upstream is trusted to enforce policy.

## Decided inputs
- Bazaar.Server has its own gateway that enforces per-agent policy. No global `auto/*` (CLAUDE.md; Bazaar.Server.md §3).
- The gateway layer is pluggable per install. FreeLLMAPI has priority support: guided setup, usage and health on the dashboard, a default free-lane preset, manual and custom dynamic routing. OmniRoute, direct provider keys and custom proxies are supported too. Operators may add reverse proxies and local runtimes (Bazaar.md §3).
- **Claude lane (DECIDED round 4, C14):** the Claude member uses both the owner's Anthropic API key and FreeLLMAPI's free Claude tokens, by availability. This supersedes "Anthropic API keys only" in CLAUDE.md. Usage and cost records keep an `identity` label, `native` or `pooled`. Pro/Max and other subscription logins stay off every route the gateway or a third party holds; the one exception is the CLI provider of doc 91, where the unmodified `claude` program keeps the login (owner request 2026-11-20, PROPOSED).
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

**Embeddings** [PROPOSED]. The install's one embedding profile is decided (C39); this is how a call reaches a model. `IModelGateway.EmbedAsync(EmbeddingRequest, ct)` takes an agent id, a route or model id, a batch of input strings and optional dimensions, and returns `EmbeddingResult` (one `float[]` per input in input order, the model that served the call, input tokens). It runs the chat pipeline above unchanged: the same route resolution and per-agent policy check on every candidate (the route is a model id or route name the agent's policy lists, so an embedding model must be on the agent's whitelist like any other), the same health, breaker and quota checks, the same failure classification and fallback (only to the next policy-allowed candidate), and exactly one `gw_usage` row per attempt that reached an adapter, with `input` set to the reported prompt tokens (or the character estimate) and `output` 0. An adapter takes part only if it also implements `IEmbeddingAdapter`; today that is `openai_chat`, which posts `{base}/embeddings` (OpenAI-compatible, so FreeLLMAPI, OmniRoute, reverse proxies and local runtimes work). `anthropic_messages` and `gemini_generate_content` do not embed and their candidates are skipped as unusable. When no candidate can serve the call, `EmbedAsync` throws `GatewayModelException` with the gateway's failure code (`policy_denied`, `no_capacity`, or the last upstream failure). An empty batch returns an empty result without a call. `FakeModelGateway` answers with deterministic hash-based unit vectors (32 dimensions by default). Previously open: a distinct "embedding" model class on `gw_model`, and how an operator declares the install's embedding route. Proposed answers are in the bullets below [PROPOSED].

- **Embedding kind** [PROPOSED] (answers the first open point). `gw_model` gets a `kind` column, `chat` or `embedding`, added by a gateway-namespace migration; the existing `family` column keeps its meaning. Chat routes exclude embedding models, embedding routes exclude chat models, and quota and health are tracked per kind.
- **Install embedding route** [PROPOSED] (answers the second open point). The setting `gateway.embedding.route` is registered through the Gateway module's settings contribution (the settings registry composes module keys through `ISettingContribution`). A caller of `EmbedAsync` may omit `Route`; the gateway then uses this setting. An explicit `Route` still goes through per-agent policy.
- **Dimension check** [PROPOSED]. The gateway checks every returned vector's length against the install embedding profile's `halfvec` dimension (C39, DECIDED). On a mismatch it fails closed: the call throws `GatewayModelException` with code `embedding_dimension_mismatch`, the candidate's output is discarded, and nothing is returned or stored.
- **Batching and caps** [PROPOSED]. Batch size and input length caps are per-model settings with provider defaults (for example, 2048 inputs per request for OpenAI-compatible endpoints). The gateway splits a batch into chunks within the caps and merges the results in input order, so callers (for example the Memory module) pass any batch and need no chunking of their own. One `gw_usage` row is written per upstream request. [PROPOSED] Details: the default caps are 2048 inputs per request and 32768 characters per input (OpenAI-compatible, 8192 tokens at four characters per token); a model's `embed_max_batch` and `embed_max_input_chars` override them. An input longer than every candidate's cap fails the call with `embedding_input_too_long` before any upstream call (never truncated); a candidate whose cap is smaller than the longest input is skipped. A retryable failure on any chunk discards the earlier chunks and restarts the whole batch on the next candidate, so one batch never mixes models. A route that is unset fails with `embedding_route_unset`. The dimension is read through `IEmbeddingProfile` (Gateway.Abstractions), which the host backs with the active `embedding_profile` row; with no active profile no check runs. Embedding calls keep health and quota separate from chat (health key suffix `#embedding`, quota class prefix `embedding:`).

**Provider kinds** [PROPOSED]: `first_party`, `aggregator`, `gateway` (FreeLLMAPI, OmniRoute, LiteLLM), `reverse_proxy` (any OpenAI-compatible URL), `local` (Ollama, LM Studio, llama.cpp, vLLM). Each is a `gw_provider` record plus one adapter.

**Adapters** (DECIDED families): `openai_chat` (default), `anthropic_messages`, `gemini_generate_content`. The Anthropic and Gemini OpenAI shims are not used: they drop caching, strict tools and thinking. Ollama's native API is optional, later. Adapters translate to and from `ModelEvent` (`TextDelta`, `ThinkingDelta`, `ToolCallStart/ArgsDelta/End`, `Usage`, `Done(stopReason)`, `Error`); differences stay at the adapter edge.

**IChatClient adapter (v1, first wave).** A thin `IChatClient` (Microsoft.Extensions.AI) implementation maps `ModelEvent` to and from the gateway, so Microsoft libraries can plug in. Tests in docs 06, 17 and 34 may use it.

**Policy** [PROPOSED]. Deny by default. Entries are provider-qualified (`anthropic-direct:claude-haiku-5-5`) or a named route, and are checked at every attempt, including fallbacks. Stored in Postgres and edited through the admin panel. No agent edits policy. The owner edits model whitelists (Question 3). New catalogue models never join a whitelist automatically, so a model such as Fable or Opus enters a whitelist only by an explicit owner edit after the live model list shows it. The Skimmer gatekeeper scorer (doc 13) has its own policy subject, `gatekeeper`: a fixed model list, no tools, and no fallback outside that list.

**Routes and fallback.** A route is an ordered candidate set with a strategy (`priority`, `round_robin`, `least_loaded`). Fallback sets are per route and intersected with the agent's policy. A fallback never leaves the agent's allowed set or its model-family class. [PROPOSED] A route may be bound to a selection profile (see [92-endpoint-selection.md](92-endpoint-selection.md)): a selector then orders the resolved candidates per call from measured latency, reliability, quota headroom and cost. It only reorders or omits candidates the policy allowed.

**Refusals and the usage source (built).** A call that policy refuses (any `DenyReason`, including a model outside the agent's whitelist) gets a clean `policy_denied` failure, calls no provider, and writes one zero-token `gw_usage` row with status `denied`, error code `policy_denied:<reason>` and the provider of the first dropped candidate (or the requested model), else the sentinel `policy`. Every usage row carries `source`: `anthropic_key` (provider `anthropic-direct`), `freellmapi`, `claude_code_cli` (provider `claude-code-cli`), `policy` for refusals before any provider, and the provider id for any other source. The `gw_usage` provider foreign key is dropped so a refusal row and usage history outlive provider edits (migration `0021_gateway_usage_source.sql`). [PROPOSED] A dedicated `subscription` identity for the CLI lane stays with doc 91.

**Delegated routes (`custom/*`).** When FreeLLMAPI is installed, Charles and the junior swarm default to a `custom/*` route that resolves to a FreeLLMAPI custom-mode profile (automatic switching over a whitelist of cheaper code-capable models). The mapping of routes to profiles is the setting `gateway.delegated_route.mapping`. FreeLLMAPI picks the model, so Bazaar cannot block a wrong pick before the call. [PROPOSED] mitigation: (1) the route declares an expected member set; (2) guided setup tells the operator to configure the same set in FreeLLMAPI; (3) Bazaar checks the served model and `X-Fallback-Trail` against the set after every call; (4) a violation suspends the route and raises an admin alert. Without FreeLLMAPI, Charles and the juniors use an explicit pinned cheap whitelist (Question 2).

**Claude lane.** Candidates: `anthropic-direct` (identity `native`, the owner's key) and FreeLLMAPI's Claude models (identity `pooled`). [PROPOSED] order is the setting `gateway.claude.order` (default `native_first`). Pooled is used when native is `RateLimited`, `SpendCapped` or `Down`, or when no native key is configured (`gateway.claude.pooled_when_no_native_key`, default on). Every FreeLLMAPI Claude entry is `pooled`; no probe promotes it to `native`. The gateway never holds a subscription credential: credential kinds are `api_key`, `bearer` and `none` only, FreeLLMAPI's "Claude Code tiers" stay refused, and the expected-set audit flags any served route outside the set. One exception route exists by owner request (2026-11-20, PROPOSED guard rails): the Claude Code CLI provider of [91-claude-code-cli-provider.md](91-claude-code-cli-provider.md), where the unmodified `claude` program holds the operator's own login and the gateway only sees model events (identity `subscription`). Recorded caveats are in Conflicts 1.

**Owner's Anthropic key in config (built).** The `anthropic-direct` source is defined in host configuration under `Bazaar:Gateway:AnthropicDirect` and overlays the stored directory, the same way the FreeLLMAPI source does. It is off by default, so an install with no key registers nothing. Settings: `Enabled` (default `false`), `ApiKey` (required when enabled; an `env:` or `file:` secret reference, never a literal key), `BaseUrl` (default `https://api.anthropic.com`) and `Models` (default `claude-haiku-5-5`). Each listed model gets the identity `native` and a priority route named after the model id, so the turn runner's default route `claude-haiku-5-5` resolves; a route with the same name stored in Postgres is replaced by the config one while the source is on. The key is read through the host secret resolver at start and on each call. A missing or unreadable key fails the start with a message that names the setting and never the value. A subscription OAuth token (`sk-ant-oat` or `sk-ant-ort` prefix) is refused. Per-agent model policy still applies: an agent with no policy that lists the model or route is denied. The dev seed (`DevAgentPolicySeed`) grants the agents listed under `Bazaar:AgentResponder:Agents` the `claude-haiku-5-5` route and `anthropic-direct:claude-haiku-5-5` while the source is on; it seeds no other Anthropic model and touches no other agent's whitelist.

**Quota and the vacation simulation.** State is tracked per (provider key, model class), never per agent, because agents share one key.

| State | Trigger | Retry | Source |
|---|---|---|---|
| `Available` | default | n/a | n/a |
| `RateLimited(until)` | 429 with `retry-after`, or header `reset` | after `until` | Anthropic headers verified |
| `SpendCapped(until)` | 429 `enforced_spend_limit_reached`, no `retry-after` | none until 00:00 UTC on the 1st | Anthropic, verified |
| `DailyExhausted(until)` | local counter hit a published RPD/TPD | reset time per provider | limits per catalogue; reset times unverified |
| `Down` | breaker open | half-open probe | passive plus active health |

A 429 with no `retry-after` and no spend-cap code gets jittered backoff. A `SpendCapped` or other blocked source reroutes the call to another allowed source of the same agent (C14). The gateway emits `AgentCapacityChanged(agentId, available, until, reason)` only when all of an agent's allowed candidates are blocked. The Work-Life Simulation (doc 09) decides whether that becomes "on vacation"; the gateway reports facts only.

**Health.** Active probes use cheap list endpoints, never paid completions. Passive health comes from real-call errors.

**Secrets.** Provider records hold `secret://bazaar/providers/<id>/<name>` references only, resolved by doc 14's `ISecretStore`. Keys are scrubbed from logs and spans, with a planted-key test.

**FreeLLMAPI guided setup** [PROPOSED]:
1. "Connect existing". Bazaar links the install docs and does not run the `curl | bash` installer.
2. Warnings, each acknowledged and stored with a timestamp: single-user, trusted-network, personal-experimentation scope; some free tiers train on prompts; provider terms are the operator's duty; pin the version, keep the `127.0.0.1` bind, back up the database and `ENCRYPTION_KEY`; no subscription credentials. The acknowledgement rule is the setting `gateway.free_tier.ack_mode` (needs counsel).
3. Base URL (`http://localhost:3001/v1` default) and the unified key, stored as a secret reference.
4. Probe `/v1/models`, `/v1/usage`, then one test message. Show the exact URLs used.
5. Pick the free-lane preset: tool-capable models from the list, ticked by the operator.
6. Pick routing: manual (Bazaar pins model ids) or custom (`custom/*`, above).
7. Self-test: compare served models with the expected set.

Dashboard: `/v1/usage` monthly requests and tokens by platform, probe latency, error and fallback rates, expected-set violations.

**Reverse proxies and local runtimes.** Add-provider form: base URL, auth, extra headers, body include/exclude lists, model-list probe, test message, final request URL shown, validation on save. Local runtimes: `none` auth, low concurrency cap, 60-second health. "Base URL override" and "Outbound network proxy" are separate concepts; `socks5` waits for the .NET 11 spike. Outbound proxy and multi-key rotation exist in the schema only and stay hidden in the UI unless `gateway.outbound_proxy.ui` or `gateway.multi_key.ui` is turned on.

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
- `gw_model(provider_id, model_id, family, identity native|pooled, caps jsonb, status, first_seen, last_seen, source, kind chat|embedding, embed_max_batch, embed_max_input_chars)` [PROPOSED for the last three, added by migration `gateway/0001_gateway_model_kind.sql`: `kind` defaults to `chat`; the two caps are nullable, and null means the provider default]
- `gw_route(id, strategy, members jsonb, expected_set jsonb)`
- `agent_model_policy(agent_id, allowed jsonb, default_route, version, changed_by, changed_at)`
- `gw_quota_state(provider_id, key_ref, model_class, state, until, observed jsonb)`
- `gw_usage(id, ts, agent_id, turn_id, provider_id, key_ref, model_id, served_model, identity, input, cache_write_5m, cache_write_1h, cache_read, output, thinking, latency_ms, status, error_code, retry_after_s, estimated, price_version)`. `input` counts uncached input tokens only and excludes cache reads (`cache_read`) and cache writes (`cache_write_5m`, `cache_write_1h`); doc 23 relies on this. `thinking` is a subset of `output`. The two cache-write columns are priced separately (1.25x and 2x multipliers, verified). `usage_record` is dropped (C33). Cost is computed from a dated price table, not stored as truth. No prompt or completion text is stored. The table is unpartitioned in v1 [PROPOSED]; retention is the setting `gateway.usage.retention_days`.

### Settings
Keys are registered in doc 03's settings registry [PROPOSED]. All are install-scoped, changed by the operator in the admin panel, and every change writes an `audit_log` entry. Rows marked "needs counsel" are privacy or terms options; their defaults are the protective choice. Lowering a protective default needs owner approval.

| Key | Type | Default | Scope | Who may change | Change needs approval? |
|---|---|---|---|---|---|
| `gateway.delegated_route.mapping` | enum: `one_route_per_profile`, `one_shared_route` | `one_route_per_profile` | install | operator | No |
| `gateway.claude.order` | enum: `native_first`, `pooled_first` | `native_first` | install | operator | No |
| `gateway.claude.pooled_when_no_native_key` | bool | true | install | operator | No |
| `gateway.gemini.order` | enum: `paid_first`, `free_first` | `paid_first` | install | operator | No |
| `gateway.outbound_proxy.ui` | bool | false | install | operator | No |
| `gateway.multi_key.ui` | bool | false | install | operator | No |
| `gateway.free_tier.ack_mode` | enum: `required`, `recorded` (needs counsel) | `required` | install | operator | Yes, owner, when set to `recorded` |
| `gateway.multi_account.enabled` | bool, per provider (needs counsel) | false | install | operator | Yes, owner, when enabled |
| `gateway.catalogue.refresh_hours` | int; 0 = release and manual check only | 0 | install | operator | No |
| `gateway.long_prompt.warn_tokens` | int | 100000 | install | operator | No |
| `gateway.long_prompt.hard_cap_tokens` | int; 0 = off | 0 | install | operator | No |
| `gateway.freellmapi.warn_on_version_change` | bool | true | install | operator | No |
| `gateway.usage.retention_days` | int; 0 = indefinite (needs counsel) | 0 | install | operator | Yes, owner, when set to a non-zero value |
| `gateway.embedding.route` [PROPOSED] | string: empty, a provider-qualified model id, or a route name (no wildcard, no auto) | empty (unset) | install | operator | No |

**Default catalogue** (shipped as a secret-free file; each entry carries `freeTier`, `termsRisk`, `trainsOnFreeData` and `verification` fields. V = primary page read; P = partly; U = unverified; re-check raw pages before release):

[Built: `src/Bazaar.Server.Gateway.Catalogue/DefaultCatalogue/default-catalogue.json`, loaded by `DefaultCatalogueLoader`. `freeTier`, `termsRisk` and `trainsOnFreeData` are `unknown` for every entry until the owner or a raw-page check fills them; `verification` and its note come from the Status column below.]

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

Not shipped: GitHub Models (retired 2026-07-30), and any Copilot, ChatGPT or Gemini CLI subscription login, or a Claude Pro/Max login held by anything other than the CLI provider of doc 91.

## Behaviour and edge cases
- Streaming: usage may arrive only at stream end. On abort, record partial usage with `estimated=true`. No retry or fallback once any event reached the agent; a mid-stream failure surfaces the partial result and the error.
- Haiku 5.5 prompts over 100K tokens cost 5x (scope unverified). The gateway emits a warning event at `gateway.long_prompt.warn_tokens`; the hard cap `gateway.long_prompt.hard_cap_tokens` is off by default. Agent runtime owns compaction.
- Catalogue refresh runs at `gateway.catalogue.refresh_hours` (default 0: release updates and the manual "check for newer catalogue" button only). A vanished model becomes `missing` and routes skip it. No whitelist is edited. Built (module `Bazaar.Server.Gateway.Catalogue`, doc 40): the refresh reads each enabled provider's own list (`openai_chat` `GET /models`, `anthropic_messages` `GET /v1/models`, `gemini_generate_content` `GET /models`, paged), marks vanished models `missing`, revives returned ones, and inserts new ones as `available` with the family and identity of the provider's latest model; a failed call changes nothing. The manual button is `POST /admin/api/gateway/catalogue/refresh` in the web admin panel (doc 51 section 5; PROPOSED; gated as the Modules area, no new capability). It returns per-provider outcome, counts and unclassified ids only, and `GET /admin/api/gateway/catalogue` returns the last result.
- Policy changes apply to the next call; in-flight calls finish.
- Telemetry goes through one mapping layer pinned to a GenAI semconv version; content capture off.
- Outbound requests only to configured provider URLs. No SSRF to arbitrary hosts from agent input.
- Terms: multi-account use, key sharing and free-tier pooling need counsel and provider-terms review before enabling (needs counsel; `gateway.multi_account.enabled` defaults off).

## Testing
A `FakeProvider` adapter is scripted: text, tool calls, thinking, `retry-after` 429, spend-cap 429, timeouts, malformed tool JSON, mid-stream drop, wrong served model. Cassette replay for adapter wire tests; changed requests fail loudly. Real Postgres for policy, quota and usage. Key cases: deny by default; fallback never leaves policy; no fallback after the first event; `retry-after` versus spend-cap states; `SpendCapped` reroutes to another allowed source; `AgentCapacityChanged` fires only when all candidates are blocked; the gatekeeper policy subject cannot reach a model outside its list; delegated-route violation suspends the route; Claude lane falls back from `native` to `pooled` only when native is blocked, and each `gw_usage` row carries the right identity; `gw_usage.input` excludes cache reads; no subscription or OAuth credential kind is loadable; planted key absent from logs; catalogue-refresh diff leaves whitelists intact; guided-setup probe against a fake FreeLLMAPI; `IChatClient` adapter round-trips every `ModelEvent`.

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
16. Spike: .NET 11 socks5 through `SocketsHttpHandler`. (none)
17. `IChatClient` adapter over `IModelGateway`, first wave. (1, 7)
18. Settings keys above registered in doc 03's registry. (doc 03)

## Conflicts
1. **Recorded C14 caveats (not re-asked).** FreeLLMAPI maps Claude names to its own free pool and documents no pass-through of an operator's key, so native calls go direct only and FreeLLMAPI's Claude calls are always `pooled`. Its "Claude Code tiers" routing may involve subscription logins, which Anthropic's terms forbid on any route; the design excludes it. Separately, FreeLLMAPI's production fit is a real residual risk (personal-experimentation scope, one maintainer, nine releases in a week, SECURITY.md supports 0.13.x while the line is 0.16.x). The owner gives it priority; this design mitigates with warnings, pinning, audit and Bazaar-side enforcement.
2. **Policy editing.** The owner edits policy (Question 3); no agent does. C32 says members and roles are managed by the group, and lists "changing a model whitelist" as a candidate for a "bigger change" that needs the owner's vote. Whether a whitelist change is a Council motion stays tied to the C32 FOLLOW-UP; docs 07, 10 and 11 must follow the answer.
3. **Delegated routes are post-hoc.** `custom/*` cannot be hard-enforced before the call. Enforcement is audit plus route suspension, weaker than "Bazaar enforces policy itself" (Question 1).
4. **Stale lines in other docs.** Bazaar.md line 58 ("`auto/cheap` only") and line 60 (`auto/*`-routed juniors) still contradict `custom/*`. Bazaar.md line 55 lists Opus and Fable as whitelisted; under this doc they enter a whitelist only by owner edit after the live list shows them. Wherever CLAUDE.md, Bazaar.md line 63 or Bazaar.Server.md item 11 still say "Anthropic API keys only" or route Anthropic traffic through FreeLLMAPI, C14 governs.

## Questions for the owner
1. Accept post-hoc audit plus route suspension for delegated routes? Default: yes. (Weakens "Bazaar enforces policy itself", a security-relevant choice, so it stays a question.)
2. Charles and the juniors without FreeLLMAPI: pinned cheap whitelist, no `auto`? Default: yes, Haiku 5.5 plus one free source.
3. Who may edit model policy: only the owner in the admin panel, or the group by vote under C32? Default: owner only until the "bigger changes" rule is defined.

MOVED TO SETTINGS: gateway.delegated_route.mapping, gateway.claude.order, gateway.claude.pooled_when_no_native_key, gateway.gemini.order, gateway.outbound_proxy.ui, gateway.multi_key.ui, gateway.free_tier.ack_mode, gateway.multi_account.enabled, gateway.catalogue.refresh_hours, gateway.long_prompt.warn_tokens, gateway.long_prompt.hard_cap_tokens, gateway.freellmapi.warn_on_version_change, gateway.usage.retention_days (DECISIONS-LOG round 13)

ANSWERED: Claude lane uses the owner's key and FreeLLMAPI's free tokens, by availability, labelled `native` or `pooled` (DECISIONS-LOG round 4, C14).
ANSWERED: the model-call seam is `ModelEvent`, with an `IChatClient` adapter in v1 (DECISIONS-LOG round 8, C29).
ANSWERED: provider keys are held by the Identity Service from the start, behind doc 14's `ISecretStore`; no OS-file interim store (DECISIONS-LOG round 2, C05).
