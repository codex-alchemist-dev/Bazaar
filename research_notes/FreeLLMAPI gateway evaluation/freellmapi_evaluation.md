# FreeLLMAPI as a Bazaar model gateway: evaluation (as of 2026-10-24)

Scope: evaluate https://github.com/tashfeenahmed/freellmapi against the Bazaar requirements: per-agent hard model whitelists, pass-through of a user's own paid Anthropic key, OpenAI-compatible endpoint, free tokens for cheap agents, and the ToS and security risk. Compared with OmniRoute using the existing notes at `/home/user/Bazaar/research_notes/Bazaar stack feasibility research/ai_gateway_models.md`. Not legal advice.

**Evidence labels.** "Verified (primary)" means I read the repository file or docs page through WebFetch. WebFetch returns a model-summarised rendering, so quotes are marked as summarised and should be re-checked against the raw file before they go into a public document. "Secondary" means blogs, aggregators or search summaries. "Unverified" means not confirmed from any source I could read. I did not run, install or clone anything.

**Bottom line.** FreeLLMAPI is a real, MIT-licensed, actively released project. It is a personal-use router that pools the free tiers of many providers behind one OpenAI-compatible endpoint. Its own README and SECURITY.md say it is for personal experimentation, single-user, trusted-network use, and "not production." Against Bazaar's requirements it fails on three points: (1) I found no documented mechanism for a hard per-agent model whitelist, and the chat fallback chain is not documented as family-locked; (2) it does not document any pass-through of a user's own Anthropic key, and its docs say Claude model names "map to your free pool"; (3) its ToS posture is self-assessed and explicitly single-user. It could be a useful sandbox for throwaway experiments on free tiers. It is not a fit as Bazaar's production gateway, and it must not carry the Claude lane. OmniRoute (per the existing notes, not re-verified here) is the better-documented candidate for hard key-level pinning, though it has its own `tos: avoid` and subscription-routing risks.

---

## 1. What FreeLLMAPI is

### Takeaway
An MIT-licensed TypeScript/Node npm-workspaces monorepo with one visible owner and a very fast release cadence (v0.16.9 on 2026-10-23). It runs locally, in Docker, or as a Windows desktop app. Its stated scope is personal, single-user use.

