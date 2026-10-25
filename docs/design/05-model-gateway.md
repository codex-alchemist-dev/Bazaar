# Model Gateway (Bazaar.Server's own)

Status: DRAFT for owner review (2026-10-25) · Owner questions: 16
Depends on: no other design doc is written yet. Subsystems named instead: Identity/secret store (Soul Passport, Wave 3), Bazaar.Contracts, Work-Life Simulation, admin panel and dashboard. Used by: agent runtime, Work-Life Simulation, dashboard, admin panel.

## Purpose
The single path through which every agent model call leaves Bazaar.Server. It enforces per-agent model policy itself, treats every provider as an interchangeable upstream, tracks quota and usage per provider key, and hands quota state to the Work-Life Simulation. No upstream is trusted to enforce policy.

## Decided inputs
- Bazaar.Server has its own gateway; per-agent model policy is enforced there (CLAUDE.md; Bazaar.Server.md s3). No global `auto/*`.
- Gateway layer is pluggable per install. FreeLLMAPI has priority support: guided setup, usage and health on the dashboard, a default free-lane preset, its manual and custom dynamic routing modes. OmniRoute, direct provider keys and custom proxies are also supported.
- Operators can add many API reverse proxies and local runtimes. The default source catalogue comes from public provider facts. OpenCode and SillyTavern are concept references only: no code or config imports, no live models.dev tracking.
- Claude lane: Anthropic API keys, Haiku-heavy. Pro/Max subscription credentials are never routed.
- Free-tier data exposure is the operator's choice; setup shows a clear warning.
- Money budgets are REJECTED. Limits are quotas plus the Work-Life Simulation. The GC cost chip is display only.
- Raw Npgsql, SQL-first; xUnit v3; no Protobuf tooling yet.

## Design
**Pipeline per call** [PROPOSED]: agent runtime submits `ModelRequest` (agent id, route or model id, messages, tools) → resolve route to candidates → policy check on every candidate → drop candidates that are unhealthy or quota-blocked → call via adapter → classify the outcome → fallback only to the next policy-allowed candidate → write one `UsageRecord` → emit quota events.

**Provider kinds** [PROPOSED]: `first_party`, `aggregator`, `gateway` (FreeLLMAPI, OmniRoute, LiteLLM), `reverse_proxy` (any OpenAI-compatible URL), `local` (Ollama, LM Studio, llama.cpp, vLLM). All are a `Provider` record plus one adapter.

**Adapters** (DECIDED families, per source catalogue): `openai_chat` (default), `anthropic_messages` (native), `gemini_generate_content` (native). The Anthropic and Gemini OpenAI shims are not used: they are test-only/beta and drop caching, strict tools and thinking. Ollama's native API is optional, later.

**Own types.** Adapters translate to and from `ModelEvent` (`TextDelta`, `ThinkingDelta`, `ToolCallStart/ArgsDelta/End`, `Usage`, `Done(stopReason)`, `Error`). Tool-call and thinking differences stay at the adapter edge. No retry or fallback once any event reached the agent: a mid-stream failure surfaces the partial result and the error.

**Policy.** Deny by default. Entries are provider-qualified (`anthropic-direct:claude-haiku-5-5`) or a named route. Checked at every attempt, including fallbacks. Stored in Postgres, edited only by the owner through the admin panel, never by an agent. New catalogue models never join a whitelist automatically.

**Routes and fallback groups.** A route is an ordered candidate set with a strategy (`priority`, `round_robin`, `least_loaded`). The proposal's global `fallbackGroups` become per-route sets that are intersected with the agent's policy. A fallback may never leave the agent's allowed set or its model-family class.

**Delegated routes (`custom/*`).** When FreeLLMAPI is installed, Charles and the junior swarm default to a `custom/*` route that resolves to a FreeLLMAPI custom-mode profile: automatic switching, custom rules, a whitelist of cheaper code-capable models. FreeLLMAPI picks the model, so Bazaar cannot block a wrong pick before the call. [PROPOSED] mitigation: (1) the route declares an expected member set; (2) guided setup tells the operator to configure the same set in FreeLLMAPI; (3) Bazaar checks the served model in the response and `X-Fallback-Trail` against the set after every call; (4) a violation suspends the route and raises an admin alert. Without FreeLLMAPI, Charles defaults to an explicit pinned cheap whitelist [PROPOSED].

