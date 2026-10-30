# FreeLLMAPI as a first-class provider

Status: PROPOSED, for owner review · Extends: [05-model-gateway.md](05-model-gateway.md) · Owner questions: 4

## 1. Status

- **PROPOSED.** On 2026-10-29 the owner decided to use FreeLLMAPI as the testing LLM source and as a first-class provider.
- Bazaar-side enforcement stays in force, unchanged: deny-by-default per-agent whitelists (doc 05, Policy), a served-model check after every call, and subscription credentials refused on every route (doc 05, credential kinds `api_key`, `bearer`, `none` only).
- This note adds no DECIDED item and changes none. Where it touches the Claude lane, C14 governs (see section 6).

Evidence labels used below: **V** = primary page or file read (summarised by the reader); **S** = secondary source; **src** = source code read (partial where noted); **I** = inference; **U** = unverified; **P** = partly read.

## 2. What the owner supplies

- **Base URL.** Default `http://127.0.0.1:3001/v1` (owner). Doc 05 uses `http://localhost:3001/v1` in its catalogue and setup text; the owner value wins for this provider, and the two are reconciled in one line of doc 05 when this is accepted.
- **One unified key**, `freellmapi-…`, copied from the Keys page of the FreeLLMAPI dashboard. The unified key is the only key that calls `/v1/usage` [V, evaluation note section 3]. Per-client keys (`sk-cp-` profile keys) are not used for agent scoping until the S17 conflict is settled [src, DECISIONS-LOG S17].
- **Storage.** The key is never written to git, to a config file, or to log text. Config holds a reference only: `env:<VAR>` or `file:<path>` (owner's wording). **PROPOSED** mapping: these are the two resolver inputs behind doc 05's `secret://bazaar/providers/<id>/<name>` reference, resolved by doc 14's `ISecretStore` (C05). The schema choice is open (section 6).
- **Probe at setup.** `GET /v1/models`, then `GET /v1/usage`, then one test message (doc 05, guided setup steps 4 and 7). Planted-key test: the key must not appear in logs or spans (doc 05, Secrets).

## 3. How it plugs in

- **Provider.** One `gw_provider` record: kind `gateway`, adapter `openai_chat`, base URL above, `auth_kind` `bearer`, `secret_ref` as in section 2. Adapter code: `src/Bazaar.Server/Gateway/Adapters/OpenAi/OpenAiChatAdapter.cs`.
- **Route.** One named route, `agent-default`, with model `auto`. FreeLLMAPI picks the upstream model. Bazaar checks the candidate route against the agent's policy on every attempt (doc 05, Pipeline).
- **Expected set.** The route declares the expected member set: the free-lane preset the operator ticked in guided setup (doc 05, step 5). This is the same audit as delegated routes (doc 05, "Delegated routes"), because `auto` cannot be blocked before the call.
- **Fallback trail.** After each call, read `X-Fallback-Trail` and `X-Fallback-Attempts`, and record the served model and the trail on the `gw_usage` row (doc 05 table). A trail that cannot be read in full is a violation, not a pass. Check code: `src/Bazaar.Server/Gateway/FreeLlmApi/FreeLlmApiRouting.cs` (`TrailMalformed`).
- **Expected-set auditor.** `FreeLlmApiExpectedSetAuditor.Violations(route, routing, servedModel)` returns the violations for one call. Code: `src/Bazaar.Server/Gateway/FreeLlmApi/FreeLlmApiExpectedSetAuditor.cs`. A violation suspends the route and raises an admin alert (doc 05, mitigation step 4).
- **429 and Retry-After.** A 429 with `Retry-After` becomes `RateLimited(until)` and the route waits (doc 05, quota table). A 429 with no `Retry-After` gets jittered backoff. The OpenAI adapter already maps 429 to `rate_limited` with the header value (`OpenAiChatAdapter.cs`, src).
- **Config keys.** Owner's names: `Bazaar:Gateway:FreeLlmApi:*`. **Not in code yet** (no match under `src/Bazaar.Server`). Proposed members: `BaseUrl`, `SecretRef`, `DefaultRoute` (`agent-default`), `Model` (`auto`), `ExpectedSet`, `PinnedVersion`. Doc 05's `gateway.freellmapi.warn_on_version_change` stays a registry setting (doc 03); the host keys are not a second settings store (section 6).

## 4. Limits and risks

- **Scope.** FreeLLMAPI's README and SECURITY.md describe personal, single-user, trusted-network use and say "not production" (V, evaluation note section 1). Mitigations: pin the version, keep the `127.0.0.1` bind, store the warning acknowledgement (doc 05, step 2). The owner accepts this scope for testing and first-class use; the risk stays recorded.
- **Shared limits.** Quotas are per provider key, not per agent (V, evaluation note section 3). All agents using the unified key draw one free pool, so one busy agent can exhaust the others. Quota state is already tracked per provider key and model class (doc 05). Per-agent soft caps are a PROPOSED add-on, not a claim of isolation.
- **No documented per-client allowlist.** No per-client model restriction is documented (V as an absence, evaluation note section 3). The S17 source read (partial) found that fallback can reach any enabled chain member with no caller exclusion list (src, DECISIONS-LOG S17). Bazaar's policy is therefore the only gate.
- **`auto` can leave the whitelist.** A call may be served by a model outside the agent's whitelist (I, from the S17 read). Bazaar cannot block the pick before the call. The served model and trail are checked after the call, so this is detection, not prevention. A completed call outside policy is logged and the route is suspended, not undone.
- **Claude names map to the free pool.** Claude model names are documented as mapping to FreeLLMAPI's free pool, not to Anthropic (V, evaluation note section 3). Under C14 these calls are labelled `pooled` in usage.
- **Subscription routing.** FreeLLMAPI's "Claude Code tiers" (release v0.16.0) may involve subscription logins (V, evaluation note section 3). Anthropic's terms bar routing Claude Code through Free, Pro or Max credentials on a third party's behalf (V, evaluation note section 3, citing Anthropic's legal page). Subscription logins stay off every route; S17 checks the live behaviour (U until run).
- **Version churn.** Nine releases in one week, and SECURITY.md supports 0.13.x while the project line is 0.16.x (V, evaluation note; doc 05 Conflicts 1). Pinning is required, and each upgrade is an owner action.
- **Free-tier data.** Some free tiers train on prompts (P, doc 05). Provider terms are the operator's duty; the setup warning stays.

