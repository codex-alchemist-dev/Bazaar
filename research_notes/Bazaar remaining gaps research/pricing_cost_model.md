# Bazaar Agent-Team Pricing and Monthly Cost Model (as of 2026-10-24)

Scope: current per-million-token (MTok) API prices for the lanes Bazaar plans to use, prompt-caching and batch mechanics, Anthropic cost features and rate-limit tiers, a three-scenario monthly cost model for a six-agent team, the levers that matter, and a coarse note on hosting costs. All prices are USD unless a page names another currency. Nothing was installed or run. Figures from WebFetch are a model-summarised rendering of the page, so re-check any figure against the raw page before it goes into a public document.

**Evidence labels.** "Verified (primary)" means the page was fetched in this pass; WebFetch text is a converted rendering, so treat quotes as close paraphrase. "Verified (primary, summarised)" means a summarising model extracted the numbers; re-check them. "Carried" means the fact comes from an existing note in `/home/user/Bazaar/research_notes/` that was read in this pass. "Secondary" and "Unverified" are as defined in the source catalogue. All pricing pages are undated apart from the dates each page states.

**Bottom line.**
1. Anthropic's current table (verified primary) puts Haiku 5.5 at $0.10 input and $0.50 output per MTok for prompts up to 100K tokens, and Sonnet 5.5 at $2 and $10. Opus 5.5 is $4 and $20. Fable 5.1 is $10 and $50. At the same (typical) token profile, Haiku's cost per call is roughly one-nineteenth of Sonnet's and one-ninety-second of Fable's.
2. Under the stated profile, the team costs about $26 per month (light), $177 (typical) and $1,035 (heavy) at the base prices. The upper bound, with DeepSeek peak pricing and the announced Gemini price step, is about $43, $298 and $1,749. No lower bound was modelled.
3. The single largest driver is the Sonnet escalation lane, not the Haiku lane. At 5% of work items it is about 24% of cost in the typical and heavy scenarios. Moving it to Haiku saves about $41 per month (typical) and $235 (heavy), before any quality cost.
4. Caching is the second lever. Modelled at a 70% hit rate, it roughly halves per-call cost on every paid lane (about 48% to 57% saving). Keeping any single prompt under 100K tokens matters because Haiku 5.5's price for prompts over 100K is five times higher.
5. Free tiers do not carry the junior lane at typical volume. OpenRouter's free models cap at 50 requests per day below 10 purchased credits, and Cerebras's trial caps at 1M tokens per day. Free tiers save little money at these volumes because Haiku is already cheap, so they are a capacity question more than a cost one.

---

## Key Question 1: Current per-token prices (Anthropic, Google Gemini, DeepSeek, one aggregator)

### Takeaway
Anthropic's table is the authoritative source for the Claude lane and confirms the owner's model names (Haiku 5.5, Sonnet 5.5, Opus 5.5, Fable 5.1). The Gemini and DeepSeek pricing pages were also read directly. The aggregator per-token price could not be obtained, so it is marked unverified.

### Cited Findings

