# OpenCode provider system and models.dev registry (as of 2026-10-24)

Scope: how OpenCode defines providers, models, auth and proxies; what models.dev is and whether it is usable as a default catalogue; licences. Sources were fetched on 2026-10-24. Fetched pages were read through a summarising fetcher, so exact wording is paraphrased where not quoted; "verified" below means the primary page was read, not that every value was hand-checked against raw HTML. Not legal advice.

Primary URLs used:
- OpenCode providers docs: https://opencode.ai/docs/providers/ (page states "Last updated: Oct 24, 2026")
- OpenCode config docs: https://opencode.ai/docs/config/
- OpenCode models docs: https://opencode.ai/docs/models/ (page states "Last updated: Oct 24, 2026")
- OpenCode config JSON schema: https://opencode.ai/config.json
- OpenCode repo README: https://github.com/anomalyco/opencode
- OpenCode LICENSE: https://github.com/anomalyco/opencode/blob/dev/LICENSE
- models.dev site: https://models.dev
- models.dev API: https://models.dev/api.json
- models.dev repo README: https://github.com/sst/models.dev
- models.dev LICENSE: https://github.com/sst/models.dev/blob/dev/LICENSE

Secondary (used only for context, low weight): FreeLLMAPI listings from search snippets (awesome.ecosyste.ms, betterstack, newreleases.io, gittrend.io, ossinsight.io). These disagree with each other and are not primary.

## 1. How OpenCode defines providers (config format, schema, auth, env vars)

### Takeaway
OpenCode uses a JSON config (`opencode.json`) with a `provider` map keyed by provider ID. Each provider names an AI SDK adapter package (`npm`), a `baseURL`, optional `apiKey` via `{env:...}` substitution, and a `models` map. The schema is in transition: the config schema marks `provider` as deprecated in favour of a newer `providers` block using `package` instead of `npm`. Bazaar should copy the concept, not the exact keys, until OpenCode's v2 settles.