**Claude lane.** Candidates are `anthropic-direct`, then optionally FreeLLMAPI. FreeLLMAPI documents no pass-through of an operator's Anthropic key, and maps Claude names to its free pool. So catalogue entries carry `identity: native | pooled`. The Claude lane accepts only `native`, and a FreeLLMAPI entry counts as `native` only after the operator confirms it holds their own Anthropic API key and a verification probe passes [PROPOSED]. Credential kinds are limited to `api_key`, `bearer`, `none`; OAuth or subscription kinds do not exist in the schema.

**Quota and the vacation simulation.** State is tracked per (provider key, model class), never per agent, because agents share one key.

| State | Trigger | Retry | Source |
|---|---|---|---|
| `Available` | | | |
| `RateLimited(until)` | 429 with `retry-after`, or header `reset` | after `until` | Anthropic headers verified |
| `SpendCapped(until)` | 429 `enforced_spend_limit_reached`, no `retry-after` | none until 00:00 UTC on the 1st | Anthropic, verified |
| `DailyExhausted(until)` | local counter hit a published RPD/TPD | reset time per provider | limits per catalogue; reset times unverified |
| `Down` | breaker open | half-open probe | passive plus active health |

A 429 with no `retry-after` and no spend-cap code gets jittered backoff. The gateway emits `AgentCapacityChanged(agentId, available, until, reason)` when all of an agent's allowed candidates are blocked. The Work-Life Simulation decides whether that becomes "on vacation"; the gateway only reports facts.

**Health.** Active probes use cheap list endpoints, never paid completions. Passive health comes from real-call errors. Several health paths are unverified (see catalogue).

**Secrets.** Provider records hold `secret://bazaar/providers/<id>/<name>` references only. Resolved through an `ISecretStore` owned by the Identity design; interim v1 store is a provider-per-OS behind that interface [PROPOSED]. Keys are scrubbed from logs and spans, with a planted-key test.

**FreeLLMAPI guided setup** [PROPOSED]:
1. Choose "connect existing". Bazaar links the install docs and does not run the `curl | bash` installer.
2. Warnings, each acknowledged and stored with a timestamp: FreeLLMAPI calls itself personal-experimentation, single-user, trusted-network; some free tiers (for example Gemini) train on prompts; provider terms are the operator's duty; pin the version, keep the `127.0.0.1` bind, back up the database and `ENCRYPTION_KEY` (upgrades have deleted keys); no subscription credentials.
3. Enter base URL (`http://localhost:3001/v1` default) and the unified key (stored as a secret ref).
4. Probe `/v1/models`, `/v1/usage`, then a test message. Show the exact URLs used.
5. Pick the free-lane preset: models from the list filtered to tool-capable, ticked by the operator.
6. Pick routing: manual (Bazaar pins model ids) or custom (`custom/*` as above).
7. Self-test: send probes and compare served models with the expected set.

Dashboard: `/v1/usage` monthly requests and tokens by platform, probe latency, error and fallback rates, expected-set violations.

**Reverse proxies and local runtimes.** Add-provider form: base URL, auth, extra headers, extra body include/exclude lists, model-list probe, test message, final request URL shown, validation on save. No silent "skip status check". Local runtimes: `none` auth, low concurrency cap, 60-second health. Two separately named concepts: "Base URL override" and "Outbound network proxy" (`none/http/https`; `socks5` blocked until the .NET 10 spike). Outbound proxy and multi-key rotation were not requested (OPEN); the schema allows them, the UI hides them.

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
```
Tables [PROPOSED]: `gw_provider(id, kind, adapter, base_url, auth_kind, secret_ref, headers, proxy_ref, terms_ack_at, enabled)`; `gw_model(provider_id, model_id, family, identity, caps jsonb, status, first_seen, last_seen, source)`; `gw_route(id, strategy, members jsonb, expected_set jsonb)`; `agent_model_policy(agent_id, allowed jsonb, default_route, version, changed_by, changed_at)`; `gw_quota_state(provider_id, key_ref, model_class, state, until, observed jsonb)`; `gw_usage(id, ts, agent_id, provider_id, key_ref, model_id, served_model, input, cache_create, cache_read, output, thinking, latency_ms, status, error_code, retry_after_s, estimated, price_version)`. Cost is computed from a dated price table, not stored as truth. No prompt or completion text is stored (GCs are the record).

**Catalogue format** (shipped file, secret-free):
```json
{ "id": "openrouter", "kind": "aggregator", "adapter": "openai_chat",
  "baseUrl": "https://openrouter.ai/api/v1", "auth": "bearer", "keyHint": "OPENROUTER_API_KEY",
  "modelList": "/models", "health": "/key",
  "freeTier": { "offered": true, "limits": { "rpm": 20, "rpd": 50, "rpdAfter10Credits": 1000 }, "source": "<url>", "checkedOn": "2026-10-24" },
  "termsRisk": "review", "trainsOnFreeData": "unknown",
  "verification": { "baseUrl": "partly", "limits": "verified", "health": "verified" } }
