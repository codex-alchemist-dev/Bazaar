# Bazaar Model-Gateway Layer: OmniRoute, Free-Tier Supply, Subscription ToS Risk (as of 2026-10-24)

Scope: verify the design-doc claims in Bazaar.md and Bazaar.Server.md about OmniRoute, NVIDIA NIM free-tier supply, Claude/Gemini subscription routing, model names, and token-compression tools. Sources are dated where a date is available. Several sources are secondary (news, forums, aggregators) and are labelled as such. This is research, not legal advice.

**Bottom line.** (1) OmniRoute is real, MIT-licensed, and does have per-key model pinning, so a hard per-agent whitelist is plausible. Its provider and model counts are inconsistent across sources. (2) Routing a Claude Pro/Max subscription through a shared gateway for Bazaar agents is prohibited by Anthropic's current Claude Code legal page. Bazaar must use Anthropic API keys for the Claude lane. (3) NIM's free endpoint has a widely reported ~40 RPM limit with no official source, and no official daily limit was found. The "~1,000 req/day" figure in the design docs is unsourced. (4) "Fable" is a real Anthropic model name in press coverage, but its availability is disputed. Verify before whitelisting it. (5) Gemini quota pooling across Antigravity and Gemini CLI is not documented by Google. (6) RTK and Caveman savings are self-reported and mostly unverified for source code.

---

## 1. What OmniRoute is, and whether it can enforce a hard model whitelist

