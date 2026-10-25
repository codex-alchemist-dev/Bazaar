# Bazaar Gateway Source Catalogue: providers, wire formats, outbound proxies, free-token terms (as of 2026-10-24)

Scope: candidate default model sources and gateways for Bazaar.Server's own model gateway (per-agent whitelists enforced by Bazaar, pluggable providers). Built on the two existing notes, which were read first: `docs/research/notes/Bazaar stack feasibility research/ai_gateway_models.md` and `docs/research/notes/FreeLLMAPI gateway evaluation/freellmapi_evaluation.md`. This pass fetched about 30 primary pages, and anything I did not fetch is marked unverified. Not legal advice. Nothing was run or installed.

**Evidence labels.** "Verified (primary)" means I read the page through WebFetch in this pass. WebFetch returns a model-summarised rendering, so quotes are summaries unless stated otherwise, and figures should be re-checked against the raw page before they go into a public document. "Carried (prior note)" means the fact comes from one of the two existing notes, which were themselves primary-read but not re-read here. "Secondary" means blogs, aggregators, search summaries or a third party's review. "Unverified" means not confirmed from any source I could read.

**Bottom line.**
1. The free-token landscape changed under the prior notes. GitHub Models was fully retired on 2026-07-30 (verified primary), so it cannot be a default source. Several free tiers now publish small numbers (Cerebras free trial 5 RPM; OpenRouter `:free` 20 RPM and 50 or 1,000 RPD depending on purchased credits; Cloudflare Workers AI 10,000 neurons/day). Gemini's per-model free numbers are not on Google's rate-limit page at all.
2. Four wire formats matter: OpenAI Chat Completions (the majority), Anthropic Messages (Anthropic native), Gemini native `generateContent`, and Ollama's native API (only if used). The OpenAI-compatible shims from Anthropic and Gemini are beta or test-only and drop features, so the gateway should speak the native formats for those two lanes.
3. .NET 10 has no SOCKS-specific API on `WebProxy`. Whether `SocketsHttpHandler.Proxy` accepts `socks5://` URIs is not confirmed by a primary page I could read. Make it a spike gate, not a design assumption.
4. Recommended shape: one `HttpClient` (one `SocketsHttpHandler`) per provider per outbound proxy, one operator-owned account per provider, provider keys referenced from a secret store, and per-agent allowlists enforced by Bazaar, never by the upstream.

---

## 1. Categories and members: which sources to support by default

### Takeaway
A default list should have four tiers: (a) first-party labs with a paid API that Bazaar can use directly (Anthropic, OpenAI, Google Gemini API, DeepSeek, Mistral and others); (b) aggregators with free or cheap OpenAI-compatible access (OpenRouter, Hugging Face Inference Providers, Groq, Cerebras, NVIDIA NIM, Cloudflare Workers AI); (c) self-hosted gateways that Bazaar can sit in front of or replace (LiteLLM, OmniRoute, FreeLLMAPI, Portkey, Helicone); (d) local runtimes (Ollama, LM Studio, llama.cpp server, vLLM). Only items marked "Verified (primary)" below have documented limits or endpoints in this pass. Many members are still unverified and need a primary read before any default is set.

### Cited Findings