```

**Default catalogue** (V = primary page read, summarised fetch; P = partly; U = unverified; re-check raw pages before release):

| Source | Adapter | Base URL | Status |
|---|---|---|---|
| Anthropic | anthropic_messages | `https://api.anthropic.com` | P: limits and prices V; native path/headers carried, not re-read |
| Google Gemini API | gemini_generate_content | `https://generativelanguage.googleapis.com/v1beta` | P: shim base V; native path and free limits not published or read |
| OpenAI | openai_chat | `https://api.openai.com/v1` | U: base from SillyTavern code summary; free-tier claim needs raw check |
| OpenRouter | openai_chat | `https://openrouter.ai/api/v1` | P: limits and `/key` V; chat path U |
| Hugging Face router | openai_chat | `https://router.huggingface.co/v1` | V |
| Cerebras | openai_chat | `https://api.cerebras.ai/v1` | P: limits V; base is an assumption |
| Groq | openai_chat | to confirm | U: free-tier numbers not obtained |
| NVIDIA NIM | openai_chat | to confirm | U: ~40 RPM is forum-only; trial terms unread |
| Cloudflare Workers AI | openai_chat | account-specific | P: 10,000 neurons/day V; endpoint U |
| DeepSeek | openai_chat | to confirm | P: prices V (summarised); id `deepseek-flash`, old `deepseek-v4-flash` retired |
| Mistral, xAI | openai_chat | to confirm | U |
| FreeLLMAPI (gateway) | openai_chat | `http://localhost:3001/v1` | P: API reference and MIT V (summarised); code unread |
| OmniRoute (gateway) | openai_chat | operator-set | U: carried from earlier note only |
| LiteLLM (gateway) | openai_chat | operator-set | V: OpenAI-compatible, MIT |
| Ollama | openai_chat | `http://localhost:11434/v1` | V (2024 post) |
| LM Studio / llama.cpp | openai_chat | `http://127.0.0.1:1234/v1` / `:8080/v1` | P: from OpenCode docs only |
| vLLM | openai_chat | operator-set | U |
| Custom OpenAI-compatible | openai_chat | operator-set | n/a |

Not shipped: GitHub Models (retired 2026-07-30), and any Copilot, Claude Pro/Max, ChatGPT or Gemini CLI/Antigravity subscription login.

**Reconciliation with the PROPOSAL JSON:**
| PROPOSAL | Change |
|---|---|
| No FreeLLMAPI | Added as `gateway` kind with usage, health and setup flow |
| Claude lane direct only | Candidates now direct plus optional FreeLLMAPI with `identity` gate |
| `agentWhitelists` in config | Moved to Postgres policy table, owner-edited |
| Global `fallbackGroups` | Per-route candidates, intersected with agent policy |
| `fallbackWithinGroupOnly` | Replaced by "never leave allowed set or family class" |
| Hard-coded `limits` (rpm 1000) | Read from headers; static limits only where published |
| `secret://` refs, `pooling`, `unknownModelPolicy: deny` | Kept. Multi-account entries stay off by default |
| `outboundProxies`, `socks5` | Kept as schema only; UI hidden (OPEN); socks5 spike-gated |
| Added fields | `kind`, `identity`, `termsRisk`, `trainsOnFreeData`, `verification`, `terms_ack_at` |

## Behaviour and edge cases
- Streaming: usage may arrive only at stream end; on abort, record partial usage with `estimated=true`.
- Haiku 5.5 prompts over 100K tokens cost 5x (scope of the step unverified). Gateway emits a warning event; agent runtime owns compaction.
- Catalogue refresh (24h default, local 1h): a vanished model becomes `missing` and routes skip it; no whitelist is edited. "Fable" is whitelistable only if the live list shows it.
- Policy change applies to the next call; in-flight calls finish.
- Telemetry goes through one mapping layer pinned to a GenAI semconv version; content capture off.
- Outbound requests only to configured provider URLs. No SSRF to arbitrary hosts from agent input.
- Terms: multi-account use, key sharing and free-tier pooling need counsel and provider-terms review before enabling.

