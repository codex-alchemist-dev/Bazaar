# SillyTavern: API sources, reverse proxies, network proxy and secrets (as of 2026-10-24)

Research date: 2026-10-24. Scope: how SillyTavern (ST) connects to model sources, custom and reverse-proxy endpoints, outbound network proxies, connection profiles, and API key handling. Used as a concept reference for Bazaar.Server's gateway and provider setup screens.

Verification labels: **verified** = primary source read (repo file or official docs page, read through the fetch tool); **secondary** = third-party or search-summary text; **unverified** = not confirmed.

Method caveats: raw source files were read from the `release` branch of github.com/SillyTavern/SillyTavern on 2026-10-24, not a pinned commit. Several `docs.sillytavern.app` pages returned 404 to my fetches (Chat Completion page, custom-openai page, the SillyTavern-Docs mirror pages for chat completion and reverse proxying). Facts drawn from the fetch tool's summaries of large files are marked as such. No code was run or installed.

## Key Question 1: Source inventory (Chat Completion and Text Completion)

### Takeaway
ST has two families of sources. Chat Completion sources (about 28 IDs in `openai.js`) are mostly OpenAI-compatible, plus native Anthropic, Google Gemini, Cohere and Azure handlers. Text Completion backends (15 IDs) are mostly self-hosted runtimes plus a few hosted services. Many hosted vendors appear in both families. Base URLs are hardcoded per vendor, except for self-hosted types and Azure, which need a user-supplied URL.

### Cited Findings