**Anthropic (Claude API, first-party, USD per MTok)**
- Haiku 5.5, prompts up to 100,000 tokens: base input $0.10; 5-minute cache write $0.125; 1-hour cache write $0.20; cache hit $0.01; output $0.50. Haiku 5.5, prompts over 100,000 tokens: input $0.50; 5-minute write $0.625; 1-hour write $1; cache hit $0.05; output $2.50. Verified (primary): [platform.claude.com/docs/en/about-claude/pricing](https://platform.claude.com/docs/en/about-claude/pricing) (undated page).
- Sonnet 5.5: input $2; 5m write $2.50; 1h write $4; cache hit $0.10; output $10. Verified (primary): same page. The footnote on Sonnet 5 says its introductory $2/$10 price "is now the standard price" and the planned September 2026 increase "will not occur" (verified primary, same page).
- Opus 5.5: input $4; 5m write $5; 1h write $8; cache hit $0.20; output $20. Verified (primary): same page.
- Fable 5.1: input $10; 5m write $12.50; 1h write $20; cache hit $0.25; output $50. Fable 5 (non-.1): cache hit $1, otherwise identical. Verified (primary): same page. The page lists Mythos 5.x as "limited availability" (verified primary) but does not mark Fable that way. The notes' secondary report that Fable was suspended on 2026-06-12 (carried, `ai_gateway_models.md` section 5) does not appear in the primary pricing or models pages I read. Account availability of Fable is therefore unverified.
- Cache-hit multipliers: 0.1x of base for most models; 0.05x for Opus 5.5 and Sonnet 5.5; 0.025x for Fable 5.1 and Mythos 5.1 (footnotes, verified primary, same page).
- Context window 1M tokens and max output 128K for Fable 5.1, Opus 5.5, Sonnet 5.5 and Haiku 5.5 (verified primary, [platform.claude.com/docs/en/models/overview](https://platform.claude.com/docs/en/models/overview), undated). The models overview also gives Haiku 5.5 as "From $0.10 / input MTok, From $0.50 / output MTok" and says Batch requests are 50% off (verified primary, same page).
- Tokenizer note: "Claude 4.7 and later models ... use a newer tokenizer ... This tokenizer produces approximately 30% more tokens for the same text" (verified primary, pricing page). Token estimates for Claude lanes should be treated as possibly 30% higher than for older-tokenizer models.
- Data-residency and cloud-partner premiums: "Regional and multi-region endpoint" premiums of 10% apply on Bedrock and Google Cloud; "inference_geo: us" applies a 1.1x multiplier for Claude 4.6 and later (verified primary, pricing page). Bazaar uses the first-party global default, so neither applies.

**Google Gemini API (USD per MTok, standard tier)**
- Gemini 3.8 Flash: free tier (input, output and cache all "Free"); paid input $0.75 then $1.50; paid output $3.75 then $7.50; cache read $0.075 then $0.15; cache storage $0.50 then $1.00 per million tokens per hour. The page says "Where the page lists two values, the first applies through the listed date and the second starts then." The step date was not captured in the extraction and is a gap. Verified (primary, summarised): [ai.google.dev/gemini-api/docs/pricing](https://ai.google.dev/gemini-api/docs/pricing) (page states "Last updated 2026-10-22 UTC").
- Gemini 3.8 Flash and 3.6 Flash: the free tier shows "Used to improve our products" as Yes; the paid tier shows No (verified primary, summarised, same page).
- Gemini 3.5 Flash-Lite: input $0.30; output $2.50; cache read $0.03. Verified (primary, summarised, same page).
- Gemini 3.1 Flash-Lite: input $0.25 (text/image/video), $0.50 (audio); output $1.50; cache read $0.025. Verified (primary, summarised, same page).
- Gemini 2.5 Flash-Lite: input $0.10 (text/image/video); output $0.40; cache read $0.01. Verified (primary, summarised, same page).
- Gemini 3.1 Pro Preview: input $2 (up to 200K) and $4 (over 200K); output $12 and $18. Verified (primary, summarised, same page). Only Gemini 3.1 Pro Preview and Gemini 2.5 Pro have a >200K surcharge (same page).
- Gemini 4 Argon: input $2 rising to $4 on Dec 1, 2026; output $10 rising to $20; no free tier. Verified (primary, summarised, same page).
- Batch: the page describes a "50% cost reduction" and the table shows Batch input at half the standard price (verified primary, summarised, same page). Example: Gemini 3.8 Flash batch input $0.375, output $1.875.
- Free-tier RPD and RPM figures are not on the rate-limit page: "depend on a variety of factors ... can be viewed in Google AI Studio" (verified primary, summarised: [ai.google.dev/gemini-api/docs/rate-limits](https://ai.google.dev/gemini-api/docs/rate-limits), carried from the source catalogue). The Gemini CLI page's "free Gemini API key 250/day on Flash only" is a CLI figure, not an API quota (carried, `source_catalogue.md` section 1).

**DeepSeek API (USD per MTok; page uses "$" and names no currency code)**
- deepseek-v4-pro, off-peak: input cache hit $0.022; input cache miss $0.66; output $1.98. Peak: $0.044; $1.32; $3.96. Verified (primary, summarised): [api-docs.deepseek.com/quick_start/pricing/](https://api-docs.deepseek.com/quick_start/pricing/) (trailing slash needed; the URL without it returned no table).
- deepseek-flash (served by DeepSeek-V4.1-Flash-1017): off-peak cache hit $0.003; miss $0.15; output $0.60. Peak: $0.006; $0.30; $1.20. Verified (primary, summarised, same page).
- The legacy name deepseek-v4-flash "is retired. Its requests are served by DeepSeek-V4.1-Flash-1017 and billed at the Flash price" (verified primary, summarised, same page). This corrects the feasibility note's claim that `deepseek-v4-flash` is the live ID (carried, `ai_gateway_models.md` section 5, third-party source).
- Off-peak rates are half of peak; peak hours are described as "01:00–04:00 and 06:00–10:00 UTC, Monday through Friday, excluding Chinese public holidays" (verified primary, summarised, same page). Check the UTC claim against the raw page. No end date is given for the discount.
- Context length 1M; maximum output 384K (verified primary, summarised, same page).
- Search and fetch tools: $0.75 per 1,000 successful requests; free through DeepSeek Harness, Claude Code or Codex for now; per-request billing starts 00:00 Beijing time on 2026-11-01 (verified primary, summarised, same page).

**One cheap aggregator (partial)**
- OpenRouter `:free` models: below 10 lifetime credits purchased, 20 requests per minute and 50 requests per day; at 10 or more credits purchased, 20 RPM and 1,000 RPD (carried, verified primary, summarised: [openrouter.ai/docs/api/reference/limits](https://openrouter.ai/docs/api/reference/limits), from `source_catalogue.md` section 1).
- OpenRouter per-token prices for DeepSeek V4 Flash: not obtained. The fetch of `openrouter.ai/deepseek/deepseek-v4-flash` returned the API integration guide, not the model page (verified primary, summarised, that page). **Unverified.**
- Cerebras free trial: 5 RPM, 1M TPD and 1M TPH for the listed models (carried, verified primary, summarised: [inference-docs.cerebras.ai/support/rate-limits](https://inference-docs.cerebras.ai/support/rate-limits)). No per-token price for the trial was in the notes.
- Cloudflare Workers AI: 10,000 neurons per day free; $0.011 per 1,000 neurons above that (carried, verified primary, summarised: [developers.cloudflare.com/workers-ai/platform/pricing](https://developers.cloudflare.com/workers-ai/platform/pricing/)). The neurons-to-tokens conversion for each model was not checked. **Unverified.**

### Inferences
- At list price, Haiku 5.5 and Gemini 2.5 Flash-Lite are about the same on input ($0.10 each), and Haiku's output is $0.50 against $0.40. For a small gatekeeper call, the two are close enough that the choice is a quality and operations decision, not a cost one.
- DeepSeek V4 Pro off-peak input-miss price ($0.66) is about 6.6 times Haiku 5.5's ($0.10), and its cache-hit price ($0.022) is about twice Haiku's ($0.01). Pro is not a cheap-input lane. DeepSeek Flash has the lowest cache-hit price of the lanes modelled ($0.003 off-peak), but its miss price ($0.15) is above Haiku 5.5's.
- Gemini 3.8 Flash costs more than DeepSeek Pro off-peak per call at the typical profile, because its output price is $3.75 against $1.98 and its miss rate is about $0.75 against $0.66.

### Gaps
- Dates for the Gemini 3.8 Flash price step ($0.75 to $1.50) and other two-value Gemini cells were not captured.
- DeepSeek's currency is not named on the page.
- DeepSeek's discount end date and the peak-hour time zone should be re-checked on the raw page.
- Aggregator per-token prices (OpenRouter, Cerebras, Cloudflare conversion): not obtained.
- Anthropic's free-tier availability and Fable account access: not verified.

---

## Key Question 2: Anthropic features that cut cost in agent loops; rate-limit tiers

### Takeaway
Prompt caching is the biggest cost lever available on the Claude lane (reads at 0.1x or lower, writes at 1.25x for 5 minutes or 2x for 1 hour). Batch gives 50% off. Context editing and server-side compaction reduce context size but invalidate cache entries when they clear content. Thinking tokens are billed as output. Tiers are set by the console and their qualifying spend is not on the fetched page.

### Cited Findings

**Prompt caching mechanics**
- Minimum cacheable prompt: 512 tokens for Haiku 5.5, Sonnet 5.5, Opus 5.5, Fable 5 and Fable 5.1; 1,024 for Sonnet 5 and Opus 4.8; 2,048 for Opus 4.7. "Shorter prompts cannot be cached, even if marked with `cache_control`." Verified (primary, summarised): [platform.claude.com/docs/en/build-with-claude/prompt-caching](https://platform.claude.com/docs/en/build-with-claude/prompt-caching).
- TTLs: default 5 minutes, refreshed for no extra cost each time the cache is read; a 1-hour option "at additional cost." Writes: 5-minute at 1.25x base; 1-hour at 2x base. Verified (primary, summarised, same page).
- Prefix order is `tools`, `system`, then `messages`. Hits require "100 percent identical prompt segments." Up to 4 explicit breakpoints; automatic caching uses one slot. Lookback: "The system checks at most 20 positions for each breakpoint." Verified (primary, summarised, same page).
- Cache-write rule for tool loops: each tool turn reads the earlier prefix and writes only the new block if a breakpoint is placed at the end of the new content. This is the basis of the cost formula in Key Question 3.

**Batch API**
- "reducing costs by 50%" and "most batches finishing in less than 1 hour" (verified primary, [platform.claude.com/docs/en/build-with-claude/batch-processing](https://platform.claude.com/docs/en/build-with-claude/batch-processing)). Models overview repeats "Batch API requests are 50% off" (verified primary).
- Whether batch and cache-read discounts stack on Anthropic was not confirmed on a primary page. **Unverified.** The model below does not assume stacking.
- Batch limits: 100,000 requests per batch; Start tier batch RPM 1,000 (verified primary, [rate-limits page](https://platform.claude.com/docs/en/api/rate-limits)).

**Context editing and compaction**
- Context editing (beta header `context-management-2025-06-27`) offers `clear_tool_uses_20250919` (tool-result clearing) and `clear_thinking_20251015` (thinking-block clearing). Verified (primary, summarised): [platform.claude.com/docs/en/build-with-claude/context-editing](https://platform.claude.com/docs/en/build-with-claude/context-editing) (undated).
- "You'll incur cache write costs each time content is cleared, but subsequent requests can reuse the newly cached prefix." Clearing invalidates cached prefixes (verified primary, same page).
- Thinking-block clearing keeps the cache intact when thinking blocks are kept: "When thinking blocks are kept in context (not cleared), the prompt cache is preserved" (verified primary, same page).
- "Anthropic recommends server-side compaction over SDK compaction." Server-side compaction uses the edit `compact_20260112`; the SDK default trigger is 100,000 tokens (verified primary, summarised, same page).

**Extended thinking and tool-use overhead**
- Thinking billing: "usage.output_tokens_details.thinking_tokens" reports "how many of the billed output tokens were internal reasoning" (verified primary, summarised: [platform.claude.com/docs/en/build-with-claude/extended-thinking](https://platform.claude.com/docs/en/build-with-claude/extended-thinking)). Thinking is therefore billed at the output rate.
- `thinking.type: "enabled"` (budget_tokens) returns a 400 error on Claude 4.7 and later, including Haiku 5.5 (verified primary, same page). Haiku 5.5 uses adaptive thinking with a default effort of "medium" (verified primary, [models overview](https://platform.claude.com/docs/en/models/overview)). Thinking volume is therefore set by effort, not a budget.
- Tool-use system-prompt overhead: 286 tokens for Haiku 5.5 with `auto` or `none` tool choice; 406 for `any` or `tool`. Also Opus 5.5 286 and Sonnet 5.5 286 (verified primary, summarised: [platform.claude.com/docs/en/agents-and-tools/tool-use/overview](https://platform.claude.com/docs/en/agents-and-tools/tool-use/overview)). Tool definitions, tool_use blocks and tool_result blocks are also input tokens (same page).
- Server tools add charges: web search and code execution have per-use charges; their rates were not read. **Unverified.**

**Rate-limit tiers (Messages API, per model class)**
- Spend caps (monthly): Start $500; Build $1,000; Scale $200,000; Custom has no cap (verified primary, [platform.claude.com/docs/en/api/rate-limits](https://platform.claude.com/docs/en/api/rate-limits)).
- Haiku 5.5, per tier (RPM / ITPM / OTPM): Start 1,000 / 2,000,000 / 400,000; Build 5,000 / 5,000,000 / 1,000,000; Scale 10,000 / 10,000,000 / 2,000,000 (verified primary, same page).
- Sonnet 5.5 and Opus 5.5 have the same limits as Haiku 5.5 in each tier. Fable 5.x shares one combined bucket: Start 1,000 / 500,000 / 100,000; Build 2,000 / 1,500,000 / 300,000; Scale 4,000 / 4,000,000 / 800,000 (verified primary, same page). The Fable output limit is therefore much tighter than the others at Start tier.
- Cache-aware ITPM: "For most Claude models, only uncached input tokens count toward your ITPM rate limits." Haiku 3.5 is the exception (verified primary, same page).
- Spend-cap 429: "enforced_spend_limit_reached"; no `retry-after` header, and retries fail until access resumes (verified primary, same page). The agent loop must treat this as a hard stop, not a retry.
- Tier qualification: the page says "Organizations are placed on a tier automatically based on usage history and account standing" and new organisations start in an "Evaluation" tier (verified primary, same page). **The spend thresholds that unlock each tier were not on the fetched page.** The console's "Request tier increase" flow is the stated route (same page). Unverified.
- Gemini tier thresholds (carried, verified primary, summarised: [ai.google.dev/gemini-api/docs/rate-limits](https://ai.google.dev/gemini-api/docs/rate-limits)): Tier 1 requires an active billing account (billing cap $250); Tier 2 requires $100 paid plus 3 days; Tier 3 requires $1,000 paid plus 30 days.

### Inferences
- A 512-token minimum means a shared system prompt and tool block of at least 512 tokens will cache. Bazaar's agent system prompts should be placed in the cached prefix, with per-turn content after the last breakpoint.
- Cache reads do not count toward ITPM for Haiku, so caching also raises usable throughput, which matters for bursty group-chat traffic.
- Haiku 5.5's 100K price step is the most important long-context fact for this plan (see Key Question 3, sensitivity).

### Gaps
- Qualifying spend for Start, Build and Scale tiers: not on the fetched rate-limit page.
- Batch and cache-read stacking on Anthropic: not confirmed.
- Server-tool (web search, code execution) prices: not read.

---

## Key Question 3: Three-scenario monthly model for a six-agent team

### Takeaway
At the stated profile, a typical month costs about $177 at base prices, with an upper bound of about $298 when Gemini moves to its later price and DeepSeek runs at peak (no lower bound was modelled). The Claude lanes (Haiku 5.5 plus the Sonnet escalation) are about $54 per month in typical use and about $316 in heavy use, which fits under Anthropic's $500 Start-tier spend cap but leaves little room for Opus or Fable. All figures are model outputs under stated assumptions, not measurements.

### Model formula

**Per-call cost (USD)** for one model call in a tool loop:

Cost per call = [ h × C × r + (1 − h) × C × p + D × k × p + (O + T) × q ] / 1,000,000

where:
- `C` = cached context already in the prompt before this call (tokens)
- `h` = cache hit share of `C` (0 to 1)
- `r` = cache read price per MTok (Anthropic: 0.1x base for Haiku, 0.05x for Sonnet and Opus, 0.025x for Fable 5.1)
- `p` = base input price per MTok, also used for cache misses
- `D` = new tokens added this turn (tool results, new messages) and written to cache
- `k` = write multiplier on `D`: 1.25 for Anthropic 5-minute writes; 1.0 for Gemini and DeepSeek, which list no separate write price in the fetched pages
- `O` = visible output tokens per call
- `T` = thinking tokens per call, billed as output
- `q` = output price per MTok

**Monthly cost per lane (USD)** = calls per day × 30 × cost per call.
**Calls per day per lane** = work items per day × share of lane × calls per work item.
**Skimmer Bouncer cost per month** = inbound messages per day × 30 × cost per gatekeeper call.

Known simplification: misses are billed at base input price and do not pay the 1.25x write premium. Applying the premium to misses would raise Anthropic lane cost by about 11% to 12% (estimated: typical Haiku 900 of 2,010 per call; typical Sonnet 18,000 of 38,100). This understates Anthropic cost.

### Scenario assumptions (all are assumptions, not measurements)

| Parameter | Light | Typical | Heavy |
|---|---|---|---|
| Work items per day (team) | 40 | 150 | 400 |
| Model calls per work item (tool turns) | 3 | 5 | 8 |
| Inbound GC messages per work item (Skimmer input) | 3 | 3 | 3 |
| Cached context C per call (tokens) | 20,000 | 30,000 | 60,000 |
| Cache hit share h | 0.5 | 0.7 | 0.8 |
| New tokens D per call | 2,000 | 2,000 | 2,000 |
| Visible output O per call | 800 | 800 | 800 |
| Thinking T per call (billed output) | 0 | 500 | 1,000 |

Lane shares of work items (same in all scenarios): Claude architect on Haiku 5.5, 25%; Claude escalation on Sonnet 5.5, 5%; Gemini research on Gemini 3.8 Flash, 20%; DeepSeek implementation on V4 Pro, 20%; DeepSeek boilerplate on Flash, 15%; junior agents on free tiers, 15%.

Base prices used: Haiku 5.5 $0.10 / $0.01 / $0.50 (input, cache read, output), with the 5-minute write at $0.125. Sonnet 5.5 $2 / $0.10 / $10, write $2.50. Gemini 3.8 Flash at the current price $0.75 / $0.075 / $3.75. DeepSeek V4 Pro off-peak $0.66 / $0.022 / $1.98. DeepSeek Flash off-peak $0.15 / $0.003 / $0.60. Upper-bound prices: Gemini 3.8 Flash after the step ($1.50 / $0.15 / $7.50); DeepSeek peak (Pro $1.32 / $0.044 / $3.96; Flash $0.30 / $0.006 / $1.20).

Skimmer Bouncer assumption: a single gatekeeper call per inbound message, 1,000 input tokens and 50 output tokens, on Haiku 5.5 without caching: $0.10 x 1,000 + $0.50 x 50 = $125 per million calls, i.e. $0.000125 per call. If the gatekeeper uses a shared 800-token system prompt that caches, the cost is about $0.000053 per call (800 cache reads at $0.01 plus 200 uncached at $0.10 plus 50 output at $0.50, divided by 1M). The main numbers below use the uncached $0.000125.

### Cost per call by lane (USD, computed from the formula)

| Lane (price basis) | Light | Typical | Heavy |
|---|---|---|---|
| Haiku 5.5 (Claude architect) | 0.00175 | 0.00201 | 0.00283 |
| Sonnet 5.5 (escalation) | 0.0340 | 0.0381 | 0.0518 |
| Gemini 3.8 Flash, current price | 0.01275 | 0.0147 | 0.02085 |
| Gemini 3.8 Flash, after step | 0.0255 | 0.0294 | 0.0417 |
| DeepSeek V4 Pro, off-peak | 0.00972 | 0.01030 | 0.01386 |
| DeepSeek V4 Pro, peak | 0.0194 | 0.0206 | 0.0277 |
| DeepSeek Flash, off-peak | 0.00231 | 0.00249 | 0.00332 |
| DeepSeek Flash, peak | 0.00462 | 0.00499 | 0.00665 |
| Opus 5.5 (sensitivity, typical and heavy) | n/a | 0.0762 | 0.1036 |
| Fable 5.1 (sensitivity, typical and heavy) | n/a | 0.1853 | 0.247 |

Worked example (Haiku 5.5, typical): h = 0.7, C = 30,000, D = 2,000, O + T = 1,300.
- Cache reads: 0.7 × 30,000 = 21,000 tokens × $0.01 = $210 per million-token units
- Cache misses: 0.3 × 30,000 = 9,000 tokens × $0.10 = $900
- New-token writes: 2,000 × 1.25 × $0.10 = $250
- Output: 1,300 × $0.50 = $650
- Total = $2,010 per million units = $0.00201 per call

### Monthly cost by lane (USD, base prices: Gemini current, DeepSeek off-peak)

| Lane | Light (calls/mo) | Typical (calls/mo) | Heavy (calls/mo) |
|---|---|---|---|
| Haiku 5.5 architect | 1.58 (900) | 11.31 (5,625) | 67.92 (24,000) |
| Sonnet 5.5 escalation | 6.12 (180) | 42.86 (1,125) | 248.64 (4,800) |
| Gemini 3.8 Flash research | 9.18 (720) | 66.15 (4,500) | 400.32 (19,200) |
| DeepSeek V4 Pro implementation | 7.00 (720) | 46.35 (4,500) | 266.11 (19,200) |
| DeepSeek Flash boilerplate | 1.25 (540) | 8.40 (3,375) | 47.81 (14,400) |
| Junior agents (free tiers) | 0 (540) | 0 (3,375) | 0 (14,400) |
| Skimmer Bouncer (Haiku, uncached) | 0.45 (3,600 calls) | 1.69 (13,500) | 4.50 (36,000) |
| **Total, base** | **25.58** | **176.76** | **1,035.30** |

Upper bound, with Gemini 3.8 Flash after the price step and DeepSeek at peak pricing: Light $42.97; Typical $297.70; Heavy $1,749.30. Gemini after step: $18.36 / $132.30 / $800.64. DeepSeek V4 Pro peak: $13.97 / $92.70 / $531.84. DeepSeek Flash peak: $2.49 / $16.84 / $95.76.

Sensitivity for the escalation lane (typical):
- Opus 5.5 instead of Sonnet for the 5% lane: about $85.73 (vs $42.86).
- Fable 5.1 instead of Sonnet: about $208.46.
- Heavy with Opus 5.5 in place of Sonnet: about $497 for that lane (vs $249); with Fable 5.1: about $1,186.

### Checks against limits and free tiers
- Claude lane total: typical $54.17 (Haiku plus Sonnet); heavy $316.56. Both are under Anthropic's $500 Start-tier cap. Heavy use with Opus or Fable would exceed it.
- Juniors on free tiers, typical: 112.5 calls per day, each assumed to carry about 30,000 input tokens and no caching, so about 3.4 million input tokens per day. The Cerebras trial's 1M TPD cannot carry this. OpenRouter's free models cap at 50 RPD below 10 purchased credits, which also cannot carry typical volume; at 10 or more credits (1,000 RPD) it can. Light (18 per day) fits the 50 RPD cap. Heavy (480 per day) needs the 1,000 RPD tier or several sources.
- If the juniors had been on Haiku 5.5 instead, cost would be about $6.79 per month (typical) and $40.75 (heavy). The free tier saves little money at these volumes.
- Context cliff: a Haiku 5.5 call at 110,000 tokens of context (same profile otherwise) costs about $0.0249, roughly 12 times the typical per-call figure. This assumes the over-100K rate applies to the whole prompt; the page states "for prompts over 100,000 tokens" but does not say whether it applies to all tokens or only the excess. Unverified; treat it as a conservative assumption to test.

### Inferences
- Haiku-first routing with a Sonnet escalation lane of 5% costs about 24% of Claude-lane spend at typical volume, so the escalation policy (not the Haiku lane) decides the Claude bill.
- Heavy-scenario cost is dominated by the Gemini and DeepSeek research and implementation lanes, because they carry much larger call volumes at similar per-call prices.
- The model is most sensitive to the cache hit share h and to the context size C. Both are unmeasured; see Gaps.

### Gaps
- No measured usage. Every number above depends on the assumed call counts, context sizes, cache hit rates and thinking volumes. These should be replaced with the NUC's own logs before any budget decision.
- Cache hit share: a 5-minute TTL is short for agents that wait on group-chat replies. A realistic hit rate may be below 0.5. Not verified.
- Token counts for Claude lanes may be up to 30% higher on the newer tokenizer (verified primary; exact effect on Bazaar's prompts unmeasured).

---

## Key Question 4: Levers ranked, with estimated savings

### Takeaway
The lever list below is ordered by estimated dollar effect at the typical profile. Caching and escalation policy are the two largest. Every saving range is a model output under the stated assumptions, not a measured result.

### Cited Findings and modelled effects (typical profile unless stated)

1. **Escalation policy (Sonnet 5% lane moved to Haiku).** Sonnet lane $42.86 per month; the same calls on Haiku cost $2.26. Saving about $40.60 per month (about 23% of the typical total). Heavy: about $235 saved (about 23%). Quality trade-off not measured. Prices verified primary (pricing page).
2. **Prompt caching (h = 0.7 versus no caching).** No-cache per-call cost is $0.00385 on Haiku (against $0.00201), $0.0770 on Sonnet (against $0.0381), $0.0289 on Gemini current (against $0.0147), $0.0237 on DeepSeek V4 Pro off-peak (against $0.0103). Saving: about 48%, 51%, 49% and 57% per lane. Mechanics verified primary (prompt-caching page). Hit rate unmeasured.
3. **Context reduction through compaction or clearing (C from 30,000 to 15,000).** Haiku per call $0.00146 (-28%); Sonnet $0.0236 (-38%); Gemini $0.0105 (-28%). Model output. Compaction and clearing re-write the cache once, so the realised saving is lower than a linear reduction in C would suggest. Mechanics verified primary (context-editing page).
4. **Batch (50% off) for non-interactive work.** Haiku typical lane $5.66 (against $11.31) if fully batched; the Sonnet lane $21.43 (against $42.86). Verified primary for the 50% rate; stacking with cache reads unverified, so this assumes no cache discount on batch calls.
5. **DeepSeek off-peak scheduling.** Pro off-peak is half of peak: $46.35 against $92.70 typical. Saving about $46 per month. Verified primary (summarised) for the 50% rule; the peak-hour window needs a raw-page check.
6. **Haiku 5.5 kept under 100K tokens per prompt.** Above that line, input and output prices are five times higher. Avoiding the cliff is a hard rule, not a percentage saving.
7. **Gatekeeper (Skimmer Bouncer).** Costs $1.69 per month typical (uncached), about $0.72 cached. The gatekeeper pays for itself if it blocks enough downstream work. Break-even at typical: a blocked inbound message avoids about 5 calls. On the Haiku lane (5 x $0.00201 = $0.01), the gatekeeper ($0.000125) breaks even at about 1.25% of inbound messages blocked. On the Gemini lane (5 x $0.0147 = $0.0735), break-even is about 0.17%. The filter rate itself is unmeasured.
8. **Free tiers.** Saves about $6.79 per month for juniors at typical volume and $40.75 at heavy. Capacity limits matter more than the money (see Key Question 3).
9. **Output discipline (fewer visible output tokens).** Output is the most expensive token type on every lane. Cutting O from 800 to 400 tokens on the Haiku lane saves about $0.00020 per call (about 10%). Reported token-compression savings from RTK and Caveman are self-reported and mostly unverified (carried, `ai_gateway_models.md` section 6). Not included in the model.

### Inferences
- Savings are not additive. Caching and compaction both reduce the same cache-miss term, and batching removes the discount of a cache read if stacking is not allowed.
- The most robust, lowest-risk moves are the escalation policy (lever 1), caching with a 512+ token system prefix (lever 2), and the 100K prompt limit (lever 6). The rest depend on unmeasured hit rates or unverified stacking.

### Gaps
- Batch and cache stacking on Anthropic: not confirmed.
- Actual hit rates for Bazaar's agents: unmeasured. A 5-minute TTL may be too short for group-chat cadence; the 1-hour write (2x base, verified) may be worth testing for the architect lane.
- Quality impact of moving Sonnet escalations to Haiku: not measured.

---

## Key Question 5: Hosting, hardware, electricity, domain and signing costs (coarse)

### Takeaway
No primary sources were read for NUC hardware, electricity tariffs, VPS pricing, domains or code signing. The only usable form here is the arithmetic. Treat these lines as unverified until the owner supplies the figures.

### Cited Findings
- None. No primary source was read for hardware, electricity, VPS (mail or relay), domain registration or code-signing certificate prices. The notes in `/home/user/Bazaar/research_notes/Bazaar gateway sources research/source_catalogue.md` and `/home/user/Bazaar/research_notes/Bazaar stack feasibility research/ai_gateway_models.md` contain no hosting or signing prices.

### Inferences
- Electricity formula: kWh per month = average watts x 730 hours / 1,000; monthly cost = kWh x local tariff. Illustration only, with assumed inputs: a 20 W average draw gives 14.6 kWh per month; at an assumed tariff of $0.25 per kWh, about $3.65 per month. **Both inputs are assumptions, not measurements.**
- At these scenarios, the API spend ($26 to $1,035 per month) is far larger than an estimated NUC electricity cost, so hardware power is unlikely to change the decision. VPS and signing costs are also small against the Claude lane. Confirm with real quotes.

### Gaps
- NUC average power draw and the owner's electricity tariff.
- VPS prices for the mail and relay roles.
- Domain registration and code-signing certificate prices (none sourced).

---

## Sources

Primary (fetched in this pass; undated unless stated):
- Anthropic pricing: https://platform.claude.com/docs/en/about-claude/pricing
- Anthropic models overview: https://platform.claude.com/docs/en/models/overview
- Anthropic prompt caching: https://platform.claude.com/docs/en/build-with-claude/prompt-caching
- Anthropic batch processing: https://platform.claude.com/docs/en/build-with-claude/batch-processing
- Anthropic context editing: https://platform.claude.com/docs/en/build-with-claude/context-editing
- Anthropic extended thinking: https://platform.claude.com/docs/en/build-with-claude/extended-thinking
- Anthropic tool use overview (tool-use overhead): https://platform.claude.com/docs/en/agents-and-tools/tool-use/overview
- Anthropic rate limits: https://platform.claude.com/docs/en/api/rate-limits
- Google Gemini API pricing (last updated 2026-10-22 UTC per page): https://ai.google.dev/gemini-api/docs/pricing
- DeepSeek API pricing (summarised): https://api-docs.deepseek.com/quick_start/pricing/
- OpenRouter model-page attempt (no price returned): https://openrouter.ai/deepseek/deepseek-v4-flash

Carried from existing notes (read in this pass, not re-fetched):
- `/home/user/Bazaar/research_notes/Bazaar gateway sources research/source_catalogue.md` (OpenRouter limits, Cerebras limits, Cloudflare pricing, Gemini rate-limit tiers, Gemini CLI quota)
- `/home/user/Bazaar/research_notes/Bazaar stack feasibility research/ai_gateway_models.md` (model names, DeepSeek IDs, Fable report, token-compression claims)
- Gemini rate limits: https://ai.google.dev/gemini-api/docs/rate-limits
- OpenRouter limits: https://openrouter.ai/docs/api/reference/limits
- Cerebras limits: https://inference-docs.cerebras.ai/support/rate-limits
- Cloudflare Workers AI pricing: https://developers.cloudflare.com/workers-ai/platform/pricing/

Not obtained: aggregator per-token prices; Anthropic tier qualifying-spend thresholds; Anthropic batch-and-cache stacking; server-tool prices; hardware, electricity, VPS, domain and signing prices.

## Unverified or not obtained (summary)
- Fable 5.1 account access (listed on pricing and models pages; the suspension report is unconfirmed).
- Gemini step-up dates for every two-value cell; Gemini free-tier API RPD (not on the rate-limit page).
- DeepSeek currency and peak-hour time zone; DeepSeek discount end date.
- OpenRouter and Cerebras per-token prices; Cloudflare neuron-to-token conversion.
- Anthropic tier spend thresholds; batch-cache stacking; web-search and code-execution tool prices.
- Haiku 5.5 over-100K pricing scope (whole prompt or excess tokens).
- All scenario inputs (call counts, context sizes, hit rates, thinking volume, lane shares). These are assumptions.
- NUC power draw, electricity tariff, VPS, domain and code-signing costs.