## Testing
A `FakeProvider` adapter is scripted: text, tool calls, thinking, `retry-after` 429, spend-cap 429, timeouts, malformed tool JSON, mid-stream drop, wrong served model. Cassette replay for adapter wire tests; changed requests fail loudly. Real Postgres for policy, quota and usage. Key cases: deny by default; fallback never leaves policy; no fallback after the first event; retry-after versus spend-cap states; `AgentCapacityChanged` fires only when all candidates are blocked; delegated-route violation suspends the route; Claude lane rejects `pooled`; no OAuth credential kind loadable; planted key absent from logs; catalogue-refresh diff leaves whitelists intact; guided-setup probe against a fake FreeLLMAPI.

## Work packages
1. Contracts: `ModelRequest`, `ModelEvent`, `UsageRecord`, quota events. (none)
2. `FakeProvider` and the test harness. (1)
3. Postgres migrations and repositories for the six tables. (1)
4. `openai_chat` adapter with SSE and tool calls. (1, 2)
5. `anthropic_messages` adapter. (1, 2)
6. `gemini_generate_content` adapter. (1, 2)
7. Policy engine and route resolver. (1, 3)
8. Quota tracker and the `AgentCapacityChanged` bridge. (3, 4)
9. Health probes and breakers. (4)
10. Usage accounting and the price table. (3)
11. Catalogue loader, refresh and diff. (3, 4)
12. Secret-ref resolver and scrubbing. (Identity interface)
13. FreeLLMAPI provider: usage/health and the expected-set auditor. (4, 7, 8)
14. Guided-setup service for the admin panel. (13)
15. Reverse-proxy and local-runtime add-provider validation. (4, 11)
16. Spike: .NET 10 socks5 through `SocketsHttpHandler`. (none)

## Conflicts
1. The FreeLLMAPI evaluation recommended against it as Bazaar's production gateway (personal-experiment scope, no documented whitelist, one maintainer, nine releases in a week, SECURITY.md supports only 0.13.x while the line is 0.16.x). The owner decision gives it priority support anyway. This design mitigates with warnings, version pinning, audit and Bazaar-side enforcement, but the residual risk is real.
2. CLAUDE.md and Bazaar.Server.md say the Claude lane is "Anthropic API keys only"; the assignment adds FreeLLMAPI "by availability". FreeLLMAPI documents no own-key pass-through and maps Claude names to its free pool. The `identity` gate resolves this only if FreeLLMAPI can hold the real key (spike 5).
3. Delegated `custom/*` routes cannot be hard-enforced before the call. Enforcement is post-hoc plus configuration, weaker than the decided "Bazaar enforces policy itself".
4. Bazaar.md still says Charles uses `auto/cheap` only and juniors use `auto/*`; that must be amended to `custom/*` or a pinned list.
5. Bazaar.md's DeepSeek whitelist names "V4 Flash"; DeepSeek says that id is retired. NIM's "near-infinite" supply is unsupported (~40 RPM forum-only).
6. The PROPOSAL's static limits (for example Haiku 1,000 RPM) conflict with its own "read from headers" note.

## Questions for the owner
1. Does `custom/*` map to a FreeLLMAPI profile (`auto:<profile>`)? Default: yes, one Bazaar route name per profile.
2. Accept post-hoc audit plus route suspension for delegated routes? Default: yes.
3. May the Claude lane use FreeLLMAPI at all? Default: only as a fallback, off until it is shown to hold your own key.
4. Charles and juniors without FreeLLMAPI: pinned cheap whitelist, no auto? Default: yes, Haiku 5.5 plus one free source.
5. Policy editable only by you in the admin panel, never by agents? Default: yes.
6. Own `ModelEvent` types, with a thin `IChatClient` adapter later? Default: yes.
7. Interim secret store before Soul Passport: environment-variable refs or an OS-protected file per OS? Default: OS-protected file behind `ISecretStore`.
8. Hide outbound proxy and multi-key rotation in v1? Default: hidden; socks5 after the spike.
9. Record a per-provider free-tier data acknowledgement with timestamp? Default: yes, required to enable a free-tier provider.
10. Catalogue updates only with releases, plus a manual "check for newer catalogue" button? Default: yes.
11. Whitelist "Fable" only when the live model list shows it? Default: yes.
12. Keep usage rows indefinitely (no prompt text)? Default: yes.
13. Haiku 100K guard: warn only, or refuse? Default: warn, with a configurable hard cap.
14. Warn when FreeLLMAPI's version changes? Default: yes, if a version endpoint exists (unverified).
15. Gemini lane default in your install: paid Gemini key, else FreeLLMAPI? Default: yes.
16. Multiple accounts per provider: off until signed off per provider? Default: yes.