### Cited Findings
- Config files are merged, not replaced; precedence from lowest to highest: remote `.well-known/opencode`, global `~/.config/opencode/opencode.json`, file at `OPENCODE_CONFIG`, project `opencode.json` (walks up to the nearest Git dir), `.opencode` dirs, inline `OPENCODE_CONFIG_CONTENT`, managed config files, macOS managed preferences (highest). `OPENCODE_CONFIG_DIR` sets an extra directory. — [OpenCode config docs](https://opencode.ai/docs/config/) (verified, primary)
- Top-level provider-related keys: `provider`, `model` (format `provider/model`), `small_model` (for lightweight tasks such as title generation; if unset OpenCode uses a cheaper model when available, otherwise the main model), `enabled_providers` (allowlist), `disabled_providers` (takes priority over `enabled_providers`). — [OpenCode config docs](https://opencode.ai/docs/config/) (verified, primary)
- Per-provider `options` documented: `timeout` (ms, disabled by default), `headerTimeout` (default 300000), `chunkTimeout` (default 300000; `false` disables), `setCacheKey`, `apiKey` (e.g. `"{env:ANTHROPIC_API_KEY}"`). — [OpenCode config docs](https://opencode.ai/docs/config/) (verified, primary)
- Variable substitution: `{env:VARIABLE_NAME}` (empty string if unset) and `{file:path}` (path relative to config file dir, or absolute / `~`). — [OpenCode config docs](https://opencode.ai/docs/config/) (verified, primary)
- Provider block keys shown on the providers page: `npm` (AI SDK adapter, e.g. `"@ai-sdk/openai-compatible"` for `/v1/chat/completions`, `"@ai-sdk/openai"` for `/v1/responses`, `@ai-sdk/cerebras` as another example), `name` (UI display), `options.baseURL`, `options.apiKey`, `options.headers` (sent with each request), provider-specific `options` (Bedrock: `region`, `profile`, `endpoint` where `endpoint` is an alias for `baseURL` and wins if both set; OpenRouter `provider` routing with `order` and `allow_fallbacks`; Vercel `order`, `only`, `zeroDataRetention`), `models` (map of model IDs with `name`, `id`, `limit`). — [OpenCode providers docs](https://opencode.ai/docs/providers/) (verified, primary, via summarising fetch)
- Config schema, legacy `provider` block (marked deprecated): keys `api`, `name`, `env`, `id`, `npm`, `whitelist`, `blacklist`, `options` (`apiKey`, `baseURL`, `enterpriseUrl`, `setCacheKey`, `timeout`, `headerTimeout`, `chunkTimeout`), `models`. — [OpenCode config schema](https://opencode.ai/config.json) (verified, primary)
- Config schema, newer `providers` block (`Config.ProviderEncoded`): `canonical`, `name`, `env`, `package` (in place of `npm`), `compatibility`, `settings`, `headers`, `body`, `models`. — [OpenCode config schema](https://opencode.ai/config.json) (verified, primary)
- Custom OpenAI-compatible provider flow: run `/connect`, scroll to **Other**, enter a unique provider ID, enter API key (stores credential only); then add a provider block with `npm`, `name`, `options.baseURL`, `models`; then `/models` to select. Example base URL `https://api.myprovider.com/v1`. — [OpenCode providers docs](https://opencode.ai/docs/providers/) (verified, primary)
- Model selection: full ID is `provider_id/model_id`; for custom providers `provider_id` is the key under `provider` and `model_id` is the key under `provider.models`. Per-model options go under `provider.<p>.models.<m>.options` (e.g. `reasoningEffort`, `thinking.budgetTokens`). Built-in variants exist (Anthropic `high`, `max`; OpenAI `none` to `xhigh`; Google `low`, `high`); custom variants may override or add, and `"disabled": true` turns one off. — [OpenCode models docs](https://opencode.ai/docs/models/) (verified, primary)
- Startup model priority: `--model`/`-m` flag, then config `model`, then last used model, then "the first model using an internal priority". The page does not describe switching models on failure. — [OpenCode models docs](https://opencode.ai/docs/models/) (verified, primary)
- Auth storage: credentials from `/connect` are saved to `~/.local/share/opencode/auth.json`; `opencode auth list` shows configured credentials. — [OpenCode providers docs](https://opencode.ai/docs/providers/) (verified, primary)
- Login flows: most providers use API keys; OAuth or device login for OpenAI (ChatGPT Plus/Pro), GitHub Copilot (device code at `https://github.com/login/device`), GitLab Duo (OAuth or PAT), DigitalOcean (OAuth or Model Access Key), Snowflake Cortex (browser OAuth or PAT), xAI (SuperGrok subscription or API key). — [OpenCode providers docs](https://opencode.ai/docs/providers/) (verified, primary)
- The providers page states that Anthropic prohibits using Claude Pro/Max subscriptions through third-party plugins. — [OpenCode providers docs](https://opencode.ai/docs/providers/) (verified, primary). Relevant to Bazaar's rule that Pro/Max credentials never go through the gateway.
- Env var conventions named on the providers page (non-exhaustive, per provider): Bedrock `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_PROFILE`, `AWS_BEARER_TOKEN_BEDROCK`, `AWS_REGION`, `AWS_WEB_IDENTITY_TOKEN_FILE`, `AWS_ROLE_ARN`; Azure OpenAI `AZURE_RESOURCE_NAME`; Vertex `GOOGLE_APPLICATION_CREDENTIALS`, `GOOGLE_CLOUD_PROJECT`, `VERTEX_LOCATION`; GitLab `GITLAB_TOKEN`, `GITLAB_INSTANCE_URL`, `GITLAB_AI_GATEWAY_URL`; Cloudflare AI Gateway `CLOUDFLARE_ACCOUNT_ID`, `CLOUDFLARE_GATEWAY_ID`, `CLOUDFLARE_API_TOKEN`; NVIDIA `NVIDIA_API_KEY`; SAP AI Core `AICORE_SERVICE_KEY`, `AICORE_DEPLOYMENT_ID`, `AICORE_RESOURCE_GROUP`; Snowflake `SNOWFLAKE_ACCOUNT`, `SNOWFLAKE_CORTEX_TOKEN` (`SNOWFLAKE_CORTEX_PAT` still supported). — [OpenCode providers docs](https://opencode.ai/docs/providers/) (verified, primary)

### Inferences
- OpenCode's provider model is "adapter package + base URL + key + model map", which maps closely onto a Bazaar `Provider` record (adapter kind, base URL, credential reference, models, headers). Bazaar should use its own schema but keep the same five concepts.
- Using `{env:NAME}` in config rather than inline secrets is a pattern Bazaar can adopt for operator config, with secrets kept out of git.
- The docs/schema split (legacy `provider` vs newer `providers`, `npm` vs `package`) means any field name copied from OpenCode today may change. Treat OpenCode as a design reference, not a stable contract.

### Gaps
- The providers docs page does not describe `cost`, `modalities` or capabilities as user-facing config; these appear only in models.dev and the schema (see section 2).
- No OpenCode documentation found on server-side per-agent model policy; OpenCode is a single-user client-side tool.
- Whether the newer `providers` block is the default in current releases was not confirmed (the install page lists `opencode-v2` via Homebrew, suggesting a v2 line; not verified in detail).

## 2. models.dev: maintainer, licence, schema, size, machine readability

### Takeaway
models.dev is an open-source (MIT) database of AI model specs, limits, pricing and capabilities, published as JSON, with per-provider entries that name the API base URL and npm adapter. It is a strong candidate for a default catalogue data source, subject to validating its accuracy and checking its data-licence wording. Its maintainer attribution is inconsistent across sources (SST vs anomalyco), and its update cadence and exact counts were not verified.

### Cited Findings
- README describes models.dev as "a comprehensive open-source database of AI model specifications, pricing, and capabilities", community-contributed, used by opencode. README says "Models.dev is created by the maintainers of SST" (linking sst.dev); the repo is under the `anomalyco` org per the fetched page. — [models.dev repo README](https://github.com/sst/models.dev) (verified, primary, via summarising fetch)
- Data layout: `models/<lab>/<model>.toml` (facts true on every host, e.g. `models/openai/gpt-5.toml`); `providers/<provider>/provider.toml` (provider details); `providers/<provider>/models/<model>.toml` (how a provider serves a model: reasoning controls, cache, pricing); `providers/<provider>/logo.svg`. Contributors run `bun validate` before a PR. — [models.dev repo README](https://github.com/sst/models.dev) (verified, primary, via summarising fetch)
- Machine-readable endpoints named in the README: `https://models.dev/api.json` (main API), `https://models.dev/models.json` (provider-agnostic model metadata), `https://models.dev/catalog.json` (provider endpoints and model metadata combined), `https://models.dev/logos/{provider}.svg`, and `model-schema.json`; filters `?type=decision` and `?type=all`. — [models.dev repo README](https://github.com/sst/models.dev) (verified, primary, via summarising fetch; endpoint existence not separately checked, but `api.json` was fetched successfully)
- `https://models.dev/api.json` returns a very large JSON document (fetched text reported as about 5.9 million characters). Top-level keys are provider IDs; each provider has `id`, `env` (array of API-key env var names), `npm` (AI SDK package), `api` (base URL), `name`, `doc`, `models` (object keyed by model ID). — [models.dev api.json](https://models.dev/api.json) (verified, primary; structure read from first 100k characters only)
- Model object fields seen: `id`, `name`, `description`, `family`, `canonical_model_id`, `type` (optional: embedding, transcription, realtime), `attachment`, `reasoning`, `reasoning_options`, `tool_call`, `interleaved`, `structured_output`, `temperature`, `knowledge`, `release_date`, `last_updated`, `open_weights`, `status` (deprecated, beta), `provider` (API override), `modalities` (`input`, `output` arrays), `limit` (`context`, `output`, optional `input`), `cost` (`input`, `output`, optional `cache_read`, `cache_write`, `reasoning`, `input_audio`, `output_audio`, `tiers`, `context_over_200k`). — [models.dev api.json](https://models.dev/api.json) (verified, primary, first 100k characters)
- Example provider base URLs visible in the read portion: `volcengine-coding-plan` `https://ark.cn-beijing.volces.com/api/coding/v3`; `perplexity-agent` `https://api.perplexity.ai/v1`. The visible portion contained about 11 providers and roughly 160 models before truncation. — [models.dev api.json](https://models.dev/api.json) (verified for the listed fields; counts are a rough count of a truncated read, so unverified as totals)
- Anomaly: in the read portion, the `anthropic` top-level provider did not appear; Anthropic models appeared under `perplexity-agent` (e.g. `anthropic/claude-sonnet-5`). This could be a truncation artefact, a data-quality issue, or a provider-ID naming choice; not resolved. — [models.dev api.json](https://models.dev/api.json) (verified as observed in the read portion; cause unverified)
- Licence: MIT, "Copyright (c) 2025 models.dev". — [models.dev LICENSE](https://github.com/sst/models.dev/blob/dev/LICENSE) (verified, primary)
- The models.dev home page lists model columns: Model, Lab, Providers, Context, Output, Input, Reasoning, Tool Call, Structured, Temperature, Weights (Open/Closed), Price, Release, Updated. — [models.dev](https://models.dev) (verified, primary, via summarising fetch; total model/provider counts not shown in fetched text)
- A search-snippet secondary source says models.dev is maintained by Anomaly (GitHub `anomalyco`), which also builds opencode.ai. — [traeai.com entity page](https://www.traeai.com/en/entities/anomalyco) (secondary, low weight; search snippet only, not read in full)
- A search-snippet secondary source says models.dev is MIT licensed; this matches the primary LICENSE file above. — [EveryDev listing](https://www.everydev.ai/tools/models-dev) (secondary, search snippet only)

### Inferences
- Because models.dev is MIT and ships a JSON API with base URLs, npm adapters, env var names and per-model limits and cost, it is the natural machine-readable source for a Bazaar default catalogue. Bazaar would need to vendor or fetch it under its own refresh and pinning policy.
- The Anomaly-vs-SST attribution conflict matters only for the NOTICE/attribution text; the LICENSE file names "models.dev" as copyright holder, so attribution should use that.
- Data-quality risk: the Anthropic-under-perplexity observation shows that catalogue entries can be surprising. Bazaar should validate models.dev entries against provider documentation before shipping them in a default catalogue.

### Gaps
- Exact total number of providers and models: not verified (the JSON was only read in part). Secondary claims of "75+ providers" come from the OpenCode docs and are not a models.dev count.
- Update cadence: not found in any fetched source. Unverified.
- Whether `api.json` is covered by a separate data licence, or whether logos (SVGs) carry third-party trademarks: not checked.
- Stability/versioning guarantees of the JSON schema: not found.

## 3. Providers OpenCode supports by default

### Takeaway
OpenCode's providers docs list 51 providers, spanning first-party labs, gateways and aggregators, cloud platforms, OpenCode-hosted options and local runtimes. Free-tier providers are not separated on the page. FreeLLMAPI is not in the list.

### Cited Findings
- The providers page says OpenCode uses the AI SDK and Models.dev to support "75+ LLM providers" plus local models, and that the directory lists 51 providers. — [OpenCode providers docs](https://opencode.ai/docs/providers/) (verified, primary, via summarising fetch; the count "51" was read from the page summary, so re-check before quoting)
- Cloud and gateways listed: 302.AI, Amazon Bedrock, Anthropic, Azure OpenAI, Azure Cognitive Services, Baseten, Cerebras, Cloudflare AI Gateway, Cloudflare Workers AI, Cortecs, DeepSeek, Deep Infra, DigitalOcean, Eden AI, FrogBot, Fireworks AI, GitLab Duo, GitHub Copilot, GMI Cloud, Google Vertex AI, Groq, Hugging Face, Helicone, IO.NET, Moonshot AI, MiniMax, Modal, NVIDIA, Nebius Token Factory, Ollama Cloud, OpenAI, OpenCode Zen, OpenRouter, LLM Gateway, Poolside, SAP AI Core, STACKIT, OVHcloud AI Endpoints, Scaleway, SCX.ai, Snowflake Cortex, Together AI, Venice AI, Vercel AI Gateway, xAI, Z.AI, ZenMux. — [OpenCode providers docs](https://opencode.ai/docs/providers/) (verified, primary, via summarising fetch)
- Local runtimes listed: Atomic Chat, llama.cpp, LM Studio, Ollama. — [OpenCode providers docs](https://opencode.ai/docs/providers/) (verified, primary, via summarising fetch)
- OpenCode Zen and OpenCode Go are OpenCode-hosted; Go is described as a low-cost subscription. — [OpenCode providers docs](https://opencode.ai/docs/providers/) (verified, primary, via summarising fetch)
- Base URLs stated on the providers page (examples and defaults): Anthropic `https://api.anthropic.com/v1`; Atomic Chat `http://127.0.0.1:1337/v1`; Azure OpenAI `https://RESOURCE_NAME.openai.azure.com/`; llama.cpp `http://127.0.0.1:8080/v1`; LM Studio `http://127.0.0.1:1234/v1`; Ollama `http://localhost:11434/v1`; NVIDIA on-prem NIM `http://localhost:8000/v1`; Eden AI `https://api.edenai.run/v3` (EU `https://api.eu.edenai.run/v3`); Helicone `https://ai-gateway.helicone.ai`; Poolside example `https://poolside.example.com/openai/v1`; GitLab Duo `instanceUrl` example `https://gitlab.com`. Most other providers show no base URL on the page and rely on built-in defaults from models.dev. — [OpenCode providers docs](https://opencode.ai/docs/providers/) (verified, primary, via summarising fetch)
- Base URLs for many providers (e.g. DeepSeek, Groq, OpenRouter, xAI, Together, Fireworks) are not stated on the OpenCode page; they come from models.dev `api` fields (not checked per provider). — [OpenCode providers docs](https://opencode.ai/docs/providers/) (gap noted); [models.dev api.json](https://models.dev/api.json) (structure only)
- FreeLLMAPI (`tashfeenahmed/freellmapi`) does not appear in the OpenCode providers list. — [OpenCode providers docs](https://opencode.ai/docs/providers/) (absence checked in the summary; verified against the 51-entry list read)
- FreeLLMAPI description (secondary, search snippets only): open-source self-hosted router giving one OpenAI-compatible endpoint over several providers' free tiers; MIT; Node.js 20+; described as for personal experimentation. Provider counts in snippets disagree (16, 28, 34). — [awesome.ecosyste.ms](https://awesome.ecosyste.ms/projects/github.com%2Ftashfeenahmed%2Ffreellmapi), [Better Stack guide](https://betterstack.com/community/guides/ai/freellmapi.md), [newreleases.io v0.6.1](https://newreleases.io/project/github/tashfeenahmed/freellmapi/release/v0.6.1) (secondary; the project's own README was not read, so treat as unverified)

### Inferences
- The OpenCode list is a reasonable seed for Bazaar's default catalogue of providers, but it is a list of names, and Bazaar needs base URLs and auth methods checked per provider from the primary docs before shipping.
- FreeLLMAPI would be a Bazaar-specific addition, not something OpenCode's default list can supply. Its listing in Bazaar should rest on its own README and licence, which were not read here.
- Free-tier grouping: OpenCode does not label free tiers, so any "free" grouping in Bazaar must come from provider terms, not from OpenCode.

### Gaps
- Free-tier providers: not identified by OpenCode. No primary source in this run lists which OpenCode providers have a free tier.
- Per-provider base URLs for most providers: not read (page gives few). Need per-provider verification.
- The FreeLLMAPI README was not read; its provider count and licence are secondary only.

## 4. Custom proxies, gateways, whitelists/blacklists, fallbacks

### Takeaway
OpenCode treats any OpenAI-compatible endpoint as a custom provider, with a base URL override, and supports per-provider model whitelists and blacklists. It documents no runtime fallback between providers and no LiteLLM-specific handling. Enterprise and corporate proxy support is limited to base URL overrides and some provider-specific gateway options.

### Cited Findings
- Base URL override "is useful when using proxy services or custom endpoints". — [OpenCode providers docs](https://opencode.ai/docs/providers/) (verified, primary, via summarising fetch)
- Gateways documented as unified endpoints: Cloudflare AI Gateway, Vercel AI Gateway, Eden AI (EU-based, one OpenAI-compatible API), Helicone AI Gateway (routes by model). — [OpenCode providers docs](https://opencode.ai/docs/providers/) (verified, primary, via summarising fetch)
- Custom or self-hosted: Amazon Bedrock VPC endpoint via `endpoint` (alias for `baseURL`); NVIDIA NIM on-prem by custom base URL; GitLab Duo custom AI Gateway; Poolside deployments configured as custom OpenAI-compatible providers. — [OpenCode providers docs](https://opencode.ai/docs/providers/) (verified, primary, via summarising fetch)
- Whitelist/blacklist: per-provider arrays of model IDs; `whitelist` keeps only listed models; `blacklist` removes listed models; `whitelist` applied first, then `blacklist`. Also `enabled_providers` / `disabled_providers` at top level, with `disabled_providers` winning. — [OpenCode providers docs](https://opencode.ai/docs/providers/); [OpenCode config docs](https://opencode.ai/docs/config/); [OpenCode config schema](https://opencode.ai/config.json) (verified, primary). Note: the models-ID filter is a picker filter ("removes listed models from the picker") and the docs do not say it blocks API calls to those IDs; this is an inference.
- Fallbacks: the models page documents only startup selection order, not runtime fallback. The OpenRouter option `allow_fallbacks: false` and Vercel `order` concern upstream routing inside a gateway, not OpenCode-level fallback. — [OpenCode models docs](https://opencode.ai/docs/models/); [OpenCode providers docs](https://opencode.ai/docs/providers/) (verified, primary)
- Timeouts: `timeout`, `headerTimeout`, `chunkTimeout` per provider. — [OpenCode config docs](https://opencode.ai/docs/config/) (verified, primary)

### Inferences
- OpenCode's whitelist is a client-side convenience, not an enforced policy. Bazaar's per-agent model policy must be enforced server-side in Bazaar.Server, as already decided. OpenCode's model is a useful UI reference, not a policy reference.
- OpenCode has no documented cross-provider fallback, so Bazaar's fallback logic (for FreeLLMAPI or other routers) must be designed on Bazaar's side, not borrowed.
- A LiteLLM-style proxy would work in OpenCode as a custom OpenAI-compatible provider via base URL; this is inferred from the custom-provider docs, not from a LiteLLM-specific source.

### Gaps
- No OpenCode doc on LiteLLM specifically, corporate proxies beyond base URL, or proxy auth (e.g. mTLS) was found.
- Whether whitelist/blacklist blocks API calls or only hides picker entries: not stated in the docs.
- Runtime fallback behaviour when a provider errors: not documented.

## 5. Licences and what needs counsel

### Takeaway
OpenCode and models.dev are both MIT-licensed according to their primary LICENSE files. MIT is permissive and compatible with inbound use in AGPL-3.0 and Apache-2.0 projects, but the notice-retention duty applies to copied code and data. Copying provider names, base URLs and env var names (facts) is low-risk; copying bulk JSON or derived data files needs an MIT notice and a decision on how Bazaar tracks provenance. Counsel should confirm.

### Cited Findings
- OpenCode: MIT License, "Copyright (c) 2025 opencode". — [OpenCode LICENSE](https://github.com/anomalyco/opencode/blob/dev/LICENSE) (verified, primary). Also the README states MIT — [OpenCode README](https://github.com/anomalyco/opencode) (verified, primary, via summarising fetch).
- models.dev: MIT License, "Copyright (c) 2025 models.dev". — [models.dev LICENSE](https://github.com/sst/models.dev/blob/dev/LICENSE) (verified, primary). The README link label reads "MIT license" with no full text in the fetched output.
- The OpenCode repo URL is `github.com/anomalyco/opencode`; the README says it is under the anomalyco org. An older `sst/opencode` URL may exist or redirect; not checked.
- Secondary listing states models.dev is MIT; matches primary. — [EveryDev listing](https://www.everydev.ai/tools/models-dev) (secondary)
- Provider names, base URLs and env var names are facts published by providers and by OpenCode docs; the docs are a separate work from the data. — inference, see below.

### Inferences
- Compatibility: MIT-licensed material can be incorporated into AGPL-3.0 server code and Apache-2.0 SDK/contracts code, provided the MIT copyright and permission notice are retained with copied substantial portions. Apache-2.0 requires notices for its own files; whether to ship a NOTICE file is a counsel/policy call.
- Lowest-risk approach: write Bazaar's own catalogue file with facts (provider IDs, base URLs, env var names) verified against each provider's primary docs, and record models.dev and OpenCode as design references in a credits file. Vendoring the models.dev JSON wholesale would carry the MIT notice requirement and would make Bazaar's catalogue depend on a third-party data schema.
- Trademarks: provider names and logos (models.dev serves SVG logos under `/logos/`) are third-party trademarks. Using names for identification is generally a nominative use question; using logos in a shipped UI should be cleared separately. Counsel flag.
- Data accuracy and liability: Bazaar's default catalogue would republish third-party data; MIT disclaims warranty. Bazaar should state provenance and that operators must verify.
- Anthropic terms: OpenCode's own docs say Anthropic prohibits using Pro/Max subscriptions through third-party plugins (see section 1). This agrees with Bazaar's rule. Not legal advice; the primary Anthropic terms were not re-read in this run.

### Gaps
- Whether a models.dev data file is a "compilation" with separate database rights in some jurisdictions: not checked. Counsel.
- Whether OpenCode's provider list reflects provider terms (e.g. free-tier terms): not checked.
- Whether any contributor-licence (DCO/CLA) terms exist for models.dev or OpenCode contributions: not checked.

## Points for the report writer (conflicts and open items)

1. Attribution conflict: models.dev README says "created by the maintainers of SST", but the repo sits under `anomalyco`, and a secondary source says Anomaly maintains models.dev and opencode. The LICENSE copyright line says "models.dev". Use the LICENSE line for notices.
2. Schema is in flux: OpenCode config schema marks `provider` as deprecated and adds a `providers` block with `package`, `compatibility`, `settings`, `headers`, `body`. Do not lock Bazaar to today's OpenCode keys.
3. The providers page says "75+" and lists "51" providers. These two numbers are both from one page; neither is independently counted. Re-check before quoting.
4. Cost data: OpenCode docs do not describe `cost` for users; the schema and models.dev do. Cost is in models.dev per model (`cost.input`, `cost.output`, cache fields).
5. FreeLLMAPI is not in OpenCode's list. Bazaar's first-class status for FreeLLMAPI is Bazaar-specific and needs its own README and licence check (not done here; the secondary snippets say MIT).
6. Anthropic appears under `perplexity-agent` in the models.dev read portion, not under a top-level `anthropic` provider. Possible data-quality issue; not resolved.
7. Anthropic prohibits Pro/Max subscription use via third-party tools (per OpenCode docs), consistent with Bazaar's decision.

## Not verified in this run
- Exact provider and model counts in models.dev.
- models.dev update cadence and release/versioning.
- Per-provider base URLs for the majority of providers (the OpenCode page gives few).
- Free-tier membership of any provider.
- The FreeLLMAPI README, licence and routing modes (only search snippets read).
- Whether OpenCode v2 (`opencode-v2`, `@opencode/cli`) is the current default and whether the `providers` block is in production.
- The full models.dev `api.json` (only the first 100k of about 5.9M characters were read).