### Takeaway
OmniRoute (github.com/diegosouzapw/OmniRoute) is a self-hosted, MIT-licensed gateway with an OpenAI-compatible endpoint. Its README documents combos, auto/* tags, 20 routing strategies, and per-API-key model pinning (`forcedModel`) plus per-key allowed-model lists. That is enough to build a per-agent whitelist, but it has not been tested. Its provider and model counts disagree across sources. It also has a `tos: avoid` marker, and an `auto/subscription` tag that touches the subscription-routing risk in section 3.

### Cited Findings
- License MIT. Repo belongs to the `cheaper-inference` org. The page names GitHub user `diegosouzapw` as maintainer and claims "Built by 900+ contributors." Release branch shown is `release/v3.8.53`, with a changelog covering v3.8.20 to v3.8.53. No release date is shown on the fetched page. — [OmniRoute GitHub README](https://github.com/diegosouzapw/OmniRoute) (fetched 2026-10-24)
- Provider and model counts are inconsistent on the same page: "375 providers (150+ free), 1,400+ models" in the About text; "375 registered providers" with "153 carrying hasFree: true" in the body; "239 providers / 3,059 distinct provider-model pairs / 1,475 raw model IDs" in the chat registry. Other sources cite 290+ providers (about 516 models) and 340 providers (1,200+ models). Use neither "230+" nor "250+" without a dated snapshot. — [OmniRoute GitHub README](https://github.com/diegosouzapw/OmniRoute); [Pinggy write-up](https://pinggy.io/amp/blog/omniroute_ai_gateway_security/); [developer profile listing](https://github.com/diegosouzapw)
- Auto and combo tags named in the README: `auto`, `auto/coding`, `auto/fast`, `auto/cheap`, `auto/subscription`, `auto/thrifty`, `auto/offline`, `auto/smart`, `auto/lkgp`, `auto/chaos`, plus `auto/<category>:<tier>` in release notes. The design doc's `auto/cheap` and `auto/*` tags exist. `auto/subscription` is a tag that routes to subscription-backed accounts, which is the risk in section 3. — [OmniRoute GitHub README](https://github.com/diegosouzapw/OmniRoute)
- Routing strategies (20): priority, fill-first, weighted, round-robin, p2c, least-used, random, strict-random, cost-optimized, headroom, quota-weighted, reset-window, reset-aware, context-relay, context-optimized, cache-optimized, lkgp, auto, fusion, pipeline. — [OmniRoute GitHub README](https://github.com/diegosouzapw/OmniRoute)
- Per-key restrictions: keys can be pinned to one model or combo with `forcedModel`. A key's allowed models and combos can be set atomically via `/api/keys/{id}/access`. This is the mechanism for a hard per-agent whitelist. — [OmniRoute GitHub README](https://github.com/diegosouzapw/OmniRoute)
- Circuit breakers: three layers. The provider-level breaker trips only on 408/5xx, with thresholds that vary by connection type. Connection cooldowns and per-model lockouts sit below it. — [OmniRoute GitHub README](https://github.com/diegosouzapw/OmniRoute)
- Context-optimized routing exists as "Pick the best fit for the current context size", and context-relay hands context across targets for long conversations. — [OmniRoute GitHub README](https://github.com/diegosouzapw/OmniRoute)
- Security features per README: AES-256-GCM encrypted keys, a prompt-injection guard on LLM routes, opt-in credential masking, an optional OIDC login gate for the dashboard. Providers marked `tos: avoid` (including Kiro) are excluded from automatic routing by default. Warns not to commit the generated local `.env`. — [OmniRoute GitHub README](https://github.com/diegosouzapw/OmniRoute)
- Install options: Docker, npm, source, pnpm, an Electron desktop build, and Termux are named. No Windows-specific steps appear in the fetched text. Docker and npm commands are documented in third-party guides. — [Pinggy write-up](https://pinggy.io/amp/blog/omniroute_ai_gateway_security/) (secondary)
- Secondary security note: the same guide warns that the dashboard admin login defaults to the literal string `CHANGEME` unless `INITIAL_PASSWORD` is set. A Medium article warns about CVEs and "fine print". Neither was verified against primary sources. — [Pinggy write-up](https://pinggy.io/amp/blog/omniroute_ai_gateway_security/); [Medium article](https://levelup.gitconnected.com/the-ai-tool-that-promises-1-6-billion-free-claude-tokens-and-why-you-shouldnt-touch-it-a63c687f7d91) (secondary, unverified)
- A fork, v0l/OmniRoute, claims 67+ providers. Its relation to the main project is unclear. — [v0l/OmniRoute](https://github.com/v0l/OmniRoute)

### Inferences
- A hard per-agent whitelist looks feasible: one API key per agent, each pinned with `forcedModel` or an allowed-model list. The design docs' "no global auto/*" rule can be enforced at the key level, not by convention. This is inferred from the README, not tested.
- The "Medium: 1.6 billion free Claude tokens" headline points to a claim that the gateway offers free Claude access. Treat that claim as a red flag, not a feature.
- The `tos: avoid` marker shows the project itself acknowledges provider-ToS risk. Any Bazaar lane that depends on a provider marked `avoid` should be excluded.

### Gaps
- Not verified: whether OmniRoute bundles built-in Claude Pro/Max or Google OAuth providers. The README names subscription routing via `auto/subscription` but I did not confirm which providers that tag uses.
- Not verified: release cadence. Only one release-branch name and no dates were visible.
- Not verified: Windows native support. Docker on Windows is plausible, but no source confirms it.
- Not verified: the CVE claims and the `CHANGEME` default. Check the GitHub Security tab and the current release notes before deploying.

---

## 2. NVIDIA NIM free tier: rate limits, DeepSeek V4 availability, terms

### Takeaway
The "40 RPM" figure is widely reported in NVIDIA developer forum threads, but no official NVIDIA rate-limit page was found. The "~1,000 requests/day" figure in the design docs was not found in any source I checked. NIM hosts a DeepSeek V4 Pro page at build.nvidia.com, and the endpoint is governed by NVIDIA's API Trial Terms, not production terms. Plan for the free tier to be unreliable and rate-limited.

### Cited Findings
- Commonly reported limit: about 40 RPM per account. A forum moderator says there is no official route to a rate-limit increase on the free tier. Forum posts repeatedly ask for 40 to 200 RPM increases with no result. — [NVIDIA forum: "Request for NVIDIA Build API Rate Limit Increase (40 RPM -> 200 RPM)"](https://forums.developer.nvidia.com/t/request-for-nvidia-build-api-rate-limit-increase-40-rpm-200-rpm/378450); [NVIDIA forum: "API Rate Limit Increase is NOT granted by requesting it here"](https://forums.developer.nvidia.com/t/api-rate-limit-increase-is-not-granted-by-requesting-it-here/368420) (forum posts, not official)
- A secondary blog says the figure varies by model and that the build.nvidia.com dashboard shows the personal figure. A DEV Community post says NVIDIA publishes no per-model SLA. — [Medium: NIM + OpenCode free tier](https://medium.com/@vignarajj/beyond-the-hype-supercharging-my-local-development-workflow-with-nvidia-nim-and-opencode-free-6ff12d6f851e); [DEV Community](https://dev.to/creeta/two-lines-swap-openai-for-160-free-nvidia-hosted-models-njg) (secondary)
- Credit system status is contradictory: one Medium post says credits were phased out in early 2025, while a later forum post still refers to a 1,000-credit allowance. — [NVIDIA forum: credit request thread](https://forums.developer.nvidia.com/t/credit-rate-limit-increase-request-1-000-5-000-credits-40-200-rpm/380932) (forum post); [Medium post](https://medium.com/@vignarajj/beyond-the-hype-supercharging-my-local-development-workflow-with-nvidia-nim-and-opencode-free-6ff12d6f851e) (secondary)
- The build.nvidia.com page for `deepseek-ai/deepseek-v4-pro` exists. It names the model `deepseek-v4-pro-0813`, lists a 08/24/2026 release date on build.nvidia.com (Hugging Face shows 08/13/2026), and says "This trial service is governed by the NVIDIA API Trial Terms of Service." It does not state rate limits or a free-tier quota. Context length is given as 262,144 tokens in one place and "Input Context Length 1,000,000" in another, so the 1M figure is inconsistent. It says the model may be used "for commercial or non-commercial use" under the NVIDIA Open Model Agreement, which governs the weights, not the hosted endpoint. — [build.nvidia.com DeepSeek V4 Pro](https://build.nvidia.com/deepseek-ai/deepseek-v4-pro) (fetched 2026-10-24)
- One source describes the hosted free endpoints as "for prototyping and evaluation, not production." This is a secondary characterization. — [NVIDIA forum threads](https://forums.developer.nvidia.com/t/is-nim-free/369368) (secondary)

### Inferences
- The endpoint is a trial service. Its terms, not the model license, control whether a production Bazaar company can depend on it. The NVIDIA API Trial Terms were not fetched, so this is unverified.
- Because the 40 RPM figure is per account and there is no published daily cap, "near-infinite supply" in the design docs is not supported. Multiple agents sharing one key will hit the limit quickly.
- Because the forums say the only route to higher limits is paying and deploying, budget a fallback provider for the DeepSeek lane.

### Gaps
- No official NVIDIA source for the 40 RPM figure or the 1,000 requests/day figure. Check the build.nvidia.com account dashboard for the actual limits on the live key.
- Not verified: whether `deepseek-v4-flash` is hosted on NIM and under what limits. Only the Pro page was fetched.
- Not fetched: NVIDIA API Trial Terms of Service. The prototyping-only claim needs this document.
- Context length conflict (262K vs 1M) on the NIM page is unresolved.

---

## 3. Routing Claude Pro/Max or Gemini OAuth through a gateway: terms and enforcement

### Takeaway
This is the highest-risk item in the plan. Anthropic's current Claude Code legal page states that OAuth subscription credentials are for ordinary use of Claude Code and native apps, and that developers building products must use API keys. It states that Anthropic "does not permit third-party developers ... to route requests through Free, Pro, or Max plan credentials on behalf of their users." The Claude Pro subscription should not be routed through Bazaar agents or through OmniRoute. The Bazaar Claude lane must use an Anthropic API key. Google's position on the Gemini CLI and Antigravity OAuth was not verified from a primary source.

### Cited Findings
- Claude Code legal page (current): "OAuth authentication is intended exclusively for purchasers of Claude Free, Pro, Max, Team, and Enterprise subscription plans and is designed to support ordinary use of Claude Code and other native Anthropic applications." "Developers building products or services ... including those using the Agent SDK, should use API key authentication through Claude Console or a supported cloud provider. Anthropic does not permit third-party developers to offer Claude.ai login into their own applications, or to route requests through Free, Pro, or Max plan credentials on behalf of their users." "Anthropic reserves the right to take measures to enforce these restrictions and may do so without prior notice." — [Claude Code legal and compliance](https://code.claude.com/docs/en/legal-and-compliance) (fetched 2026-10-24)
- The same page says a customer may not "pay for, resell, or intermediate Claude usage on their end users' behalf," and that each end user must authenticate with their own API key or subscription credentials. — [Claude Code legal and compliance](https://code.claude.com/docs/en/legal-and-compliance)
- Consumer Terms (effective Oct 8, 2025; no last-updated date shown on the fetched page) bar access "through automated or non-human means, whether through a bot, script, or otherwise," except "when you are accessing our Services via an Anthropic API Key or where we otherwise explicitly permit it." The Consumer Terms do not mention OAuth. — [Anthropic Consumer Terms](https://www.anthropic.com/legal/consumer-terms) (fetched 2026-10-24)
- Enforcement timeline (secondary press reports, consistent with each other but not confirmed against Anthropic's own announcements):
  - 2026-01-09: server-side blocking of subscription OAuth tokens outside the official Claude Code client; developers reported 403 errors. — [Cybersecurity News](https://cybersecuritynews.com/claude-ends-openclaw-subscriptions/); [Sovereign Magazine](https://www.sovereignmagazine.com/article/anthropic-blocks-openclaw-claude-subscriptions)
  - 2026-02-19: documentation update that Free/Pro/Max OAuth tokens are for Claude Code and Claude.ai only, and that use in other tools including the Agent SDK is a violation. — [WinBuzzer](https://winbuzzer.com/2026/02/19/anthropic-bans-claude-subscription-oauth-in-third-party-apps-xcxwbn/); [Gigazine](https://gigazine.net/gsc_news/en/20260220-anthropic-third-party-block) (secondary)
  - 2026-04-04 (per The Register): third-party tools stopped drawing on subscription limits and began using extra usage. An Anthropic spokesperson said using subscriptions with third-party tools "isn't permitted under our Terms of Service." — [The Register, 2026-04-06](https://theregister.com/2026/04/06/anthropic_closes_door_on_subscription) (secondary, quoting spokesperson)
- Google: a secondary search summary (sources not individually fetched) says Google took similar action against Gemini CLI OAuth piggybacking in February 2026. No source was fetched to confirm this. Treat as unverified. (No citation available.)
- Anthropic says consumer subscription use is for "ordinary, individual usage." Advertised Pro and Max limits "assume ordinary, individual usage of Claude Code and the Agent SDK." — [Claude Code legal and compliance](https://code.claude.com/docs/en/legal-and-compliance)
- API keys are the supported path. The legal page says customers may configure their own API keys in a secrets manager for their own authorized users. — [Claude Code legal and compliance](https://code.claude.com/docs/en/legal-and-compliance)

### Inferences
- An individual running Claude Code on their own Pro subscription is inside the terms. Bazaar agents running as a shared, multi-user platform on that subscription, routed through OmniRoute, are not. Bazaar would be a third-party developer routing subscription credentials for users, which the page prohibits.
- The `auto/subscription` tag in OmniRoute is a risk marker. Do not enable it for any Bazaar key.
- Open-sourcing Bazaar makes the risk larger. Public documentation of a subscription-routing setup could be read as inducing a violation.
- Enforcement may be technical (403s) rather than contractual alone. A failed lane could go silent without notice.

### Gaps
- Not verified: Google's current terms for Gemini CLI and Antigravity OAuth, and the February 2026 enforcement, from a Google primary source.
- Not verified: whether the Consumer Terms were updated after Oct 8, 2025. The fetched page shows no last-updated date.
- No legal opinion obtained. This is research, not legal advice. Have counsel review before publishing Bazaar with any subscription-routing configuration.

---

## 4. Google Antigravity vs Antigravity CLI vs Gemini CLI: quota pooling

### Takeaway
Google's Gemini CLI quota page (last updated 2026-06-18) gives per-day limits by account type and says nothing about sharing across accounts or Google One families. It also states that Gemini CLI was replaced by Antigravity CLI for unpaid-tier and Google One users on 2026-06-18. Whether Antigravity and Gemini CLI draw from one pool is not documented. Family-pool sharing is only forum speculation.

### Cited Findings
- Daily limits per Gemini CLI quota page (last updated Jun 18, 2026): personal Google account (Code Assist Individual) 1,000/day; Google AI Pro 1,500/day; Google AI Ultra 2,000/day; Code Assist Standard 1,500/day; Code Assist Enterprise 2,000/day; free Gemini API key 250/day on Flash only. Limits are "per user per minute," with no per-minute number stated. — [Gemini CLI quota and pricing](https://geminicli.com/docs/resources/quota-and-pricing/) (fetched 2026-10-24)
- The same page states that for unpaid-tier and Google One users, Gemini CLI was replaced by Antigravity CLI on June 18, 2026. — [Gemini CLI quota and pricing](https://geminicli.com/docs/resources/quota-and-pricing/)
- A third-party guide gives 60 requests/min and 1,000/day for a personal account on Gemini 3 models, with the higher daily tiers above. The 60 RPM figure is not on Google's page. — [Amplifying AI: Gemini CLI / Antigravity CLI](https://amplifying.ai/coding-agents/gemini-cli) (secondary)
- Forum reports say Antigravity Claude and Gemini quotas appear shared within a Google One AI Pro family group, and that exhausting one account exhausted another. A reply claiming Family groups share one pool is not from Google. — [Google AI Developers forum: family group sharing](https://discuss.ai.google.dev/t/are-antigravity-s-gemini-and-claude-quotas-shared-within-a-google-one-ai-pro-family-group/143974); [forum: quota shared across two accounts](https://discuss.ai.google.dev/t/quota-shared-exhausted-across-two-separate-google-accounts-pro-plan-when-switching-accounts-in-antigravity/178592) (forum, unverified)
- Forum reports of 429 RESOURCE_EXHAUSTED with about 1% quota used. — [Google AI Developers forum](https://discuss.ai.google.dev/t/google-ai-ultra-for-business-429-resource-exhausted-with-only-1-quota-used-in-gemini-cli-antigravity/143379) (forum)

### Inferences
- The official page treats quotas as per-user daily limits. A shared account-level pool across Antigravity and Gemini CLI is plausible but not documented. Treat it as unknown and design the Bazaar lane to read the quota from the response, not from a table.
- Multiple Google accounts to pool quota would fall under the same kind of terms risk as section 3. Not verified.

### Gaps
- Not verified: whether Antigravity CLI and Gemini CLI share one quota pool. No primary source.
- Not verified: family-group sharing. Only forum claims found.
- Not verified: Google's terms for third-party use of Gemini CLI or Antigravity OAuth in a gateway.

---

## 5. Current model names (late 2026): Claude, DeepSeek, Gemini

### Takeaway
Anthropic's model lineup is not verified from a primary source. Press coverage names "Claude Fable 5" as a public Mythos-class model announced 2026-06-09, but reports conflict on its availability (one report says it was suspended on 2026-06-12 by a US export-control directive). Confirm Fable and the rest against platform.claude.com/docs/en/about-claude/models before whitelisting. DeepSeek API IDs are `deepseek-v4-flash` and `deepseek-v4-pro`, per third-party sources. Gemini model names were not verified.

### Cited Findings
- "Claude Fable 5" announced June 9, 2026 as Anthropic's public Mythos-class model. Multiple outlets report the launch. Pricing reported as $10 input / $50 output per million tokens (one source). — [Jang](https://jang.com.pk/en/67306-anthropic-launches-claude-fable-5-the-new-benchmark-for-ai-performance-news); [iPhoneSoft](https://iphonesoft.fr/2026/06/10/claude-fable-5-anthropic-premier-modele-mythos-grand-public); [Hashnode](https://hashnode.com/posts/claude-fable/6a292883e3b263740bae7e9b) (all secondary)
- Conflict: one source says Fable 5 and Mythos 5 were suspended 2026-06-12 by a US export-control directive. Another article titled "When is Fable coming back?" implies an access gap. — [AI Tools Review / June 2026 article](https://aitoolsreview.co.uk/insights/next-claude-model); [atcyrus](https://www.atcyrus.com/stories/when-is-fable-coming-back) (secondary, conflicting)
- Sources disagree on the rest of the lineup: Opus 4.8 (28 May), Opus 4.7 (16 April, flagship in one May guide), Sonnet 4.6 (default for Free/Pro in one May guide), Opus 5, Sonnet 5, Haiku 4.5. Sonnet 5 and Opus 5 are listed as released in one July timeline and as rumour in one June article. — [suprmind.ai](https://suprmind.ai/hub/claude/); [lmmarketcap](https://lmmarketcap.com/providers/anthropic); [pasqualepillitteri guide](https://pasqualepillitteri.it/en/news/13165/claude-anthropic-complete-guide-models-pricing) (all aggregators, conflicting)
- DeepSeek API model IDs `deepseek-v4-flash` and `deepseek-v4-pro`. Both have a 1M-token context window and up to 384K output. DeepSeek released V4 as an open-source preview on 2026-04-24, with MIT-licensed weights (per aggregators). `deepseek-chat` and `deepseek-reasoner` were scheduled for retirement on 2026-07-24; sources disagree on whether they still work. — [CometAPI](https://www.cometapi.com/how-to-use-deepseek-v4-api/); [codepick.dev](https://codepick.dev/en/guides/deepseek-v4-api-platforms/); [chat-deep.ai](https://chat-deep.ai/models/deepseek-v4/) (all third-party; check api-docs.deepseek.com)
- DeepSeek V4 Pro on NIM is `deepseek-v4-pro-0813`, per build.nvidia.com (see section 2). — [build.nvidia.com](https://build.nvidia.com/deepseek-ai/deepseek-v4-pro)
- The design doc says "V4 Flash, V4 Pro, 1M-context variants only." The 1M context claim is consistent with the aggregators for the API but inconsistent with the 262K figure on the NIM page. The design doc does not name a "V3" model, so no V3/V4 naming conflict affects the plan.

### Inferences
- The whitelist should be stored as provider model IDs checked against each provider's live model list, not as marketing names. "Fable" in particular should not be whitelisted until its availability is confirmed.

### Gaps
- Not fetched: platform.claude.com model catalog. This is the authoritative list and should be checked directly.
- Not verified: current Gemini model names (Gemini 3 is mentioned in one third-party guide only).
- Not verified: DeepSeek official docs. The API IDs come from aggregators.

---

## 6. RTK and Caveman token compression: what they are, evidence, risks

### Takeaway
Both are real projects, and both report savings that are self-measured and vary a lot. RTK ("Rust Token Killer") filters Bash command output before Claude Code sees it. Caveman changes output style, compresses tool output before the model reads it, and adds an auto-routing feature. Neither has independent evidence of safe use on source code. Caveman's own page warns that its read-less mode changes what the model sees. Treat both as optional and verify on real diffs before use.

### Cited Findings
- RTK: github.com/rtk-ai/rtk, a Rust CLI proxy. README claims 60 to 90% token reduction on common dev commands. `rtk init -g` installs a hook that rewrites Bash commands. The hook covers Bash only, not Read, Grep, or Glob. — [rtk-ai/rtk](https://github.com/rtk-ai/rtk); [search summary](https://dev.co/ai/frameworks/rtk) (secondary)
- RTK practitioner report: about 20 to 50% savings in practice, and aggressive compression can hide troubleshooting context. — [LinkedIn post](https://fi.linkedin.com/in/yunwei-cui) (secondary, anecdotal)
- RTK name collision: reachingforthejack/rtk is a different project. Several forks and mirrors exist. License metadata conflicts between sources (MIT on the mirrors, Apache-2.0 on one aggregator). Verify on the official repo. — [search results](https://github.com/Lyther/rtk) (forks)
- Caveman: github.com/JuliusBrussee/caveman, Apache-2.0 per the page. Its page says it compresses tool output before the model reads it ("read-less" mode), and offers a terse output "voice." The About blurb says "cuts 65% of tokens," but the page's own measurements are lower. — [JuliusBrussee/caveman](https://github.com/JuliusBrussee/caveman) (fetched 2026-10-24)
- Caveman's own measurements: 33.2% fewer input tokens across 54 Claude Code runs (session run from August 2026 on an older engine); per-file compression 82.4% on test files; output example 63 to 20 tokens; Auto routing 54% cheaper across 85 tasks, labelled "interim," with a 0.5-point score gap "too small to tell apart at this size (±7)." — [JuliusBrussee/caveman](https://github.com/JuliusBrussee/caveman)
- Caveman's own caveat: "'Be concise' is the real baseline," and "if you pay per request (GitHub Copilot), shorter answers save nothing." — [JuliusBrussee/caveman](https://github.com/JuliusBrussee/caveman)
- Third-party figures cited on the Caveman page: Adobe Research reports 1.4 to 2.4x cost reduction per model; JetBrains reports no measurable quality loss (p = 0.82) with 8.5% fewer output tokens; Elastic reports 63.6% fewer response tokens. These are cited by the project and not independently fetched. — [JuliusBrussee/caveman](https://github.com/JuliusBrussee/caveman) (secondary)
- Source-code risk: Caveman's voice mode leaves code, commands, paths, and error messages untouched, but "Articles can go." Read-less mode compresses logs, JSON, CSV, and test output, so the model works from the compressed version (originals kept on disk). HTML got "9.9% worse," and no HTML compressor exists yet. Caveman says it does not rewrite prompts because "cavemanning your prompt makes answers worse." — [JuliusBrussee/caveman](https://github.com/JuliusBrussee/caveman)
- Privacy: Caveman telemetry is on by default. Auto sends recent prompts and context to Caveman Cloud once you sign in and enable it. Disable with `caveman telemetry off` and `caveman routing off`. — [JuliusBrussee/caveman](https://github.com/JuliusBrussee/caveman)
- OmniRoute claims RTK and Caveman stacked compression "saves 15-95% tokens" and cites "~89% avg" and a 12-engine compression system including LLMLingua-2. These are the gateway's own claims. — [OmniRoute GitHub README](https://github.com/diegosouzapw/OmniRoute)

### Inferences
- Read-less compression of test output and logs changes what the agent sees. For a debugging or code-review agent, that can hide the failure detail. Keep it off for review and debugging lanes, or test it on a fixed set of real failures first.
- Caveman's Auto routing sends prompts to a third-party cloud by default unless telemetry and routing are turned off. This conflicts with a privacy-sensitive company install. Disable it in any Bazaar deployment.
- Because savings are self-reported and vary (RTK 20 to 50% in practice, 60 to 90% advertised), budget conservatively and measure on Bazaar's own runs.

### Gaps
- No independent benchmark of RTK or Caveman on source code. The measurements are the projects' own or single-source.
- Not verified: the 65% headline figure against the page's own numbers beyond what is quoted here.
- Not verified: the OmniRoute "12-engine" and LLMLingua-2 claims.

---

## Recommendations for the Bazaar design (from these findings)

1. Claude lane: use Anthropic API keys only. Do not route Claude Pro/Max subscription credentials through OmniRoute or any Bazaar component.
2. OmniRoute keys: one key per agent, pinned with `forcedModel` or an allowed-model list. Do not give any agent a key with `auto/subscription`, `auto/*`, or any provider marked `tos: avoid`.
3. DeepSeek lane: treat NIM's free tier as rate-limited (about 40 RPM per account, unverified daily cap). Add 429 backoff and a paid fallback. Do not describe supply as "practically infinite."
4. Fable: do not whitelist until availability is confirmed on the Anthropic model catalog.
5. Compression: leave RTK and Caveman off for review and debugging lanes. Disable Caveman telemetry and Auto routing. Measure savings on real Bazaar runs before enabling.
6. Get counsel review before publishing the open-source platform with any gateway config that touches subscription or OAuth credentials.
</content>