## 5. Open questions for the owner

1. Default route for Charles and the juniors: the `custom/*` mapping over a cheap code-capable whitelist (doc 05 default), or a pinned whitelist with no `auto`?
2. Pin the FreeLLMAPI version? Recommend yes: an exact version in config, with upgrades by the owner only.
3. Are live model calls allowed in CI? Recommend no: fixtures and cassettes only (doc 34).
4. Where does the owner's instance run? Recommend the same host as Bazaar.Server, so the `127.0.0.1` bind is reachable and nothing else is.

## 6. Conflicts found (recorded, not resolved here)

- **Claude lane.** The evaluation note's bottom line says FreeLLMAPI "must not carry the Claude lane". C14 (DECIDED, round 4) puts FreeLLMAPI's Claude models on that lane as `pooled`. The owner's 2026-10-29 decision does not address the Claude lane. This note leaves C14 as written.
- **`auto` and "no global `auto/*`".** CLAUDE.md (DECIDED) forbids global `auto/*`. The `agent-default` route is per agent, but it uses `auto` upstream. The owner must confirm that a per-agent named route with an expected set is acceptable.
- **Secret reference form.** Owner: `env:` or `file:`. Doc 05 and C05: `secret://bazaar/providers/<id>/<name>`. One form is needed (schema choice).
- **Config keyspace.** Owner names `Bazaar:Gateway:FreeLlmApi:*`; doc 05 uses registry settings `gateway.*` (doc 03). One store is needed.
- **Base URL.** `127.0.0.1` (owner) versus `localhost` (doc 05). Not DECIDED; reconcile in doc 05.

## 7. Links

- Evaluation note: [freellmapi_evaluation.md](../research/notes/FreeLLMAPI%20gateway%20evaluation/freellmapi_evaluation.md)
- Gateway design: [05-model-gateway.md](05-model-gateway.md); decisions: [DECISIONS-LOG.md](DECISIONS-LOG.md) (C14, C05, S17)
- Code: `src/Bazaar.Server/Gateway/FreeLlmApi/` (auditor, routing, usage, health probe, catalogue) and `src/Bazaar.Server/Gateway/Adapters/OpenAi/` (`OpenAiChatAdapter.cs`, `OpenAiChatWire.cs`)