### Cited Findings
- Licence: MIT. Copyright (c) 2026 Tashfeen Ahmed. Verified (primary): [LICENSE](https://github.com/tashfeenahmed/freellmapi/blob/main/LICENSE).
- Language and layout: npm workspaces `shared`, `server`, `client`, `cli`; root package `@freellmapi/monorepo`, `private: true`; Node engines `>=20.18.0 <25.0.0`, npm `>=10`. Verified (primary, summarised): [package.json](https://github.com/tashfeenahmed/freellmapi/blob/main/package.json). TypeScript as the language is inferred from the workspace layout and file paths, and a search-result summary also says TypeScript (secondary, search summary only, no page cited).
- Top-level directories include `docker`, `desktop`, `cli`, `client`, `server`, `docs`, `scripts`, `.github`. Verified (primary, summarised): [repo tree](https://github.com/tashfeenahmed/freellmapi/tree/main).
- Stars and forks: 40.8k stars, 5.5k forks, 1,010 commits on `main`, per the repo page fetched 2026-10-24. Verified (primary, summarised): [repo](https://github.com/tashfeenahmed/freellmapi). Conflict: a secondary listing shows 10,449 stars and 1,665 forks, and another source says "over 3,000 stars in a single week" (undated) — [awesome.ecosyste.ms](https://awesome.ecosyste.ms/projects/github.com%2Ftashfeenahmed%2Ffreellmapi), [OSS Insight](https://ossinsight.io/analyze/tashfeenahmed/freellmapi). The snapshots are of different dates; the star count is not reliable for maturity judgments.
- Maintainers: the repo owner is `tashfeenahmed` (Tashfeen Ahmed). The page lists many contributors but no maintainer roles. Verified (primary, summarised) for the owner; the contributor count is unverified beyond "many."
- Latest release: v0.16.9, published 23 Oct 2026. Recent releases: v0.16.8 (23 Oct), v0.16.7 (21 Oct), v0.16.6 (20 Oct), v0.16.5 (19 Oct), v0.16.4 (19 Oct), v0.16.3 (18 Oct), v0.16.2 and v0.16.1 (18 Oct), v0.16.0 (17 Oct). That is roughly nine releases in seven days. Verified (primary, summarised): [Releases](https://github.com/tashfeenahmed/freellmapi/releases). A secondary listing of "v0.8.9, August 2026" is stale ([newreleases.io](https://newreleases.io/project/github/tashfeenahmed/freellmapi/release/v0.6.1)).
- Last commit date: not obtained. Unverified.
- Install paths, verified (primary, summarised) from [docs/en/install/01-install.md](https://github.com/tashfeenahmed/freellmapi/blob/main/docs/en/install/01-install.md) and the README:
  - One-liner: `curl -fsSL https://freellmapi.co/install.sh | bash`. This pipes a remote script from a non-GitHub domain into a shell. It needs review before use; I did not fetch the script.
  - Docker Compose (port 3001), and local dev with `npm install && npm run dev` (dev UI on 5173). Production without Docker: `npm run build`, then `node server/dist/index.js`. Local development needs Node.js 20+.
  - Desktop installers for macOS (.dmg), Windows (.exe; a portable .zip is also listed), and Linux (AppImage, .deb, .rpm, .tar.xz). The docs call the desktop app "the easiest path" on Windows.
  - Windows: "the Docker steps work in WSL or any bash shell"; PowerShell commands are given for Docker Compose and local dev; building the desktop app from source needs Visual Studio Build Tools (C++) and Python.
  - Android via Termux (experimental).
- Default port 3001; by default the container is published only on `127.0.0.1`; `HOST_BIND=0.0.0.0` is an opt-in "on a trusted network" setting. Verified (primary, summarised): install doc and SECURITY.md.
- Docs quality: a docs index with 19 linked documents (install, API reference, client setup, fallback chains, proxy transports, architecture, a ToS review, a contributor guide). The docs are substantive and are available in English, Simplified Chinese and Russian READMEs. Verified (primary, summarised): [docs/en/README.md](https://github.com/tashfeenahmed/freellmapi/blob/main/docs/en/README.md). Gap: no dedicated access-control or security page is linked from the index.
- Stated scope: the README says "Personal experimentation only" and "This project is for personal experimentation and learning, not production." It also says: "If you build something real on top of FreeLLMAPI, swap in a paid API before you ship." Verified (primary, summarised): README disclaimer section, as relayed by the fetch tool. Re-check the raw text before quoting.
- Commercial element: live catalog updates require a paid "Premium" plan; free installs receive a monthly snapshot about 30 days after a model joins the live feed. Verified (primary, summarised): [repo README](https://github.com/tashfeenahmed/freellmapi). Only one source; its wording should be checked.

### Inferences
- The project is actively maintained by one visible owner, with very frequent releases. This means both fast fixes and fast churn.
- The README's "7.4 billion tokens per month" and "635 free model endpoints" are marketing aggregates. They are not evidence of capacity for Bazaar.

### Gaps
- Last commit date and contributor count not obtained.
- The primary language share (GitHub language bar) was not captured.
- The Windows `.exe` installer's signing status was not verified.
- The `freellmapi.co` install script was not reviewed.

---

## 2. Features: providers, free tokens, routing, endpoint, controls

### Takeaway
It exposes an OpenAI-compatible `/v1` endpoint, plus Anthropic-style `/v1/messages` and Gemini-style `/v1beta`. It pools user-supplied free-tier keys from many providers and falls back on 429/5xx. Model restrictions per client are not documented, beyond per-provider-key caps and "client-profile" keys whose behaviour is not documented.

### Cited Findings
- Provider list: the README names Google, Groq, Cerebras, Mistral, OpenRouter, Cohere and NVIDIA, "plus 22 more," and claims 34 providers and 635 endpoints. Verified (primary, summarised): [repo README](https://github.com/tashfeenahmed/freellmapi). Other providers that appear in the project's ToS review: Cloudflare Workers AI, NVIDIA NIM, Zhipu, Z.ai, Ollama Cloud, OVH AI Endpoints, AI Horde (Verified primary, summarised: [architecture ToS review](https://github.com/tashfeenahmed/freellmapi/blob/main/docs/en/architecture/00-high-level-index.md)). An Anthropic provider does not appear in that review table. Unverified whether Anthropic is a provider at all; see section 3.
- How free tokens are obtained: "Users add their own provider keys on the Keys page"; the project aggregates "free tiers from dozens of providers." Verified (primary, summarised): README. The project's ToS review gives one rule of thumb: "one account per provider." The docs do not describe creating accounts, multiple accounts per provider, browser sessions, scraping or consumer web endpoints. Verified (primary, summarised): [architecture ToS review](https://github.com/tashfeenahmed/freellmapi/blob/main/docs/en/architecture/00-high-level-index.md). The Fetch Relay doc describes relaying provider API requests through a Cloudflare Worker, not web sessions: [docs/en/proxy/01-fetch-relay.md](https://github.com/tashfeenahmed/freellmapi/blob/main/docs/en/proxy/01-fetch-relay.md) (verified primary, summarised). It says "A Relay can see provider credentials and request content."
- Key rotation and per-key counters: "automatic fallover retries the next model on 429/5xx with cooldowns and key rotation"; "Per-key RPM/RPD/TPM/TPD counters are tracked per (platform, model, key)." Verified (primary, summarised): README and [docs/en/api/01-rest-api.md](https://github.com/tashfeenahmed/freellmapi/blob/main/docs/en/api/01-rest-api.md).
- OpenAI-compatible endpoint: base URL `http://localhost:3001/v1`; `/v1/chat/completions`, `/v1/models`, `/v1/responses`, `/v1/embeddings`, plus images, video and audio routes; `Authorization: Bearer`. Verified (primary, summarised): [API reference](https://github.com/tashfeenahmed/freellmapi/blob/main/docs/en/api/01-rest-api.md). Anthropic-style `/v1/messages` and `/v1/messages/count_tokens` accept `x-api-key` or Bearer. Gemini-style `/v1beta` uses `x-goog-api-key`.
- Streaming: supported (`stream=True`, `delta` chunks). Tool calling: "OpenAI-style `tools` round-trip across providers"; OpenAI-compatible providers receive tool requests unchanged, and Gemini requests are translated. Verified (primary, summarised): API reference.
- Model aliasing and combos: plain `auto` follows the active fallback chain; `auto:smart`, `auto:fast`, `auto:cheap`, `auto:reliable`, `auto:balanced` rank all enabled models; `auto:<profile-name>` uses a named profile's chain; a `fusion` virtual model fans out and synthesises. Verified (primary, summarised): API reference.
- Routing strategies: "six strategies" with live per-model scores (README). Routing controls added in v0.16.0 include minimum context window and "Claude Code tiers"; v0.16.3 adds an opt-in "Decision" strategy. Verified (primary, summarised): [Releases](https://github.com/tashfeenahmed/freellmapi/releases).
- Monthly caps: per-key `requestCap` and `tokenCap` set via `PATCH /api/keys/:id` (`monthlyRequestCap`, `monthlyTokenCap`); "`0` means unlimited"; usage resets at 00:00 UTC on the 1st; "Reservations apply within one gateway process only"; spend "is **not** clamped at the cap." Verified (primary, summarised): API reference. v0.16.9 adds soft budget alerts at 80% and 100% of monthly caps (Releases page).
- Usage logging: `GET /v1/usage` returns monthly requests and tokens from a durable ledger, by platform and by key (unified key only). Analytics windows 24h, 7d, 30d, 90d; logs go to the container log or `<data dir>/logs/freeapi.log` on desktop. Verified (primary, summarised): API reference, README, install doc.
- Admin UI: a React dashboard, login-gated, managing keys, chain order, playground and analytics. Verified (primary, summarised): README. Also `/api/backups` (encrypted SQL dumps, scheduled) and `/api/free-tier` (behind dashboard session auth).
- Per-client keys and model restrictions: the README and API reference describe one unified bearer key from the Keys page. The API reference also mentions "Client-profile keys (`sk-cp-…`)" that work on `/v1/messages` and `/v1beta`, "with the profile's enforced prompt injected," and says keys are "scoped away from this model" without explaining the mechanism. The agent-client doc does not mention `sk-cp-` keys; it mentions "separately revocable token[s] on **Keys → Agents**" for clients that cannot set headers. Verified (primary, summarised) for each quote; these sources do not agree in full. The mechanism that restricts models per client is not documented.
- Fallback trail: failed hops are recorded in `X-Fallback-Trail`; `X-Fallback-Detail` is off by default. When all keys are exhausted, chat, embeddings and media return `429` with `Retry-After`; mixed failures return `502` without `Retry-After`. Verified (primary, summarised): API reference.
- Embeddings fallback is restricted to the same family: "failover only walks the providers serving that same family," and "Failover never switches to a different model family." Verified (primary, summarised): API reference. The same explicit family-lock wording is not given for chat. For chat, `auto` follows the fallback chain, and the chain may contain other models. See section 3.
- Authentication of its own API: one unified bearer key (`freellmapi-…`), and optional client-profile or URL-token routes (`/v1/t/{token}/…`). Dashboard login is email plus password on server installs. Verified (primary, summarised): API reference, install doc.
- Response cache: the API reference says "The response cache can replay streams verbatim on a hit." Cache scope and whether it is keyed per client are not documented. Gap.
- Prompt compression pipeline: a `compression/01-compression-pipeline.md` doc exists; not read. Unverified.

### Inferences
- The unified-key design is single-tenant. Several agents sharing one key share one set of provider limits and one fallback chain.
- Per-provider monthly caps and per-key counters are the nearest thing to per-agent budgets, but they are per provider key, not per agent.

### Gaps
- Could not establish whether a client-profile key (`sk-cp-`) can restrict or force a model. Check the Keys page code.
- Could not establish whether chat fallback can cross model families. The docs only state that for embeddings.
- No list of the 34 providers with exact free-tier terms was obtained.
- The compression pipeline doc and the Ollama emulation (`/api/*`, default off) were not read.

---

## 3. Can it enforce a hard per-agent whitelist, and does it pass through a user's own Anthropic key?

### Takeaway
No, not on the documented evidence. The docs show one unified key plus unexplained client-profile keys. Per-provider caps are not per agent. Claude model names are documented as mapping to the free pool, and there is no documented pass-through of an operator's own Anthropic key.

### Cited Findings
- Per-agent whitelist: the API reference describes per-provider-key monthly caps and says the only route that calls `/v1/usage` is the unified key. It does not describe per-client model allowlists. Verified (primary, summarised): [API reference](https://github.com/tashfeenahmed/freellmapi/blob/main/docs/en/api/01-rest-api.md).
- Claude mapping: "On the Anthropic tab, each Claude family (`default`, `opus`, `sonnet`, `haiku`) is set to a pool-wide route or a pinned model." and "Claude model names map to your free pool." Verified (primary, summarised): API reference. This means a Claude request is served by whichever provider is in the free pool, not by Anthropic, unless a model is pinned to a pool entry.
- Anthropic passthrough: the API reference does not say `/v1/messages` forwards to Anthropic, and it does not describe passthrough of a user's own Anthropic key. Verified (primary, summarised) as an absence: [API reference](https://github.com/tashfeenahmed/freellmapi/blob/main/docs/en/api/01-rest-api.md). The client doc says only that Claude Code "takes the server root because it appends the Anthropic Messages path itself"; it does not discuss the user's key. Verified (primary, summarised): [supported agents](https://github.com/tashfeenahmed/freellmapi/blob/main/docs/en/clients/02-supported-agents.md).
- v0.16.0 added "Claude Code tiers" as routing controls, so Claude Code requests can be routed to tiers in the pool. Verified (primary, summarised): [Releases](https://github.com/tashfeenahmed/freellmapi/releases).
- Claude Code setup: `npx freellmapi setup-claude --url … --api-key <unified-key>` or `setup-claude`/`launch`. Verified (primary, summarised): supported agents doc.
- Anthropic's terms on routing Claude Code through a third party: Verified (primary, read 2026-10-24): [Claude Code legal and compliance](https://code.claude.com/docs/en/legal-and-compliance). Key sentences: "Customers may not pay for, resell, or intermediate Claude usage on their end users' behalf. Each end user must authenticate with their own Anthropic API key, Claude subscription plan credentials, or 3P inference provider credential." Also: "The Claude Code binary must not be modified." And: "Anthropic does not permit third-party developers ... to route requests through Free, Pro, or Max plan credentials on behalf of their users." The same page says configuring an operator's own API key in "a secrets manager ... for use by the customer's own authorized users" is allowed when usage is billed to the key owner. It also says advertised Pro and Max limits "assume ordinary, individual usage." The HIPAA section says "Claude Code routed through a third-party cloud provider or a gateway aren't covered" (a HIPAA configuration note, not a ToS clause).
- Cross-check with the existing feasibility notes (secondary for this task): [ai_gateway_models.md, section 3](/home/user/Bazaar/research_notes/Bazaar%20stack%20feasibility%20research/ai_gateway_models.md) reports the same legal page and an enforcement timeline (2026-01 to 2026-04) from press sources.

### Inferences
- FreeLLMAPI cannot meet the requirement "an agent's key can only reach specific models and never falls back outside them" on the documented evidence. Bazaar would need to verify this in the code (`server/` routing and key scoping), or wrap it with its own allowlist.
- Using FreeLLMAPI as the Claude lane would route Anthropic-model requests to free-pool providers that are not Anthropic. That breaks the Bazaar rule (Anthropic API keys, Haiku-heavy budget) and makes the model unknown to the agent. It also risks the Claude Code terms above if Claude Code is pointed at the gateway with a subscription login.
- The Claude lane should go direct to the Anthropic API with the operator's own key. FreeLLMAPI should not sit in that path.

### Gaps
- Could not read the routing or key-scoping code (`server/src`) to confirm or refute per-key allowlists or family-locked fallback for chat.
- Could not confirm whether a user's Anthropic API key can be configured as a provider. No evidence either way was found.

---

## 4. Terms of service and legal risk for the free-tier pooling model

### Takeaway
The project's own ToS review (April to June 2026) rates most providers "Likely OK" for a single-user proxy, rates Google and NVIDIA "Caution," and rates Cohere "Avoid." That review is self-assessed, scoped to personal single-user use, and says it is not legal advice. Bazaar is a multi-agent company platform, outside that scope. I did not check any provider's terms directly.

### Cited Findings
- The review verdicts, verbatim as summarised: Google Gemini "⚠️ Caution" (a March 2026 ToS limits use to "professional or business purposes, not for consumer use"); Groq "✅ Likely OK"; Cerebras "✅ Likely OK" ("API keys may not be sold or transferred"); Mistral "✅ Likely OK"; OpenRouter "✅ Likely OK" (an April 2026 ToS tightens the no-resale clause); Cloudflare Workers AI "⚠️ Ambiguous"; NVIDIA NIM "⚠️ Caution" ("evaluation only, not production"; "Free access is a recurring 40 RPM limit"); Cohere "❌ Avoid" ("exclude 'personal, family or household purposes'"); Zhipu "✅ Likely OK"; Z.ai "⚠️ Caution"; Ollama Cloud "✅ Likely OK"; OVH AI Endpoints "✅ Likely OK"; AI Horde "✅ Likely OK". Disclaimer: "This is informational, not legal advice — read each provider's ToS and make your own call." Verified (primary, summarised): [architecture ToS review](https://github.com/tashfeenahmed/freellmapi/blob/main/docs/en/architecture/00-high-level-index.md).
- Scope of the review: "a self-hosted, single-user, personal-use setup." Verified (primary, summarised): same page.
- Secondary: a May 2026 blog write-up describes a similar provider ToS table (search snippet only; the page was not fetched, so its content is not verified): [mer.vin, May 2026](https://mer.vin/2026/05/freellmapi-stack-free-llm-api-tiers-into-one-openai-endpoint-1b-tokens-month/). The primary verdicts are the architecture doc above.
- The README disclaimer: "Your relationship with each upstream provider is governed by the terms you accepted when you created your account ... you're responsible for complying with them." Verified (primary, summarised): README.
- Pooling several free-tier keys: the project supports multiple keys per provider (per-key counters and key rotation), and the review's rule of thumb is "one account per provider." Whether multiple accounts per provider are allowed by a given provider was not checked. Unverified.
- Reselling or sharing: Cerebras says keys "may not be sold or transferred" (per the review table). OpenRouter tightened its no-resale clause in April 2026 (per the review table). Bazaar's use would not resell, but a company gateway serving many agents and users is a grey area under the review's own scope. Unverified against primary provider terms.
- Non-free-tier risk: a search-result summary says the project notes "free tiers change without notice" and "no SLA" (secondary, search summary only; the wording was not seen in the repo). Unverified against the repo.
- Subscription and OAuth credentials: the API reference, supported agents doc, and README do not mention Claude Pro/Max, Gemini CLI OAuth or other subscription logins as providers. The Gemini-style route accepts `x-goog-api-key`, and the Gemini CLI setup uses `/v1beta` with "the native wire format." Verified (primary, summarised). Whether the Gemini CLI setup uses a subscription login is not stated. Gap. The feasibility notes flag Google's Gemini CLI and Antigravity quota pooling as undocumented (see [ai_gateway_models.md, section 4](/home/user/Bazaar/research_notes/Bazaar%20stack%20feasibility%20research/ai_gateway_models.md)).
- Web sessions and scraping: no documentation of browser sessions, scraping or consumer-web-endpoint access was found in the docs I read. The Fetch Relay is an HTTP relay for provider APIs. Not verified beyond the docs I read; I did not read the code.
- The `Premium` plan: the paid tier sells live catalog updates. This is a commercial element in a repo whose own disclaimer says personal use only. Verified (primary, summarised): README.

### Inferences
- The project's legal posture is "personal experimentation." Bazaar, as a shared company platform open-sourced under AGPL with an agent company as its first user, falls outside the posture the project has assessed. The provider verdicts do not cover multi-user, multi-agent company use.
- Using Gemini or Claude free or subscription credentials through FreeLLMAPI is a different risk from using free API tiers. Section 3 applies.
- Counsel review is needed before any production use or public promotion of a FreeLLMAPI-backed config. This matches the feasibility notes' recommendation.

### Gaps
- Provider terms for Groq, Cerebras, Mistral, OpenRouter, Google, NVIDIA and Cohere were not read. The review table is the project's own self-assessment: it is a primary source for what the project says, not for the providers' terms.
- Anthropic Commercial Terms were not read. Unverified whether free-pool routing of Claude-named models violates them. Anthropic's rules on routing Claude Code (section 3) were read.

---

## 5. Security

### Takeaway
Keys are encrypted at rest with AES-256-GCM. There is no default password, but an auto-generated development encryption key is written to a file next to the database. The project explicitly says it is single-user, trusted-network only, with no multi-tenant auth. A Node/Brotli CVE fix ships in v0.16.8 (CVE ID unverified). I found no FreeLLMAPI-specific CVE or advisory.

### Cited Findings
- Provider keys are "AES-256-GCM encrypted in SQLite" and decrypted per request. Verified (primary, summarised): README. The install doc says `ENCRYPTION_KEY` is required for startup; without it in non-production, the server generates a development key and saves it to a `.encryption-key` file (0600) beside the SQLite database; "Do not rely on that fallback with real provider keys." Verified (primary, summarised): [install doc](https://github.com/tashfeenahmed/freellmapi/blob/main/docs/en/install/01-install.md).
- Default credentials: none listed. Server installs have an email and password account with a first-run setup code. Desktop installs sign in automatically with a hidden local account, so no password is required. Verified (primary, summarised): install doc.
- Threat model: "FreeLLMAPI is a single-user, trusted-network tool," it "binds `127.0.0.1` by default," and "no multi-tenant auth by design." Exposing it to the public internet is out of scope. `HOST_BIND=0.0.0.0` should sit behind a reverse proxy with TLS and auth. Verified (primary, summarised): [SECURITY.md](https://github.com/tashfeenahmed/freellmapi/blob/main/SECURITY.md).
- Supported versions in SECURITY.md: "Security fixes land only in the 0.13.x line," with 0.12.x best-effort and 0.11.x and older unsupported. Conflict: the release line is now 0.16.x (see section 1). Either the policy is stale or it is not maintained for the current line. Verified (primary, summarised) for each side. Reporting: GitHub private vulnerability reporting, or support@freellmapi.co.
- Fetch Relay: "A Relay can see provider credentials and request content," and the relay uses a separate `Fetch-Relay-Authorization` token. Verified (primary, summarised): [fetch relay doc](https://github.com/tashfeenahmed/freellmapi/blob/main/docs/en/proxy/01-fetch-relay.md).
- Host-header allowlist: v0.16.8 adds "an opt-in Host-header allowlist to block DNS rebinding." Verified (primary, summarised): [Releases](https://github.com/tashfeenahmed/freellmapi/releases). The same release says Node 20 is dropped and the Docker image moves to Node 24.22.1.
- CVE: v0.16.8 "upgrades the Docker Node runtime to fix a Brotli decoder out-of-bounds write (CVE-2026-111497)." Verified only as the release-page summary; the CVE ID was not confirmed against NVD or GitHub Advisories. Unverified. A web search for a FreeLLMAPI-specific CVE returned nothing relevant (secondary; search result only, no advisory found). The CVE is in the Node runtime, not in FreeLLMAPI code.
- Prompt logging: the README says the catalog server "never sees prompts, completions, or provider keys" (catalog sync, not the gateway). Whether the gateway logs prompt or completion bodies to its logs or database was not established. Gap. The usage ledger stores counts; the logs location is in the install doc. The Fetch Relay doc says the design avoids logging sensitive data; that is a design statement, not a test result.
- Upgrade risk (data loss): v0.16.0 "removes GitHub Models. Leftover GitHub keys and custom models are deleted on update." v0.16.4 "removes Auray. Its keys, models, and routing entries are deleted on upgrade." Verified (primary, summarised): [Releases](https://github.com/tashfeenahmed/freellmapi/releases). Encryption key must be preserved across upgrades: "Keep the same key when upgrading." Verified (primary, summarised): install doc.
- Node version: local dev needs Node 20+ while the Docker image is on Node 24.22.1 (v0.16.8). The engines field allows `<25.0.0`. Verified (primary, summarised).
- Remote catalog and Premium activation: v0.16.5 fixes "Premium activation failing with HTTP 502 on some Windows PCs by retrying through Chromium's network stack." This implies an embedded Chromium network stack in the desktop build (inference). Verified (primary, summarised): Releases. Not investigated further.

### Inferences
- A single-owner, fast-release project with breaking upgrades that delete keys is a configuration-management risk for a persistent company. Bazaar would need pinned versions, backup of the encrypted database and the encryption key, and a staging check on every upgrade.
- The Host-header allowlist is opt-in, so a default install still relies on the `127.0.0.1` binding. Any Bazaar deployment that exposes the gateway must enable the allowlist and place it behind TLS.

### Gaps
- No independent security audit or CVE database entry for FreeLLMAPI itself was found.
- The code path for prompt and response logging was not read.
- The `freellmapi.co/install.sh` one-liner was not reviewed.

---

## 6. Comparison with OmniRoute

OmniRoute facts below come from the existing notes at `/home/user/Bazaar/research_notes/Bazaar stack feasibility research/ai_gateway_models.md` (section 1). I did not re-read the OmniRoute repository for this report. Treat those OmniRoute points as secondary.

| Criterion | FreeLLMAPI (this report) | OmniRoute (from existing notes) |
|---|---|---|
| Licence | MIT, Copyright 2026 Tashfeen Ahmed (verified, primary LICENSE) | MIT (secondary, per notes) |
| Maturity and stars | 40.8k stars and 5.5k forks on the fetched repo page (conflicting snapshots; see section 1); 1,010 commits | Not captured in the notes; unverified |
| Maintenance | One visible owner; v0.16.9 on 2026-10-23; about nine releases in the week to 2026-10-23 (verified primary). Some docs lag (SECURITY.md lists 0.13.x) | "Release branch v3.8.53" with changelog to v3.8.53; no release dates captured in the notes; the notes say release cadence was not verified |
| Per-agent model whitelist | Not documented; per-provider caps only; client-profile keys unexplained (sections 2 and 3). Embeddings are family-locked; chat is not documented as family-locked | Documented: per-key `forcedModel` and per-key allowed-model lists via `/api/keys/{id}/access` (per the notes; README not re-read) |
| Anthropic pass-through of own key | Not documented; Claude names map to the free pool (verified primary, summarised) | Not in the notes; `auto/subscription` and `tos: avoid` markers are flagged in the notes |
| Subscription-routing risk | Not documented for FreeLLMAPI; Claude Code setup is documented; Anthropic legal page applies | The notes flag `auto/subscription` as a subscription-routing risk; do not enable |
| Windows support | Native Windows desktop `.exe`, portable `.zip`, and PowerShell Docker/dev commands (verified primary) | Not verified; Docker plausible (notes) |
| Designed for production | No: "personal experimentation only" (verified primary) | Not stated in the notes |
| Security defaults | Localhost bind; `ENCRYPTION_KEY` required; no default password; opt-in Host allowlist (verified primary) | Notes cite a `CHANGEME` dashboard default in a secondary guide (unverified) |

### Takeaway
On the features Bazaar needs (hard per-agent key pinning and Anthropic pass-through), OmniRoute is the better-documented candidate on the evidence in the notes, and FreeLLMAPI does not document either. Neither should carry the Claude lane through a shared pool. FreeLLMAPI is more transparent about its single-user scope, has more stars, and has Windows-native installers, which may matter for a Windows host.

### Gaps
- OmniRoute was not re-verified in this task. Its per-key pinning claim should be checked against its code before any decision.

---

## Recommendation (for the main session to weigh)

1. Do not use FreeLLMAPI as Bazaar's production gateway. Its own docs say "not production," "single-user," and "trusted-network."
2. Keep the Claude lane on direct Anthropic API keys, not through any free-pool gateway. Section 3 shows Claude names map to the free pool, and Claude Code routing through a gateway is restricted by Anthropic's legal page.
3. If a free-token sandbox is wanted for throwaway experiments by cheap agents, FreeLLMAPI could be run locally on Windows for a single operator, with: a pinned version (not `latest`), a backed-up encrypted database and `ENCRYPTION_KEY`, the bind kept at `127.0.0.1`, and no subscription or OAuth credentials. Even then, an external whitelist check must sit in front of it (Bazaar's own per-agent allowlist), because FreeLLMAPI does not enforce one as documented.
4. Before any such use, read the code for key scoping and chat fallback family locks, and check the NVIDIA, Google and Cohere terms directly (both are rated Caution or Avoid in the project's own review).
5. Counsel review before public promotion, per the existing feasibility notes. Not legal advice.

## Unverified or not obtained (summary)
- Last commit date; primary language share; contributor count.
- Whether client-profile keys (`sk-cp-`) can restrict models; whether chat fallback crosses families.
- Whether a user's own Anthropic key can be used unchanged (no evidence either way beyond the docs).
- Whether prompts or completions are logged by the gateway.
- The CVE-2026-111497 claim (release note only).
- Provider ToS text for any provider (only the project's own review was read).
- OmniRoute's stars, release dates and README (only the existing notes were used).
- The `freellmapi.co` install script contents.
- The FreeLLMAPI code itself: no code was read beyond package.json and the directory tree.

## Sources (all accessed 2026-10-24)
- Repo: https://github.com/tashfeenahmed/freellmapi
- LICENSE: https://github.com/tashfeenahmed/freellmapi/blob/main/LICENSE
- SECURITY.md: https://github.com/tashfeenahmed/freellmapi/blob/main/SECURITY.md
- package.json: https://github.com/tashfeenahmed/freellmapi/blob/main/package.json
- Releases: https://github.com/tashfeenahmed/freellmapi/releases
- API reference: https://github.com/tashfeenahmed/freellmapi/blob/main/docs/en/api/01-rest-api.md
- Supported agents: https://github.com/tashfeenahmed/freellmapi/blob/main/docs/en/clients/02-supported-agents.md
- Clients and coding agents: https://github.com/tashfeenahmed/freellmapi/blob/main/docs/en/clients/01-agent-clients.md
- Install: https://github.com/tashfeenahmed/freellmapi/blob/main/docs/en/install/01-install.md
- Architecture and ToS review: https://github.com/tashfeenahmed/freellmapi/blob/main/docs/en/architecture/00-high-level-index.md
- Fetch Relay: https://github.com/tashfeenahmed/freellmapi/blob/main/docs/en/proxy/01-fetch-relay.md
- Docs index: https://github.com/tashfeenahmed/freellmapi/blob/main/docs/en/README.md
- Anthropic, Claude Code legal and compliance: https://code.claude.com/docs/en/legal-and-compliance
- Secondary: https://mer.vin/2026/05/freellmapi-stack-free-llm-api-tiers-into-one-openai-endpoint-1b-tokens-month/ ; https://awesome.ecosyste.ms/projects/github.com%2Ftashfeenahmed%2Ffreellmapi ; https://ossinsight.io/analyze/tashfeenahmed/freellmapi ; https://newreleases.io/project/github/tashfeenahmed/freellmapi/release/v0.6.1
- Existing notes: `/home/user/Bazaar/research_notes/Bazaar stack feasibility research/ai_gateway_models.md`