**First-party labs**
- Anthropic. Rate-limit page (fetched 2026-10-24) lists Start, Build and Scale tiers with monthly spend caps of $500, $1,000 and $200,000 respectively, and an "Evaluation" starting tier for new organisations. Limits are per organisation, measured as RPM, ITPM and OTPM per model class. The Start-tier row shows `Claude Haiku 5.5` at 1,000 RPM, 2,000,000 ITPM and 400,000 OTPM. The same table lists Claude Sonnet 5.5, Opus 5.5, Fable 5.x and Mythos 5.x, which resolves part of the model-name conflict in the feasibility notes (section 5 there). The page does not describe a free API tier. Verified (primary): [platform.claude.com/docs/en/api/rate-limits](https://platform.claude.com/docs/en/api/rate-limits). Note: the page redirects from docs.anthropic.com.
- Anthropic's footnote says the Fable rate limit applies to "Claude Fable 5.1 and Claude Fable 5". That confirms the model appears in Anthropic's own table. It does not confirm availability for a given account. Verified (primary) for the table only; availability unverified.
- OpenAI. The OpenAI rate-limit page (developers.openai.com, fetched 2026-10-24) is summarised as stating: a Free tier with a "$100/month usage limit", qualification "User must be in an allowed geography", rate limits "defined at the organization level" and "at the project level, not user level", and three paid tiers named Build, Launch and Grow. The page shows no free-tier RPM or TPM values. Verified (primary, summarised): [developers.openai.com/api/docs/guides/rate-limits](https://developers.openai.com/api/docs/guides/rate-limits). Treat the $100 figure as needing a raw-page check because it does not match what the earlier pass knew of OpenAI's free access.
- Google Gemini API. The rate-limits page (last updated 2026-10-15 UTC) does not list numeric limits. It says limits "depend on a variety of factors (such as your usage tier) and can be viewed in Google AI Studio." Paid tiers: Tier 1 requires an active billing account (billing cap $250); Tier 2 requires $100 paid plus 3 days; Tier 3 requires $1,000 paid plus 30 days. Verified (primary, summarised): [ai.google.dev/gemini-api/docs/rate-limits](https://ai.google.dev/gemini-api/docs/rate-limits). Carried (prior note, primary read by earlier agent): the Gemini CLI quota page gives "free Gemini API key 250/day on Flash only", last updated 2026-06-18 — [geminicli.com/docs/resources/quota-and-pricing](https://geminicli.com/docs/resources/quota-and-pricing/). That page is about the CLI and is not the API's official free quota, so treat 250/day as CLI-documented, not API-documented.
- DeepSeek, Mistral, xAI, Cohere, Zhipu (Z.ai) and others. Mistral: the page I tried at `docs.mistral.ai/deployment/laplateforme/tier` returned 404, and the models overview did not show a free tier. Unverified. DeepSeek: the prior note lists `deepseek-v4-flash` and `deepseek-v4-pro` IDs from third-party sites only. Unverified against api-docs.deepseek.com. The others were not fetched in this pass.

**Aggregators and fast inference hosts**
- OpenRouter. Free `:free` model limits: fewer than 10 credits purchased (all time) gives 20 requests per minute and 50 requests per day; at least 10 credits purchased gives 20 RPM and 1,000 RPD. The tier is selected by all-time credits purchased. A platform limit returns HTTP 429 with `X-RateLimit-*` headers. Upstream provider limits are reported separately. The page shows no publication date. Verified (primary, summarised): [openrouter.ai/docs/api/reference/limits](https://openrouter.ai/docs/api/reference/limits). Practical note: a one-off credit purchase changes the daily ceiling from 50 to 1,000 on the free models. BYOK: "Your provider keys are securely encrypted and used for all requests routed through the specified provider." BYOK requests are not gated by the free daily counter. Custom provider keys cost 5% of the normal price, deducted from OpenRouter credits, with a plan-dependent free allowance. Verified (primary, summarised): [openrouter.ai/docs/guides/overview/auth/byok](https://openrouter.ai/docs/guides/overview/auth/byok). The page does not say whether provider terms allow one key to serve several people or accounts.
- Hugging Face Inference Providers. Free users have no monthly included credits and must buy credits; PRO users get $2.00 per month; Team and Enterprise get $2.00 per seat. Routed requests carry no markup. "Custom Provider Key" requests are billed by the provider and do not get HF credits. The OpenAI-compatible router base is `https://router.huggingface.co/v1`, and an `X-HF-Bill-To` header bills an organisation. Verified (primary, summarised): [huggingface.co/docs/inference-providers/pricing](https://huggingface.co/docs/inference-providers/pricing). Note the page's own "200+ models" claim is marketing copy, not a limit.
- Groq. The rate-limits page states that limits "apply at the organization level, not individual users" and are for "protection against misuse". Its table covers only the Developer plan; the free-plan figures are not in the text fetched. The Groq pricing page fetched gives no free-tier terms. Verified (primary) for per-organisation scope only: [console.groq.com/docs/rate-limits](https://console.groq.com/docs/rate-limits). Free-tier figures: unverified.
- Cerebras Inference. Free trial table: `gpt-oss-120b` and `qwen-3.8-27b` each 5 RPM, 30K uncached TPM (90K total TPM), 1M TPH and 1M TPD. The page does not list RPH or RPD for these models, and it does not mention key sharing. No date shown. Verified (primary, summarised): [inference-docs.cerebras.ai/support/rate-limits](https://inference-docs.cerebras.ai/support/rate-limits). Carried (secondary, FreeLLMAPI project's own review): Cerebras "API keys may not be sold or transferred".
- NVIDIA NIM (build.nvidia.com). Carried (prior note): the free endpoint is governed by "NVIDIA API Trial Terms of Service"; the ~40 RPM figure is from forum posts, not an official page; the "~1,000 requests/day" figure in the design docs is unsourced. Verified (primary, carried): [build.nvidia.com/deepseek-ai/deepseek-v4-pro](https://build.nvidia.com/deepseek-ai/deepseek-v4-pro). The Trial Terms document was not fetched; unverified.
- Cloudflare Workers AI. Free allocation "10,000 Neurons per day at no charge"; paid rate "$0.011 / 1,000 Neurons" above that on Workers Paid; the Workers Free plan has the same 10,000 neurons/day cap. Last updated 2026-10-20 UTC. Verified (primary, summarised): [developers.cloudflare.com/workers-ai/platform/pricing](https://developers.cloudflare.com/workers-ai/platform/pricing/). Terms on pooling: unverified. The FreeLLMAPI project rates Cloudflare "Ambiguous" (secondary, see section 4).
- GitHub Models. Retired. "As of July 30, 2026, GitHub Models has been fully retired." The playground, catalogue, inference API and BYOK are all gone; GitHub points users to Azure AI Foundry and GitHub Copilot. Verified (primary): [docs.github.com/en/github-models/use-github-models/prototyping-with-ai-models](https://docs.github.com/en/github-models/use-github-models/prototyping-with-ai-models) (the URL now serves the retirement notice). Do not list it as a source. FreeLLMAPI v0.16.0 also removed it (carried, prior note).

**Self-hosted gateways and proxies**
- LiteLLM. Footer shows an MIT licence link and "© 2026 LiteLLM"; states "LiteLLM Proxy is OpenAI compatible"; lists `/v1/messages` as a passthrough endpoint for Anthropic; uses a `config.yaml` with `model_list`, `model_name` and `litellm_params`; reads `ANTHROPIC_API_BASE` (also `ANTHROPIC_BASE_URL`). Verified (primary, summarised): [docs.litellm.ai/docs/providers/anthropic](https://docs.litellm.ai/docs/providers/anthropic). Its own virtual-key model is a possible comparison point for Bazaar's per-agent whitelist, but I did not verify its per-key allowlist behaviour.
- OmniRoute, FreeLLMAPI. Carried (prior notes): OmniRoute is MIT and documents per-key `forcedModel` and allowed-model lists, with `tos: avoid` and `auto/subscription` markers. FreeLLMAPI is MIT, documented as "Personal experimentation only", "not production", single-user, and does not document per-agent model allowlists or Anthropic pass-through. See the two prior notes for details and their gaps. Not re-verified here.
- Portkey, Helicone, Cloudflare AI Gateway. Not fetched in this pass. Unverified. Listed as candidates only.

**Local runtimes**
- Ollama. OpenAI-compatible base URL `http://localhost:11434/v1`; the API key is required by client libraries but ignored ("required, but unused"); Chat Completions is the documented compatibility surface. Dated 2024-02-08. Verified (primary, summarised): [ollama.com/blog/openai-compatibility](https://ollama.com/blog/openai-compatibility). Whether the current Ollama release also serves Anthropic Messages was not confirmed.
- LM Studio, llama.cpp server, vLLM. Not fetched in this pass. Unverified. These are widely documented as OpenAI-compatible servers; check their own docs before relying on it.

### Inferences
- A default list that is safe to ship should contain only providers whose free or paid access is documented on a primary page with numbers, or is a self-hosted gateway. Today that is: Anthropic (paid only, no free tier documented), Gemini API (billing-dependent tiers), OpenRouter (free `:free` models with published daily caps), Hugging Face (credits), Cerebras (free trial), Cloudflare Workers AI (free neurons), plus local runtimes.
- Free tiers are per account and per organisation. Several agents on one key share one quota. The gateway must count against the provider key, not against the agent.
- Removing GitHub Models is a correction to older design material, which lists it as a source.

### Gaps
- Groq free-tier numbers: the page text did not include the free plan table. Check the limits page inside the Groq console.
- Mistral, DeepSeek, xAI, Together, Fireworks, Cohere, Cloudflare AI Gateway, Portkey, Helicone, vLLM, llama.cpp, LM Studio: not fetched in this pass. Each needs a primary read of its docs and terms.
- NVIDIA NIM's current free numbers and Trial Terms: not verified; prior-note claims stay carried.
- OpenAI's $100/month free-tier claim: a summarised fetch only. Re-check the raw page.

---

## 2. Which sources need non-OpenAI wire formats

### Takeaway
Plain OpenAI Chat Completions covers most aggregators and local runtimes. Anthropic Messages and Gemini `generateContent` must be implemented natively for the Claude and Gemini lanes, because the official OpenAI-compatible shims are documented as test-only (Anthropic) or beta (Gemini) and drop fields that matter for agents: prompt caching, thinking, strict tool schemas, and in Anthropic's case audio, `response_format` and more.

### Cited Findings
- Anthropic OpenAI SDK compatibility: base URL `https://api.anthropic.com/v1/`; "This compatibility layer is primarily intended to test and compare model capabilities, and is not considered a long-term or production-ready solution." Ignored or unsupported: `strict` on tools, prompt caching, `response_format`, `logprobs`, `seed`, `service_tier`, `user`, and most of `metadata`. System and developer messages are hoisted into one top-level `system` field. "Thinking" is accepted but the SDK does not return thinking blocks. Verified (primary, summarised): [platform.claude.com/docs/en/cli-sdks-libraries/libraries/openai-sdk](https://platform.claude.com/docs/en/cli-sdks-libraries/libraries/openai-sdk). The page also states that `anthropic-workspace-id` is needed for personal or service-account keys not scoped to a workspace.
- Gemini OpenAI compatibility: base URL `https://generativelanguage.googleapis.com/v1beta/openai/`; auth `Authorization: Bearer $GEMINI_API_KEY`; endpoints `/chat/completions`, `/embeddings`, `/models`, `/models/{model}`, plus image generation and Batch jobs. "Support for the OpenAI libraries is still in beta." Batch file upload and download are not supported through this layer. Last updated 2026-10-22 UTC. Verified (primary, summarised): [ai.google.dev/gemini-api/docs/openai](https://ai.google.dev/gemini-api/docs/openai). Native `generateContent` is not in this page; use the native API for the Gemini lane.
- Anthropic native Messages: carried (prior note, FreeLLMAPI API reference): FreeLLMAPI exposes `/v1/messages` and `/v1beta` Gemini routes and accepts `x-api-key` or Bearer on Messages. The Anthropic native path was not fetched in this pass; treat `POST https://api.anthropic.com/v1/messages` as unverified here, though it is the standard path shown in the OpenAI-SDK page's native-API link.
- Hugging Face router: OpenAI client used with `base_url="https://router.huggingface.co/v1"` (verified, pricing page sample). Plain OpenAI-compatible.
- OpenRouter: `GET /api/v1/key` documented (verified, limits page). Plain OpenAI-compatible as far as the pages show; the chat path itself was not read. Treat as OpenAI-compatible, label unverified for the chat path.
- Ollama: Chat Completions compatibility, plus its own native API (not read in this pass).
- LiteLLM: OpenAI-compatible, with an Anthropic passthrough route (verified, section 1).

| Source | Wire format | Status in this pass |
|---|---|---|
| Anthropic | Anthropic Messages (native); OpenAI SDK shim is test-only | Verified (primary) for shim limits; native path carried |
| Gemini API | Gemini native `generateContent`; OpenAI shim in beta | Verified (primary) for shim base and limits |
| OpenRouter | OpenAI Chat Completions (`/api/v1`) | Partly verified (`/api/v1/key`); chat path unverified |
| Hugging Face router | OpenAI Chat Completions (`router.huggingface.co/v1`) | Verified (primary, sample) |
| Ollama | OpenAI Chat Completions (`localhost:11434/v1`) + native | Verified (primary, 2024 post) |
| LiteLLM | OpenAI Chat Completions + Anthropic passthrough | Verified (primary) |
| FreeLLMAPI | OpenAI, Anthropic Messages, Gemini `/v1beta` | Carried (prior note) |
| Cerebras, Groq, NVIDIA NIM, Mistral, DeepSeek, Cloudflare Workers AI, Cohere, LM Studio, llama.cpp, vLLM | Assumed OpenAI-compatible, not verified in this pass | Unverified |

### Inferences
- The gateway should support three adapter families: `openai_chat` (default, covers most sources), `anthropic_messages` (native), and `gemini_generate_content` (native). Ollama's native API is optional.
- The Anthropic lane must not be routed through the Anthropic OpenAI-SDK shim, because it drops fields and is documented as not production-ready (verified).
- Tool-call and thinking semantics differ across families. Keep the agent-facing contract in Bazaar's own types and translate at the adapter edge.

### Gaps
- Chat-path details for OpenRouter, Groq, Cerebras, NIM, Mistral, DeepSeek, Cloudflare and the local runtimes: not read.
- Native Anthropic and Gemini request and header details were not re-read from primary pages in this pass.

---

## 3. Outbound network proxies from .NET (HTTP, HTTPS, SOCKS5)

### Takeaway
.NET 10 can route `HttpClient` through a proxy by setting `SocketsHttpHandler.Proxy` to an `IWebProxy` (verified). The documented `WebProxy` class is described as "HTTP proxy settings" and does not list SOCKS; SOCKS5 support for HttpClient is claimed in a dotnet-api-docs pull request description and a secondary article, but I could not confirm it on a merged primary page. Treat SOCKS5 as a spike gate on .NET 10 before it goes in the design.

### Cited Findings
- `SocketsHttpHandler.Proxy` is "Gets or sets the custom proxy when the UseProxy property is true." Its type is `System.Net.IWebProxy`, and it applies to .NET 5 through 11 in the page's monikers. Verified (primary): [learn.microsoft.com/dotnet/api/system.net.http.socketshttphandler.proxy](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.proxy?view=net-10.0).
- `WebProxy` is described as "Contains HTTP proxy settings for the HttpClient class." Its constructors accept a `Uri` or string, a bypass flag, a bypass list, and `ICredentials` (for example `WebProxy(Uri, Boolean, String[], ICredentials)`). Its properties include `Credentials`, `UseDefaultCredentials`, `BypassList` and `BypassProxyOnLocal`. The page does not list SOCKS as a supported scheme. Verified (primary): [learn.microsoft.com/dotnet/api/system.net.webproxy](https://learn.microsoft.com/en-us/dotnet/api/system.net.webproxy?view=net-10.0).
- Guidance on one client per configuration: "Using more than one instance is necessary for scenarios with multiple proxies." The page recommends long-lived clients with `PooledConnectionLifetime` (e.g. 2 to 15 minutes) for DNS changes, and an `IHttpClientFactory` option. Verified (primary): [learn.microsoft.com/dotnet/fundamentals/networking/http/httpclient-guidelines](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines). Relevant to per-provider proxies: one `SocketsHttpHandler` per proxy/provider pair.
- SOCKS formats: a dotnet-api-docs PR (#10600) says the docs "were enhanced to include the supported proxy formats" `https`, `socks4`, `socks4a` and `socks5`; it says it fixes dotnet/runtime#102441 "HttpClient doesn't support proxy server in HTTPS protocol in Linux". The PR page does not show the merged text, does not state the .NET version, and the author asked whether to document .NET 7 and 8 behaviour. Verified (primary PR text, unmerged status unknown): [github.com/dotnet/dotnet-api-docs/pull/10600](https://github.com/dotnet/dotnet-api-docs/pull/10600).
- A secondary article says SOCKS proxy support arrived in .NET 6 and is used through the same proxy setup as HTTP. Secondary (search summary of [dotnetcoretutorials.com/socks-proxy-support-in-net](https://dotnetcoretutorials.com/socks-proxy-support-in-net/)); not verified against a primary release note.
- Third-party SOCKS libraries: SocksSharp (wiki says it "provides support for Socks4/4a/5 proxy servers to HttpClient") and GeekTools.Socks5Proxy. The SocksSharp repository is reported archived in 2020 (secondary search summary). Not evaluated.
- Cloudflare Tunnel: not fetched. It exposes an inbound service to the internet through an outbound tunnel from the host. It is not an outbound egress proxy for provider calls. Unverified in this pass.

### Inferences
- Support per provider: `none`, `http`, `https` (TLS to the proxy), `socks5` (only after the .NET 10 spike passes). Credentials go in a secret reference, not in the URI.
- A corporate proxy is usually HTTP CONNECT with optional basic auth, which `WebProxy` handles through `Credentials`. Verify NTLM or Kerberos needs separately.
- Residential or rotating proxies exist, but using them to change apparent location or to multiply quota likely conflicts with provider terms. Bazaar should not offer that as a feature. Operator egress through a corporate or home network is the defensible use. The provider terms on proxy use were not checked (unverified).
- Cloudflare Tunnel belongs in a separate "exposure" section (for example, letting a home Ollama or LM Studio box be reached from a server), not in the proxy field.

### Gaps
- Whether `SocketsHttpHandler` on .NET 10 accepts `socks5://` in `WebProxy` (a runnable spike is needed; not run here).
- Whether `https://` proxy URIs work on Linux (dotnet/runtime#102441 suggests an issue; current status unverified).
- NTLM/Kerberos proxy authentication behaviour on .NET 10: not verified.

---

## 4. Free-token sourcing: multiple keys, pooling, and risk classes

### Takeaway
A clear split: a single operator-owned account per provider, used inside published limits, is the defensible default. Multiple accounts to multiply free quota, shared or resold keys, subscription OAuth credentials, and proxies used to evade limits or regional rules are the risky classes. Provider terms on multiple accounts and on key sharing were not read for most providers, so the list of "allowed" sources is short.

### Cited Findings
- OpenRouter lets an account configure "multiple BYOK keys for the same provider", and a "member filter" can restrict which workspace members use a key. The page does not say whether provider terms allow one key to serve several people. Verified (primary, summarised): [openrouter.ai/docs/guides/overview/auth/byok](https://openrouter.ai/docs/guides/overview/auth/byok).
- FreeLLMAPI (carried, prior note; its own review, secondary): "one account per provider" is its rule of thumb. Its verdict table says Cerebras "API keys may not be sold or transferred", Cohere "exclude 'personal, family or household purposes'", Google Gemini "Caution" with a March 2026 ToS "professional or business purposes, not for consumer use", NVIDIA NIM "Caution" ("evaluation only, not production"), and Cloudflare Workers AI "Ambiguous". These are a project's self-assessment for personal use. They are not provider terms. Source: [docs/en/architecture/00-high-level-index.md](https://github.com/tashfeenahmed/freellmapi/blob/main/docs/en/architecture/00-high-level-index.md) (carried, prior note).
- Anthropic (carried, prior note; primary read by earlier agent): "Customers may not pay for, resell, or intermediate Claude usage on their end users' behalf"; "Anthropic does not permit third-party developers ... to route requests through Free, Pro, or Max plan credentials on behalf of their users." Each end user must use their own API key or subscription. [code.claude.com/docs/en/legal-and-compliance](https://code.claude.com/docs/en/legal-and-compliance).
- Gemini quota pooling across Antigravity and Gemini CLI is undocumented by Google (carried; the prior note cites forum reports only).
- GitHub Models retired (verified, section 1). Several older lists still include it.
- Free-tier scope: OpenAI's summarised page limits its $100 free tier to "allowed geography" (verified, summarised; re-check raw).

### Inferences
- Risk classes for Bazaar:
  - Allowed by default (still check each provider's current terms): one operator-owned account per provider; use inside published limits; keys held in a secret store; one `HttpClient` per proxy.
  - Needs written provider-terms review before enabling: several accounts for one provider; any key used by more than one legal person or company; the free tiers of Google and NVIDIA, which the project's own review rates Caution; Cohere, which the review rates Avoid for non-personal use.
  - Do not build: subscription OAuth (Claude Pro or Max, Gemini CLI or Antigravity OAuth) in any gateway (carried, Anthropic terms); proxies or IP rotation to evade limits or regional rules; scraping web sessions or consumer web endpoints.
- Quota design: the gateway must track usage per provider key and per model, read the 429 and `Retry-After` headers, and back off. Per-agent budgets belong in Bazaar, not in the provider.
- A one-off purchase of OpenRouter credits (10 or more, verified) lifts the free `:free` daily cap from 50 to 1,000 requests. Whether that is worth buying is a cost decision for the owner.

### Gaps
- Provider terms (not the project's review) for multiple accounts, key sharing and pooling: not read for any provider in this pass.
- Groq, Cerebras, Mistral, Cloudflare, NIM and OpenRouter terms of service: not read.
- Whether Gemini's free API data is used for product training: the rate-limit page did not address it (verified absence; source for the Gemini terms not fetched).

---

## 5. Recommended provider and proxy configuration shape (PROPOSAL, not decided)

**Status: PROPOSAL.** This is a suggested shape for discussion. It is not a DECIDED item in `docs/deep-dives/`. It has not been tested against Bazaar.Server code, and the SOCKS field depends on the spike in section 3. Placeholder values only. No real keys, hosts or account names.

Principles behind the shape:
- Bazaar enforces per-agent whitelists. Providers never see agent identity; the gateway sends each agent's request with a single provider key.
- One provider entry per (provider, account). Multiple accounts are separate entries with separate secret references, and are off by default.
- Native adapters for `anthropic_messages` and `gemini_generate_content`; `openai_chat` for the rest.
- Outbound proxy is per provider, with `none` as the default.
- Health checks use cheap endpoints (for example, the model list), not paid completions.

```json
{
  "_status": "PROPOSAL (not decided). Placeholder values only.",
  "gateway": {
    "defaultProxy": "none",
    "requireAgentAllowlist": true,
    "unknownModelPolicy": "deny"
  },
  "providers": [
    {
      "id": "anthropic-direct",
      "adapter": "anthropic_messages",
      "baseUrl": "https://api.anthropic.com",
      "auth": { "scheme": "x-api-key", "secretRef": "secret://bazaar/providers/anthropic-direct/api-key" },
      "headers": { "anthropic-version": "2023-06-01" },
      "modelCatalog": { "source": "static", "models": ["claude-haiku-5-5"] },
      "outboundProxy": { "ref": "none" },
      "limits": { "rpm": 1000, "concurrency": 8, "source": "provider-response-headers" },
      "health": { "method": "GET", "path": "/v1/models", "intervalSeconds": 300 },
      "freeTier": false,
      "pooling": "forbidden",
      "fallbackGroup": "claude"
    },
    {
      "id": "gemini-direct",
      "adapter": "gemini_generate_content",
      "baseUrl": "https://generativelanguage.googleapis.com/v1beta",
      "auth": { "scheme": "x-goog-api-key", "secretRef": "secret://bazaar/providers/gemini-direct/api-key" },
      "modelCatalog": { "source": "remote", "path": "/models", "refreshHours": 24 },
      "outboundProxy": { "ref": "none" },
      "limits": { "source": "provider-response-headers", "concurrency": 4 },
      "health": { "method": "GET", "path": "/models", "intervalSeconds": 600 },
      "freeTier": "billing-dependent",
      "pooling": "forbidden",
      "fallbackGroup": "gemini"
    },
    {
      "id": "openrouter-main",
      "adapter": "openai_chat",
      "baseUrl": "https://openrouter.ai/api/v1",
      "auth": { "scheme": "bearer", "secretRef": "secret://bazaar/providers/openrouter-main/api-key" },
      "headers": { "HTTP-Referer": "https://example.invalid/bazaar", "X-Title": "Bazaar" },
      "modelCatalog": { "source": "remote", "path": "/models", "refreshHours": 24 },
      "outboundProxy": { "ref": "corp-egress" },
      "limits": { "rpm": 20, "rpd": 50, "rpdAfterCreditsPurchased": 1000, "concurrency": 4 },
      "health": { "method": "GET", "path": "/key", "intervalSeconds": 900 },
      "freeTier": true,
      "pooling": "forbidden",
      "fallbackGroup": "cheap"
    },
    {
      "id": "cerebras-trial",
      "adapter": "openai_chat",
      "baseUrl": "https://api.cerebras.ai/v1",
      "auth": { "scheme": "bearer", "secretRef": "secret://bazaar/providers/cerebras-trial/api-key" },
      "modelCatalog": { "source": "remote", "path": "/models", "refreshHours": 24 },
      "outboundProxy": { "ref": "none" },
      "limits": { "rpm": 5, "tpm": 90000, "tpd": 1000000, "concurrency": 1 },
      "health": { "method": "GET", "path": "/models", "intervalSeconds": 900 },
      "freeTier": true,
      "pooling": "forbidden",
      "fallbackGroup": "cheap"
    },
    {
      "id": "ollama-local",
      "adapter": "openai_chat",
      "baseUrl": "http://localhost:11434/v1",
      "auth": { "scheme": "none" },
      "modelCatalog": { "source": "remote", "path": "/models", "refreshHours": 1 },
      "outboundProxy": { "ref": "none" },
      "limits": { "concurrency": 2 },
      "health": { "method": "GET", "path": "/models", "intervalSeconds": 60 },
      "freeTier": "local",
      "pooling": "not-applicable",
      "fallbackGroup": "local"
    }
  ],
  "outboundProxies": {
    "none": { "scheme": null },
    "corp-egress": {
      "scheme": "http",
      "uri": "http://proxy.corp.example.invalid:8080",
      "credentialsSecretRef": "secret://bazaar/proxies/corp-egress/basic",
      "bypassList": ["localhost", "127.0.0.1", "*.internal.example.invalid"],
      "bypassOnLocal": true
    },
    "socks-spike-only": {
      "scheme": "socks5",
      "uri": "socks5://socks.example.invalid:1080",
      "status": "BLOCKED until .NET 10 spike passes",
      "credentialsSecretRef": "secret://bazaar/proxies/socks-spike-only/basic"
    }
  },
  "fallbackGroups": {
    "claude": ["anthropic-direct"],
    "gemini": ["gemini-direct", "openrouter-main"],
    "cheap": ["openrouter-main", "cerebras-trial"],
    "local": ["ollama-local"]
  },
  "agentWhitelists": {
    "_note": "Enforced by Bazaar.Server before any provider call. Model IDs are provider IDs, not marketing names.",
    "example-agent": { "allowedModels": ["anthropic-direct:claude-haiku-5-5"], "fallbackWithinGroupOnly": true }
  }
}
```

Field notes:
- `auth.scheme` values: `bearer` (OpenAI-compatible, OpenRouter, HF, Gemini's OpenAI shim), `x-api-key` (Anthropic native), `x-goog-api-key` (Gemini native; FreeLLMAPI's Gemini route uses this header per prior note), `none` (Ollama).
- `limits`: values on OpenRouter and Cerebras come from the verified table above; the Anthropic and Gemini limits are read from response headers or the console, not hard-coded.
- `freeTier` and `pooling` are Bazaar policy flags. The gateway refuses to load a provider with `pooling: "allowed"` unless the owner has signed off in config.
- `fallbackWithinGroupOnly`: a per-agent setting that stops a fallback from crossing into a provider or model family the agent is not allowed to use. The prior note found FreeLLMAPI does not document a family lock for chat, so Bazaar must enforce this itself.
- `health` paths: `/v1/models`, `/key` and `/models` are documented or commonly exposed, but only OpenRouter's `/api/v1/key` and Gemini's `/models` are primary-verified in this pass. Confirm the others before use.

### Inferences
- The shape keeps the Claude lane on direct Anthropic keys only, consistent with the existing notes and Anthropic's terms (carried).
- The `cheap` group mixes two free sources with different limit models; the per-provider `limits` object is needed so the scheduler does not treat 5 RPM and 20 RPM the same.

### Gaps
- No runnable test of this config against Bazaar code. No spike of `socks5://` on .NET 10.
- Cerebras and OpenRouter base URL and chat path are not verified in this pass (`api.cerebras.ai/v1` is an assumption).

---

## Sources (all accessed 2026-10-24 unless stated)
- Anthropic rate limits: https://platform.claude.com/docs/en/api/rate-limits (redirected from docs.anthropic.com/en/api/rate-limits)
- Anthropic OpenAI SDK compatibility: https://platform.claude.com/docs/en/cli-sdks-libraries/libraries/openai-sdk
- Anthropic Claude Code legal and compliance (carried): https://code.claude.com/docs/en/legal-and-compliance
- OpenAI rate limits (summarised): https://developers.openai.com/api/docs/guides/rate-limits
- Google Gemini rate limits (last updated 2026-10-15): https://ai.google.dev/gemini-api/docs/rate-limits
- Google Gemini OpenAI compatibility (last updated 2026-10-22): https://ai.google.dev/gemini-api/docs/openai
- Gemini CLI quota and pricing (carried; last updated 2026-06-18): https://geminicli.com/docs/resources/quota-and-pricing/
- OpenRouter limits: https://openrouter.ai/docs/api/reference/limits
- OpenRouter BYOK: https://openrouter.ai/docs/guides/overview/auth/byok
- Hugging Face Inference Providers pricing: https://huggingface.co/docs/inference-providers/pricing
- Groq rate limits: https://console.groq.com/docs/rate-limits
- Groq pricing (no free-tier terms in fetched text): https://groq.com/pricing
- Cerebras rate limits: https://inference-docs.cerebras.ai/support/rate-limits
- Cloudflare Workers AI pricing (last updated 2026-10-20): https://developers.cloudflare.com/workers-ai/platform/pricing/
- GitHub Models retirement notice: https://docs.github.com/en/github-models/use-github-models/prototyping-with-ai-models
- NVIDIA build.nvidia.com DeepSeek V4 Pro (carried): https://build.nvidia.com/deepseek-ai/deepseek-v4-pro
- LiteLLM Anthropic provider docs: https://docs.litellm.ai/docs/providers/anthropic
- Ollama OpenAI compatibility (dated 2024-02-08): https://ollama.com/blog/openai-compatibility
- .NET SocketsHttpHandler.Proxy (net-10.0): https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.proxy?view=net-10.0
- .NET WebProxy (net-10.0): https://learn.microsoft.com/en-us/dotnet/api/system.net.webproxy?view=net-10.0
- .NET HttpClient guidelines (updated 2026-08-06): https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines
- dotnet-api-docs PR 10600 (SOCKS formats in docs; unmerged status unknown): https://github.com/dotnet/dotnet-api-docs/pull/10600
- Secondary (search summary, not fetched): https://dotnetcoretutorials.com/socks-proxy-support-in-net/ ; https://github.com/extremecodetv/SocksSharp
- FreeLLMAPI ToS review (carried, project self-assessment): https://github.com/tashfeenahmed/freellmapi/blob/main/docs/en/architecture/00-high-level-index.md
- Existing notes: `docs/research/notes/Bazaar stack feasibility research/ai_gateway_models.md`; `docs/research/notes/FreeLLMAPI gateway evaluation/freellmapi_evaluation.md`

## Unverified or not obtained (summary)
- Mistral, DeepSeek, xAI, Together, Fireworks, Cohere, Cloudflare AI Gateway, Portkey, Helicone, vLLM, llama.cpp, LM Studio: no primary read in this pass.
- Groq free-tier numbers; NVIDIA NIM free numbers and Trial Terms (carried only).
- Native Anthropic Messages and Gemini `generateContent` request details (not re-read in this pass).
- Provider terms on multiple accounts, key sharing and pooling for every provider.
- Whether .NET 10 `SocketsHttpHandler` accepts `socks5://` via `WebProxy` (needs a spike).
- Whether Anthropic's Fable model is available to a given account (only the rate-limit table was read).
- Residential or rotating-proxy terms; Cloudflare Tunnel details.
- OmniRoute and FreeLLMAPI details beyond the two prior notes.