**Chat Completion source IDs** (`chat_completion_sources` in `public/scripts/openai.js`; verified via fetch summary of the file): openai, claude, openrouter, ai21, makersuite, vertexai, mistralai, custom, cohere, perplexity, groq, electronhub, chutes, nanogpt, deepseek, aimlapi, xai, pollinations, moonshot, fireworks, cometapi, azure_openai, zai, siliconflow, workers_ai, minimax. Source: [openai.js (release branch)](https://raw.githubusercontent.com/SillyTavern/SillyTavern/release/public/scripts/openai.js). Note: the fetch summary lists no display names; display names come from the UI and docs (see below).

**Chat Completion base URLs and API style** (`src/endpoints/backends/chat-completions.js`; verified via fetch summary of the file, which was truncated at the `/generate` fallthrough):
- Claude: `https://api.anthropic.com/v1`, Anthropic Messages API (native handler `sendClaudeRequest`).
- AI21: `https://api.ai21.com/studio/v1`, OpenAI-compatible.
- Google AI Studio (MakerSuite): `https://generativelanguage.googleapis.com`, Gemini API (native).
- Vertex AI: `https://us-central1-aiplatform.googleapis.com` or a region/project URL, Gemini-style handler.
- Mistral: `https://api.mistral.ai/v1`, OpenAI-compatible.
- Cohere: `https://api.cohere.ai/v2` (status check uses v1), Cohere v2 chat (native).
- DeepSeek: `https://api.deepseek.com/beta`, OpenAI-compatible.
- xAI: `https://api.x.ai/v1`, OpenAI-compatible.
- AIMLAPI: `https://api.aimlapi.com/v1`, OpenAI-compatible.
- Electron Hub: `https://api.electronhub.ai/v1`, OpenAI-compatible.
- Chutes: `https://llm.chutes.ai/v1`, OpenAI-compatible.
- MiniMax: `https://api.minimax.io/v1` (or `API_MINIMAX_CN`), OpenAI-compatible.
- Azure OpenAI: user-supplied `azure_base_url`, Azure OpenAI handler.
- OpenAI: `https://api.openai.com/v1`, handled in the `/generate` branch.
- OpenRouter: `https://openrouter.ai/api/v1`, handled in the `/generate` branch.
- Others (custom, nanogpt, moonshot, fireworks, groq, siliconflow, pollinations, workers_ai): hardcoded constants or URLs in the truncated part of the file, not verified in detail. COMETAPI is referenced in `/status` but throws "This provider is temporarily disabled."
Source: [chat-completions.js (release branch)](https://raw.githubusercontent.com/SillyTavern/SillyTavern/release/src/endpoints/backends/chat-completions.js).

**Text Completion backend types** (`textgen_types` in `public/scripts/textgen-settings.js`; verified via fetch summary): ooba, mancer, vllm, aphrodite, tabby, koboldcpp, togetherai, llamacpp, ollama, infermaticai, dreamgen, openrouter, featherless, huggingface, generic. Hosted defaults present in code: Mancer `https://neuro.mancer.tech`; TogetherAI `https://api.together.xyz`; InfermaticAI `https://api.totalgpt.ai`; DreamGen `https://dreamgen.com`; OpenRouter `https://openrouter.ai/api`; Featherless `https://api.featherless.ai/v1`. Self-hosted types (ooba, vllm, aphrodite, tabby, koboldcpp, llamacpp, ollama, huggingface, generic) have no default URL and fall back to a per-type `server_urls` setting, so the user must enter one. Source: [textgen-settings.js (release branch)](https://raw.githubusercontent.com/SillyTavern/SillyTavern/release/public/scripts/textgen-settings.js). Note: the summary did not show a localhost default; the docs give Oobabooga default `http://localhost:5000/` (see below).

**Docs grouping** (verified, [API connections index](https://docs.sillytavern.app/usage/api-connections/) and the SillyTavern-Docs mirror at [Usage/API_Connections/index.md](https://raw.githubusercontent.com/SillyTavern/SillyTavern-Docs/main/Usage/API_Connections/index.md)):
- Local: KoboldCpp, llama.cpp, Ollama, Oobabooga TextGeneration WebUI (default `http://localhost:5000/`), TabbyAPI, KoboldAI Classic (marked deprecated/abandoned).
- Cloud: AI Horde, OpenAI, Claude (by Anthropic; "requires prefills for reply steering"), Google AI Studio and Vertex AI, Mistral, OpenRouter, DeepSeek, AI21, Cohere, Perplexity, Mancer AI, DreamGen, Pollinations (free; outputs may include ads), NovelAI, Electron Hub, AI/ML API.
- The docs list about 16 cloud entries; the code has more Chat Completion sources (e.g., xAI, Groq, Fireworks, Moonshot, MiniMax, Chutes, NanoGPT, SiliconFlow, Workers AI, Z.ai, CometAPI) that the docs index does not list. Docs lag the code.

**Docs claim for Chat vs Text:** "A Chat Completion model ... will structure your prompt into a series of messages"; Text Completion "will convert your prompt into one long string" (verified, docs index page). The API type decides how the prompt is built.

**Free and community providers:** Pollinations (free, ads possible), AI Horde (volunteer GPUs, no extra settings), plus local runtimes. Source: docs index page above (verified).

### Inferences
- ST's source list is effectively a catalogue of hardcoded vendor base URLs plus a generic "custom" escape hatch. Bazaar can copy the concept (a shipped catalogue of named sources with known base URLs and API style) without copying the list verbatim, since base URLs and vendor names are facts, not code.
- Two API styles matter for Bazaar: OpenAI-compatible (most sources) and native (Anthropic Messages, Gemini, Cohere v2, Azure). Bazaar's gateway should model "API style" as a field per provider, not assume one wire format.

### Gaps
- Exact display names and the UI list for all 28 Chat Completion sources were not read (the fetch summary lacked UI labels). Not verified.
- Handler details for the truncated sources (nanogpt, moonshot, fireworks, groq, siliconflow, pollinations, workers_ai, custom) were not read.
- Which ST version added which source was not checked (the docs index lags the code, see above).
- FreeLLMAPI is not in ST's source list; I did not check whether it can be reached via "Custom (OpenAI-compatible)". Not verified.

## Key Question 2: Custom (OpenAI-compatible) and reverse-proxy settings

### Takeaway
ST's "Custom (OpenAI-compatible)" is a Chat Completion source with a URL and optional key. Reverse-proxy and custom-endpoint behaviour is stored as plain settings keys in `openai.js` and overrides the built-in base URL per request. The docs say users should omit `/chat/completions` from the URL and not expect support for arbitrary endpoints.

### Cited Findings

**Custom endpoint setup steps** (from the docs section "Custom OpenAI-compatible endpoint" on the OpenAI page; verified via fetch summary): 1. Switch to the Chat Completion API type. 2. Select "Custom (OpenAI-compatible)" for "Chat Completion Source". 3. Enter the endpoint URL, plus an API key if required. 4. For connection problems, try adding `/v1`; the page says "Do NOT add the `/chat/completions` suffix." Model selection: if the endpoint implements `/v1/models`, pick from a dropdown; otherwise type the model ID. "Bypass API status check" silences warnings when the endpoint works anyway. "Test Message" sends a simple prompt. Source: [docs.sillytavern.app OpenAI page](https://docs.sillytavern.app/usage/api-connections/openai/).

**Support disclaimers on the same page** (verified): "we do not provide support for possible issues that you may have!" and "we do not guarantee compatibility with every possible API endpoint!" For local servers (TabbyAPI, Oobabooga, Aphrodite) the docs recommend the built-in compatibility options instead of the custom endpoint. Source: same page.

**Post-processing caveat** (verified): "Tool Calling is not supported when Post-Processing option with 'no tools' is used!" and the custom endpoint "may error on invalid requests, while the built-in endpoints are not affected by the less restrictive post-processing options." Source: same page.

**Settings keys** (verified, `default_settings` in `openai.js`): `reverse_proxy: ''`, `proxy_password: ''`, `custom_url: ''`, `custom_include_headers: ''`, `custom_include_body: ''`, `custom_exclude_body: ''`. Source: [openai.js](https://raw.githubusercontent.com/SillyTavern/SillyTavern/release/public/scripts/openai.js).

**How reverse proxy overrides work** (verified via fetch summary of `chat-completions.js` and `openai.js` server routes): the request body carries `reverse_proxy`; when set, it replaces the base URL, and `proxy_password` replaces the stored key (pattern `request.body.reverse_proxy ? request.body.proxy_password : readSecret(...)`). This appears in the Claude, Gemini and Vertex (proxy mode), Mistral, DeepSeek, xAI handlers, the `/status` routes for OpenAI, Mistral, DeepSeek, xAI, Google AI Studio and Moonshot, and the OpenAI `/generate` branch. Source: [chat-completions.js](https://raw.githubusercontent.com/SillyTavern/SillyTavern/release/src/endpoints/backends/chat-completions.js). Implication: the proxy's API key lives in the browser-supplied request body for the proxy case, and the proxy is a per-request override, not a global setting.

**Model list fetching** (partly verified): the custom endpoint uses `/v1/models` when present (docs, above). The `saveModelList` function processes model data per source (fetch summary of `openai.js`); endpoint URLs for each source were not visible in the read portion. Gap for the rest.

**Streaming** (gap): no docs page with the streaming setting was readable. The docs-issue list references Google AI Studio and Vertex streaming through proxies, but I did not read those issues in full.

**Text Completion custom URL behaviour** (verified, [issue #6077](https://github.com/SillyTavern/SillyTavern/issues/6077), opened 2026-09-25, open): ST always appends `/completions` to the configured base URL for Text Completion "Custom (OpenAI-compatible)", so gateways that only expose `/v1/chat/completions` return 404. The reporter asks for a configurable completions path or documentation of the incompatibility.

**Reverse-proxy docs page is about something else** (verified, [docs reverse-proxying page](https://docs.sillytavern.app/administration/reverse-proxying.md)): its danger note says "This section does **not** refer to OpenAI/Claude reverse proxies. This refers exclusively to **HTTP/HTTPS Reverse Proxies**." It covers exposing a home instance through Traefik (or NGINX/Caddy) with listen mode, basic auth and whitelist changes. So ST's docs use "reverse proxy" for two unrelated things, and the API-proxy docs page was not found.

**Secondary guidance** (secondary, vendor marketing): several vendor guides (apisrouter.com, hpc-ai.com, meganova blog) describe the same custom-endpoint form and disagree on whether the URL should end in `/v1`. Example: [apisrouter.com setup](https://apisrouter.com/roleplay-reverse-proxy-setup). Treat as unverified.

### Inferences
- ST's model is "one generic OpenAI-compatible slot plus per-vendor presets", with per-request overrides for URL and key. The fields to copy as concepts: base URL, key, extra headers, extra body (include and exclude lists), a test message, and a "skip status check" escape.
- The `/v1` vs no-`/v1` ambiguity and the non-configurable `/completions` path are exactly the failure modes Bazaar's gateway should avoid. Bazaar should store the full endpoint path per provider style, or validate the probe.

### Gaps
- Exact labels and behaviour for extra headers and extra body fields (`custom_include_body` format) were not read.
- Streaming settings and the "Prompt Post-Processing" option list were not read (the Chat Completion page returned 404).
- Model list endpoints per source were not read beyond `/v1/models` for Custom.

## Key Question 3: Connection profiles and presets

### Takeaway
Connection profiles bundle the API type, model, server URL, settings preset and (for Chat Completion) prompt post-processing and proxy preset, and they can be switched from a dropdown or `/profile [name]`. They also store the Secret Key, which is a relevant security point.

### Cited Findings
- "Save Connection Profiles to quickly switch between different APIs, models and formatting templates." (verified, [connection profiles page](https://docs.sillytavern.app/usage/core-concepts/connection-profiles/)).
- Common items: API type, model, server URL, settings preset, Start Reply With, Custom Stopping Strings, reasoning formatting. Text Completion also stores system prompt state, instruct mode and template, context template, tokenizer. Chat Completion also stores prompt post-processing and the proxy preset. (verified, same page).
- Creation: set options, click Create, review, give a unique name. Switching: dropdown or `/profile [name]`. "you will lose unsaved changes by switching to a different profile." (verified, same page).
- "The Secret Key is listed among the saved items" (verified, same page). The page does not explain storage.

### Inferences
- A profile is a set of dropdown selections, not a copy of secrets. The docs' statement that Secret Key is saved in profiles means a profile could carry a key reference; the exact mechanism is not verified. Bazaar should decide explicitly whether a profile holds a key reference, not the key.

### Gaps
- Whether profiles store the key value or a secret ID was not verified (the secrets code uses IDs, see Q4, but I did not confirm the profile link).
- The proxy preset content was not read.

## Key Question 4: Network proxy, request proxy, CORS proxy

### Takeaway
ST has a server-wide outbound "requestProxy" in `config.yaml` (HTTP, HTTPS, SOCKS4/5, PAC, with a bypass list), separate from per-request API reverse proxies, plus an off-by-default CORS proxy middleware. Bypass and proxy-URL handling have open bugs.

### Cited Findings
- `requestProxy` block (verified, [default config.yaml](https://raw.githubusercontent.com/SillyTavern/SillyTavern/release/default/config.yaml)): `enabled: false` ("If a proxy is enabled, all outgoing HTTP/HTTPS requests will be routed through it."); `url: "socks5://username:password@example.com:1080"` ("Possible protocols: http, https, socks, socks5, socks4, pac"); `bypass: [localhost, 127.0.0.1]` ("Requests to these hosts won't be routed through the proxy."); `enableCorsProxy: false` ("Enables CORS proxy middleware").
- CLI equivalents `--requestProxyEnabled` and `--requestProxyUrl` (secondary, [configuration file docs](https://docs.sillytavern.app/administration/config-yaml/) as seen in a search summary; the docs page warns it may be stale and points to the default config.yaml).
- Bypass wildcards do not work: [issue #5787](https://github.com/SillyTavern/SillyTavern/issues/5787), opened 2026-06-20, open. CIDR and octet wildcards are ignored; only exact IPs match. A commit adding CIDR and IP wildcard support was linked on 2026-07-12 and the issue is to close on merge to `release` (verified via issue page summary).
- Proxy URL with a space crashes with "Invalid URL" ([issue #4883](https://github.com/SillyTavern/SillyTavern/issues/4883), open, per the issue list page).
- Fetch through a proxy failing with ETIMEDOUT ([issue #4453](https://github.com/SillyTavern/SillyTavern/issues/4453), closed) and Vertex AI not working through a proxy ([issue #5116](https://github.com/SillyTavern/SillyTavern/issues/5116), closed) both appear in the repo issue search (verified via listing; contents not read).
- Chat Completion requests use direct `fetch` in the visible code, with no proxy agent (verified, fetch summary of `chat-completions.js`). The Text Completion page routes through ST's own endpoints (`/api/backends/text-completions/...`) with the target passed as `api_server` in the body, so the browser never calls the backend directly (verified, fetch summary of `textgen-settings.js`).
- OpenRouter status failing with 403 because Cloudflare blocks Node's TLS fingerprint ([issue #5825](https://github.com/SillyTavern/SillyTavern/issues/5825), closed) shows that a network proxy does not solve every provider-side block (verified via listing; contents partially read).

### Inferences
- Two different things share the word "proxy" in ST: (1) a network proxy for the server's outbound traffic, which is server-wide, and (2) an API reverse proxy (`reverse_proxy` setting) that replaces a provider's base URL per request. Bazaar's UI should name them differently, e.g. "Outbound network proxy" versus "Provider base URL override (gateway)".
- The CORS proxy is a browser-era concept. Bazaar has no web client, so it has no need for a CORS proxy; do not copy it.

### Gaps
- Whether `requestProxy` applies to every outbound call including model APIs was not confirmed beyond the config comment (which says "all outgoing HTTP/HTTPS requests").
- Whether the Text Completion `/generate` path uses the same agent was not verified.

## Key Question 5: Secrets handling

### Takeaway
ST stores provider keys in a plain-text `secrets.json` under the user's root, with no encryption, supports multiple keys per provider with one active, and hides key values by default behind `allowKeysExposure: false`. The docs and hosting guides warn that a public instance exposes keys and that basic auth is the only lock.

### Cited Findings
- File path and format (verified, [secrets.js (release branch)](https://raw.githubusercontent.com/SillyTavern/SillyTavern/release/src/endpoints/secrets.js), read via fetch summary): `secrets.json` in the user's root directory (`path.join(directories.root, SECRETS_FILE)`), a JSON object keyed by names such as `api_key_openai`, each holding entries with `id`, `value`, `label`, `active`.
- No encryption: writes use `JSON.stringify` with `write-file-atomic` (verified, same file). Migration backups are also unencrypted (verified, same file).
- Endpoints (verified, same file): `/write` returns only the new id; `/read` returns masked values (last 3 characters shown for values over 10 characters); `/view` returns only active secrets and returns 403 unless `allowKeysExposure` is true; `/find` returns 403 for any key outside `EXPORTABLE_KEYS` (URL-type settings only) unless the flag is true; `/settings` reports the flag; `/delete`, `/rotate`, `/rename` do not return values.
- Multiple keys: each key can hold several secrets with UUID ids, one `active`; `writeSecret` deactivates others; `rotateSecret(key, id)` activates one; `deleteSecret` falls back to another active entry (verified, same file).
- `allowKeysExposure` defaults to false (verified, same file and [default config.yaml](https://raw.githubusercontent.com/SillyTavern/SillyTavern/release/default/config.yaml) which states "# Allow secret keys exposure via API").
- Config security surface (verified, default config.yaml): `whitelistMode: true`, `whitelist: [::1, 127.0.0.1]`, `enableForwardedWhitelist: true`, `basicAuthMode: false`, default basic auth `user`/`password`, `perUserBasicAuth: false`, `rateLimiting.basicAuthMaxAttempts: 5`, `securityOverride: false` ("Disable startup security checks - NOT RECOMMENDED"), `enableUserAccounts: false`, and `ssl.keyPassphrase` with a note to use an env var (`SILLYTHAVERN_SSL_KEYPASSPHRASE`) or CLI argument instead.
- No API key fields appear in the default config.yaml (verified, same file).
- Hosting guide (secondary, [Railway deployment page](https://railway.com/deploy/sillytavern-private-instance)): a public instance holds provider keys and basic auth is the only lock; the guide says basic auth has no rate limiting. Secondary, not checked against the code. A second guide says the startup check refuses non-loopback serving without a security layer (secondary).
- Browser exposure: one docs-issue fragment shows the UI calling `/api/secrets/view` and a 403 popup pointing at `allowKeysExposure` (secondary, search snippet of `secrets.js`).

### Inferences
- The security design is "keys are stored server-side, never sent back in full by default, but unencrypted on disk". Bazaar's Windows-only server host and DPAPI or envelope-encryption options (already in the Soul Passport plan) are a clear improvement axis; this is a design comparison, not a claim about ST's threat model.
- ST's multi-key-with-active-flag model is a simple rotation scheme. Bazaar can copy the concept: several credentials per provider, one active, rotate without re-entry.
- The `/find` allowlist (only URL-type settings exportable) is a useful pattern: the API can return config but never raw credentials.

### Gaps
- No primary-source ST document on the threat model or on encryption at rest was found. The claim that users hit plain-text key storage is supported by the code, not by a user report I read.
- The search did not find a dedicated GitHub issue on plain-text `secrets.json`. Not verified as a recurring complaint.

## Key Question 6: User pain points and praise (source and proxy UX)

### Takeaway
Users of ST praise breadth of sources, custom endpoints and profiles, and local, free options. The recurring pain is endpoint-compatibility edge cases, proxy and bypass bugs, and confusing overlap between "proxy" meanings. Issues are visible in the repo but I read only the titles and a few bodies.

### Cited Findings (open or recent issues, from [issue search](https://github.com/SillyTavern/SillyTavern/issues?q=is%3Aissue+proxy+password+OR+reverse+proxy+OR+requestProxy))
- #6077 (2026-09-25, open): Text Completion custom source always appends `/completions`; gateways with only `/v1/chat/completions` fail with 404. Verified.
- #5787 (2026-06-20, open): requestProxy bypass wildcards and CIDR ignored. Verified.
- #5793 (open): Image generation seems to ignore the custom OpenAI endpoint. Title only, verified via listing.
- #5786 (open): Google AI Studio reverse proxy stuck at "Not Connected" after refresh. Title only.
- #4883 (open): crash "Invalid URL" when proxy URL contains a space. Title only.
- #5076 (open): Moonshot chat completion uses wrong default endpoint. Title only.
- #5264 (open): severe pre-generation latency from repeated tokenizer counts for OpenAI-compatible and Claude models. Title only.
- #5265 (open): full chat and settings save uploads do not scale; proposes incremental sync. Title only.
- #6114 (open): feature request for an official Reka Chat Completion source. Title only. Shows demand for a native source list to grow.
- #5991 (open): feature request for a PZERO custom OpenAI-compatible endpoint example. Title only; suggests docs gaps for custom endpoints.
- #5825 (closed): OpenRouter status 403 from Cloudflare blocking Node's TLS fingerprint. Title only.
- #4392 (closed): feature request to enforce first-run authentication and private-mode defaults. Title only; relevant to secure defaults.
- All of the above were read from the issue list page (verified for titles and status) and, where stated, the issue page summaries.

### Praise (secondary)
- Breadth and the "one UI, many backends" model, local and free options, and the docs' explicit local-vs-cloud grouping are the most-cited strengths in the docs themselves. Verified only as the docs' own framing; I found no independent praise source read in full. Mark as unverified for user sentiment.

### Inferences for Bazaar's setup screens
- Put an endpoint probe ("Test message" plus "list models") next to every provider, and show the exact request path that will be used. ST's `/completions` and `/v1` confusion is a direct lesson.
- Show the proxy meaning in the label. ST's dual use of "proxy" and "reverse proxy" is a recurring confusion (issue #5787, the docs danger note).
- Make the bypass list and proxy URL validated at save time (issue #4883).
- Do not hide "Skip status check" style escapes without a warning; ST uses one, and it masks real failures.
- Support multiple keys with rotation (ST's model above) and avoid showing full keys by default.

### Gaps
- No user-sentiment survey or Reddit/Discord thread was read in full. The "praise" side is thin.
- Titles-only evidence for most issues; I did not read most issue bodies.

## Licence (AGPL-3.0) and what it means for borrowing

### Takeaway
SillyTavern is AGPL-3.0. Concepts, public vendor facts (URLs, API styles, endpoint paths, setting names) and UX patterns are not copyrightable in the same way as code, but copying code or distinctive expression would bring AGPL obligations into any Apache-2.0 part of Bazaar. This is a counsel question, not a conclusion.

### Cited Findings
- LICENSE file is GNU AGPL v3 (verified, [LICENSE at release](https://github.com/SillyTavern/SillyTavern/blob/release/LICENSE)).
- README states AGPL-3.0 and credits TavernAI (MIT) and kokoro-js (Apache-2.0) as components (verified, [README](https://github.com/SillyTavern/SillyTavern) via fetch summary).
- The project says it "does not provide hosted services or track user data" and "will remain free and open source" (verified, README summary).
- The docs repo is a separate repository (SillyTavern-Docs, "Documentation website for SillyTavern") (verified, [repo page](https://github.com/SillyTavern/SillyTavern-Docs)). Its licence was not read; treat docs text as copyrighted until checked.

### Inferences (not legal advice)
- Bazaar's SDK and contracts are Apache-2.0. Do not copy ST code (including `openai.js`, `chat-completions.js`, `secrets.js`) into those parts. Re-implement from the concepts above.
- Bazaar.Server (AGPL-3.0 direction) can technically copy AGPL code, but copying still needs attribution and AGPL obligations; counsel should decide whether that is wanted.
- Vendor base URLs, model names and API styles are public facts about third-party services. Using them in Bazaar's provider catalogue is a fact-based question for counsel (not verified as settled), but no ST expression is needed to use them.
- Copying ST's docs prose, UI copy or screenshots is a different question from using the ideas. Do not copy docs text.

### Gaps (counsel)
- Whether a catalogue of vendor base URLs derived from ST's code (rather than from vendor docs) carries any copyright or database-right risk. Counsel.
- The SillyTavern-Docs licence was not checked.
- Trademark use of provider names in a UI is a separate question. Counsel.

## Summary of what Bazaar should copy as concepts (not code)
1. A shipped catalogue of named providers, each with an API style (OpenAI-compatible, Anthropic Messages, Gemini, Cohere v2, Azure) and a default base URL. Source the URLs from each vendor's own docs, not from ST code.
2. A generic custom OpenAI-compatible slot with base URL, key, extra headers, extra body include and exclude lists, a test message and a model list probe.
3. Explicit endpoint-path handling (avoid ST's `/completions` and `/v1` ambiguity): show the final request URL.
4. Per-provider credential sets with one active key, rotation without re-entry, and key values masked by default.
5. A profile or preset concept, with a rule about whether a profile may reference a key ID (and never store the key value in a shareable file).
6. Two clearly named proxies: outbound network proxy (HTTP, HTTPS, SOCKS5, bypass list with CIDR support) and per-provider base-URL override (gateway). Validate on save.
7. Do not copy ST's unencrypted `secrets.json` or its CORS proxy; Bazaar's Windows and Linux providers should encrypt at rest (decision belongs to the owner and the Identity design).

## Sources read
- https://docs.sillytavern.app/usage/api-connections/ (verified)
- https://docs.sillytavern.app/usage/api-connections/openai/ (verified, custom endpoint section)
- https://docs.sillytavern.app/usage/core-concepts/connection-profiles/ (verified)
- https://docs.sillytavern.app/administration/reverse-proxying.md (verified)
- https://docs.sillytavern.app/administration/config-yaml/ (secondary, search summary)
- https://github.com/SillyTavern/SillyTavern (README via fetch summary, verified-summary)
- https://github.com/SillyTavern/SillyTavern/blob/release/LICENSE (verified)
- https://github.com/SillyTavern/SillyTavern/releases (verified; see dates below)
- https://github.com/SillyTavern/SillyTavern-Docs (verified)
- https://raw.githubusercontent.com/SillyTavern/SillyTavern-Docs/main/Usage/API_Connections/index.md (verified)
- https://raw.githubusercontent.com/SillyTavern/SillyTavern/release/public/scripts/openai.js (verified, summary)
- https://raw.githubusercontent.com/SillyTavern/SillyTavern/release/src/endpoints/backends/chat-completions.js (verified, summary)
- https://raw.githubusercontent.com/SillyTavern/SillyTavern/release/public/scripts/textgen-settings.js (verified, summary)
- https://raw.githubusercontent.com/SillyTavern/SillyTavern/release/src/endpoints/secrets.js (verified, summary)
- https://raw.githubusercontent.com/SillyTavern/SillyTavern/release/default/config.yaml (verified, summary)
- Issues #6077, #5787, issue list (verified)
- Secondary: apisrouter.com, hpc-ai.com, meganova blog, railway.com deploy pages (vendor or hosting marketing; unverified)

Not reachable (404 or no content): docs.sillytavern.app Chat Completion page (`/usage/api-connections/chat-completion/`), custom-openai page, SillyTavern-Docs `Usage/API_Connections/chat-completion.md`, `Administration/Reverse-Proxying.md`, and the docs site's reverse-proxy API page. The SillyTavern Discord and Reddit were not read.

## Release dates (secondary; year not shown on page)
- 1.19.0 released "14 Sep", 1.18.0 "03 May", 1.17.0 "28 Mar" ([releases page](https://github.com/SillyTavern/SillyTavern/releases)). Year not shown; 2026 is inferred from issue #5787 (opened 2026-06-20 against version 1.18) and is unverified as a fact.
