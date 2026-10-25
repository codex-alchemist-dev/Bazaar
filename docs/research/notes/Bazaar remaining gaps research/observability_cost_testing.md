# Observability, Usage/Cost Accounting and Deterministic Agent Testing (Bazaar, as of 2026-10-24)

Scope: observability stack for the Observatory and company dashboard, per-agent and per-provider usage tracking, logs/traces, and deterministic replay testing. Research budget was about 16 tool calls, so several points are marked unverified. Labels: **verified** = primary source read in this session; **secondary** = search snippet or third-party page; **unverified** = not checked, from background knowledge or not found.

Context read first: `/home/user/Bazaar/docs/deep-dives/Bazaar.md` and `/home/user/Bazaar/docs/deep-dives/Bazaar.Server.md`. Relevant DECIDED items: Postgres is the database; the server is OS-agnostic with Windows as the first host; the gateway is Bazaar's own, per-agent model whitelists are enforced by Bazaar.Server; GCs are append-only and are the coordination system; the Claude lane uses Anthropic API keys only; Resource Economy (money budgets and wallet) was REJECTED on 2026-10-24, so cost data should drive display and quota analysis, not budget gates. The Work-Life Simulation replaces rate limits in the design, but real provider 429s still have to be handled.

---

## 1. OpenTelemetry in .NET 10 and the GenAI semantic conventions

### Takeaway
Use OpenTelemetry .NET (traces, metrics, logs over OTLP) from day one, but treat the GenAI semantic conventions as **Development-status and still moving**. Put a Bazaar-owned mapping layer between the gateway and the attribute names, so renames cost one file.

### Cited Findings
- The GenAI semantic-convention pages on opentelemetry.io now say the GenAI conventions "have moved to the OpenTelemetry GenAI semantic conventions repository" and that the page "is no longer maintained" — [OTel GenAI spans page](https://opentelemetry.io/docs/specs/semconv/gen-ai/gen-ai-spans/) (**verified**, fetched; the page holds only the moved notice, so no attribute list was readable there). The same redirect notice appears at [OTel GenAI index](https://opentelemetry.io/docs/specs/semconv/gen-ai/) (**verified**).
- The repo [open-telemetry/semantic-conventions-genai](https://github.com/open-telemetry/semantic-conventions-genai) (**verified**, fetched) has no release listed on its page, its schema URL is "TODO", and the attribute definitions are in the `model` folder, which the fetch did not parse. Status and attribute names were therefore not confirmed from the primary repo.
- Status: a search snippet for the events page labels GenAI conventions "Development" and says instrumentations on v1.36.0 or earlier keep emitting their current version by default, with an opt-in env var for the latest experimental version — [OTel GenAI events page](https://opentelemetry.io/docs/specs/semconv/gen-ai/gen-ai-events) (**secondary**, search snippet only; the opt-in variable name was not read).
- A TrueFoundry article says core semconv v1.42.0 moved GenAI definitions into the separate GenAI repo and that the repo marked them Development with no official release as of 2026-08-21 — [TrueFoundry](https://www.truefoundry.com/blog/opentelemetry-genai-semantic-conventions) (**secondary**).
- A GreptimeDB overview says attribute names may still change, that v1.37 replaced per-message events with aggregated `gen_ai.input.messages` / `gen_ai.output.messages`, and that `gen_ai.system` was replaced by `gen_ai.provider.name` — [Greptime overview, 2026-05-09](https://www.greptime.com/blogs/2026-05-09-opentelemetry-genai-semantic-conventions) (**secondary**). Names below are from these secondary sources and must be checked against the GenAI repo before coding: `gen_ai.provider.name`, `gen_ai.operation.name`, `gen_ai.request.model`, `gen_ai.response.model`, `gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens`.
- Microsoft Learn's Microsoft.Extensions.AI OpenTelemetry page returned 404 when fetched (**unverified** for the `UseOpenTelemetry` builder, activity source name, metric names, and whether prompt/completion content is recorded by default).
- Package names for the OpenTelemetry .NET SDK, the ASP.NET Core / HttpClient / Npgsql instrumentation, and the OTLP exporter (for example `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, and Npgsql's built-in `Npgsql` ActivitySource) were **not verified** in this session. Confirm them on NuGet before use.

### Inferences
- Because the conventions are Development-status and attribute names have changed recently (secondary sources), pin the semconv version explicitly in Bazaar, and use one internal `ModelCallTelemetry` mapping in the gateway rather than sprinkling `gen_ai.*` strings through the code.
- Keep content capture (prompts and completions) **off** in telemetry by default, since the GenAI content attributes are the privacy-sensitive part (see section 5). The transcripts already live, append-only, in GCs.
- Tool-call attribute names could not be verified; do not write tests against them until the GenAI repo is read.

### Gaps
- Exact current attribute names for tool calls, error type, finish reasons, and the opt-in env var.
- Whether Microsoft.Extensions.AI's built-in telemetry emits GenAI-conformant spans at the version Bazaar will pin.

---

## 2. Observability backend for one small host

### Takeaway
For v1 on the Windows NUC, use the **.NET Aspire standalone dashboard** as a developer-only trace/log/metric viewer over OTLP. Keep the Observatory and company dashboard on Bazaar's own Postgres data and the Valkey/Garnet pub-sub, not on a telemetry backend. Defer Grafana/Loki/Tempo, SigNoz, Jaeger and Langfuse until a concrete need appears; their footprints and licences were not verified in this session.

### Cited Findings
- Standalone Aspire dashboard (**verified**, fetched from aspire.dev, the page Microsoft Learn now redirects to): UI on 18888, OTLP/gRPC on 4317, OTLP/HTTP on 4318 (inside the container these map to 18889 and 18890). Browser login token is required by default; `--allow-anonymous` disables it. The telemetry API uses API-key auth by default. Persistence is **off** by default ("telemetry is lost when the dashboard shuts down"); `Run` and `Resume` modes persist. Telemetry is removed when limits are exceeded, and the numeric limits are on the Configuration page (not read). Deployment: Aspire CLI (`aspire dashboard run`, or `npx -y @microsoft/aspire-cli dashboard run`) or container image `mcr.microsoft.com/aspire/dashboard:latest`. The install page lists macOS, Linux and Windows — [aspire.dev standalone dashboard](https://aspire.dev/dashboard/standalone/) (**verified**).
- The Aspire dashboard overview page says the dashboard "persists resources and telemetry" for app runs, which conflicts with the standalone page's default of no persistence. The standalone page was used because it describes the standalone mode — [aspire.dev dashboard overview](https://aspire.dev/dashboard/overview.md) (**verified**, conflicting statement noted).
- Aspire's security page (search snippet) says the OTLP endpoint accepts telemetry without auth unless API-key auth is configured, so it must not be exposed beyond localhost — [aspire.dev security considerations](https://aspire.dev/dashboard/security-considerations.md) (**secondary**, snippet only).
- Langfuse: core described as MIT-licensed in the official FAQ (**secondary**, search snippet, [langfuse.com FAQ](https://langfuse.com/faq/all/self-hosting-langfuse.md)). Hosting-platform templates list 2 vCPU and 4 to 8 GB RAM for app containers, and a ClickHouse container, but the figures conflict between templates and no official minimums were read (**secondary**, [Railway template](https://railway.com/deploy/langfuse-observability.md)). This is a hosting-template claim, not a Langfuse requirement.
- Grafana/Loki/Tempo, SigNoz, Jaeger, Phoenix and OpenLIT: footprints, Windows support and licences were **not researched** in this session. Treat any statement about them as unverified.

### Inferences
- The Aspire dashboard fits the Windows-first host and the .NET stack, and it is the cheapest way to see traces while building. Its in-memory default means it is not an audit store. Use `Resume` mode if history across restarts is wanted for debugging, and never expose its OTLP port beyond localhost.
- The Observatory (live view of every agent's activity) is a product feature. It should be driven by Bazaar's own events (GC activity, agent state rows, pub-sub), since it must work after the telemetry backend is gone and must follow the "events serve GCs, never replace them" rule. OTel is for engineers debugging behaviour, not for the product dashboard.
- Storing usage and quota history in Postgres (partitioned by day if needed, per the Bazaar.Server note on `gc_id`/timestamp partitioning) avoids a second database and fits DECIDED Postgres storage. Metric backends are not needed for v1 cost or quota reporting.
- Langfuse (LLM-trace review UI) is the most likely later addition if prompt-level review is wanted. Its ClickHouse dependency, if confirmed, is a heavier footprint than v1 needs.

### Gaps
- Numeric telemetry limits and the Aspire dashboard's licence (not on the pages read; MIT is unverified).
- Footprint and Windows support for Jaeger, Grafana stack, SigNoz, Phoenix, OpenLIT: not researched.
- Whether Langfuse's self-hosted minimums are official; only hosting-template figures were found.

---

## 3. Usage accounting: tokens, quotas, 429/Retry-After

### Takeaway
Record usage from the provider's own response `usage` object on every call and treat estimation as a fallback only. Model two different exhaustion states: a retryable rate limit (429 with `retry-after`) and a spend cap (429 without `retry-after`, error code `enforced_spend_limit_reached`). Both are inputs to the simulated "on vacation" state.

### Cited Findings
- Anthropic rate limits are measured as RPM, ITPM and OTPM per model class and per tier. Start tier, Haiku 5.5: 1,000 RPM, 2,000,000 ITPM, 400,000 OTPM. Build: 5,000 / 5,000,000 / 1,000,000. Scale: 10,000 / 10,000,000 / 2,000,000. Exceeding a limit gives a 429; when `retry-after` is present, wait that many seconds — [Anthropic rate limits](https://platform.claude.com/docs/en/api/rate-limits) (**verified**, primary, fetched; the old `docs.anthropic.com` URL 301-redirects there).
- Response headers: `retry-after`; `anthropic-ratelimit-requests-{limit,remaining,reset}`; `anthropic-ratelimit-tokens-{limit,remaining,reset}`; `anthropic-ratelimit-input-tokens-{limit,remaining,reset}`; `anthropic-ratelimit-output-tokens-{limit,remaining,reset}`; reset values are RFC 3339 timestamps. Tokens remaining is rounded to the nearest thousand — [Anthropic rate limits](https://platform.claude.com/docs/en/api/rate-limits) (**verified**).
- Spend cap: once a tier's monthly cap is reached, requests return HTTP 429 with `error.type` `rate_limit_error`, `error.details.error_code` `enforced_spend_limit_reached`, and **no** `retry-after` header; access resumes at 00:00 UTC on the first of the next month. A user-set spend limit instead returns HTTP 400 `invalid_request_error` — [Anthropic rate limits](https://platform.claude.com/docs/en/api/rate-limits) (**verified**).
- Cache accounting: `input_tokens` covers only tokens after the last cache breakpoint. Total input = `cache_read_input_tokens` + `cache_creation_input_tokens` + `input_tokens`. For most models only `input_tokens` + `cache_creation_input_tokens` count toward ITPM; cache reads do not, except Haiku 3.5, which counts them — [Anthropic rate limits](https://platform.claude.com/docs/en/api/rate-limits) (**verified**). Store the three input fields separately, so cost and ITPM are both computable.
- OTPM is evaluated as output is produced, and `max_tokens` does not count toward it — [Anthropic rate limits](https://platform.claude.com/docs/en/api/rate-limits) (**verified**).
- The Messages API also has a separate Managed Agents and Files API rate limit (for example, Managed Agents read endpoints 1,200 RPM) — [Anthropic rate limits](https://platform.claude.com/docs/en/api/rate-limits) (**verified**). Not relevant to chat calls, but note it as a second bucket if Bazaar uses those APIs.
- LiteLLM: budgets can be set in config, per team, or per key, and requests fail once a key passes `max_budget`; per-provider budget routing uses Redis to track spend across instances — [LiteLLM budgets](https://docs.litellm.ai/docs/proxy/users) and [provider budget routing](https://docs.litellm.ai/docs/proxy/provider_budget_routing.md) (**secondary**, search snippets; the LiteLLM pages were not fetched).
- OmniRoute and FreeLLMAPI docs were **not read** in this session, so their usage and quota models are unverified. Bazaar.Server.md already notes a NIM "~40 RPM / ~1,000 req/day" figure with no source (**verified**, file read).

### Inferences
- Design: one `UsageRecord` per provider call (agent id, model id, provider id, key id, timestamps, input/cache-create/cache-read/output tokens, latency, HTTP status, error code, retry-after seconds, rate-limit headers when present). Cost is computed from a versioned price table, not stored as truth, so past records can be re-priced.
- Quota state per (provider, key, model class) comes from headers and 429 bodies: a `RateLimited(until)` state from `retry-after` (retry after the wait) versus `SpendCapped(untilMonthStart)` from `enforced_spend_limit_reached` (no automatic retry). The vacation simulation should consume both states. This matches Bazaar's rule that the Work-Life Simulation is the schedule, not the rate limit, but real 429s still have to be handled as data.
- If `retry-after` is missing on a non-spend 429, back off with jitter and log it. The HTTP-date form of `retry-after` (RFC 9110) was not checked here (**unverified**), so parse seconds first and treat other forms as a fallback.
- Estimation (tokenizer-based) is only for pre-flight budgeting or providers that omit `usage`; mark those records `estimated=true` so dashboards never mix them silently with billed numbers.
- The "cost chip" in GC messages (DECIDED UI) can show per-message cost from the price table. That is display only and must not become a money budget, which was REJECTED on 2026-10-24.

### Gaps
- Other providers' header names and 429 shapes (OpenAI, Google, DeepSeek/NIM, OmniRoute, FreeLLMAPI): not verified.
- Current price table values: not researched; must come from each provider's pricing page and be dated.
- Whether usage fields in streaming responses arrive only at the end of the stream (not checked; design should handle partial usage on abort).

---

## 4. Deterministic testing: record/replay, fakes, evals, loop guards

### Takeaway
Test agents with a fake gateway behind the same `Microsoft.Extensions.AI` chat abstraction that the real gateway exposes, replaying recorded cassettes. Gate CI on deterministic assertions (tool-call sequences, GC event order, loop-guard counters, quota transitions). Run LLM-as-judge evaluations as a separate, non-gating, scheduled job, because they are stochastic and judge models are themselves unstable.

### Cited Findings
- Microsoft.Extensions.AI.Evaluation is a family of packages: `Microsoft.Extensions.AI.Evaluation` (core abstractions), `.NLP` (BLEU, GLEU, F1, non-LLM), `.Quality` (LLM-judged: Relevance, Completeness, Retrieval, Fluency, Coherence, Equivalence, Groundedness, plus agent-focused Intent Resolution, Task Adherence and Tool Call Accuracy), `.Safety` (uses the Microsoft Foundry Evaluation service, an Azure dependency), `.Reporting` (response caching and result storage), `.Reporting.Azure` (Azure Storage), and `.Console` (`dotnet aieval`). Works with MSTest, xUnit or NUnit, and can publish scores to telemetry — [Microsoft Learn: evaluation libraries](https://learn.microsoft.com/en-us/dotnet/ai/evaluation/libraries) (**verified**, fetched; page dated 2026-04-09, updated 2026-09-10). Custom metrics via `IEvaluator`.
- Response caching: the library stores model responses keyed by prompt and model, and serves them on later runs if the prompt and model are unchanged. This is a built-in form of cassette replay for judge calls — same source (**verified**).
- `RelevanceTruthAndCompletenessEvaluator` is marked experimental — same source (**verified**).
- The Quality and Safety evaluators are LLM-based or Foundry-based; the NLP evaluators are deterministic — same source (**verified**). The Safety package requires Foundry, which conflicts with the goal of an offline test suite, so keep it out of CI.
- Other frameworks (promptfoo, Inspect, DeepEval) and VCR/cassette libraries for .NET were **not researched** in this session (**unverified**).

### Inferences
- Cassette format: store each replayed call as request hash (model id, system prompt hash, message digest, tool schema digest, temperature) to response body plus usage. Re-record only on an explicit command. A changed prompt should fail replay loudly, not silently fall through to a real model.
- Fake gateway: implement a test `IChatClient` (or the Bazaar gateway interface) that returns scripted or cassette responses, injects 429/`retry-after`, spend-cap 429, timeouts and malformed tool calls, and records what was asked. This lets quota and vacation logic be tested without any model.
- Golden transcripts: compare GC message sequences and tool-call sequences structurally (who spoke, which tools, which event kinds), not as exact text, because model text is non-deterministic even at temperature 0 across versions.
- Loop guards: test them with a scripted agent that repeats the same tool call or the same @mention; assert the guard trips at the configured count and writes the expected GC event. This needs no model.
- Multi-agent coordination: run two or more fake agents through a real in-memory GC and Postgres (or a test container) with scripted replies, and assert ordering, sequence numbers and replay-from-seq behaviour (the reconnect design in Bazaar.Server.md). Every behaviour change needs a test (CLAUDE.md).
- LLM-as-judge pitfalls (from general knowledge, **unverified** in this session): judge scores drift with judge model version, self-preference bias when the judge is the same model family as the agent, and sensitivity to prompt wording. Keep judges out of gating and pin judge models if used.

### Gaps
- No primary source read on promptfoo, Inspect, DeepEval, or .NET VCR/cassette libraries; no comparison of their maturity.
- No verification of how Microsoft.Extensions.AI's `IChatClient` abstraction behaves under replay (streaming and tool-call shapes not checked).
- No source for loop-guard or multi-agent test practice specific to this stack.

---

## 5. Logs, retention, privacy and secret scrubbing

### Takeaway
Keep prompts and completions out of telemetry and logs by default; they live in GCs, which are already append-only and are the audit record. Telemetry carries IDs, sizes, token counts, status and timings. Add a redaction step before any export, and treat retention periods as an owner decision.

### Cited Findings
- The OTel GenAI content attributes are the main privacy surface; their default capture state was not verified (see section 1, **unverified**).
- Aspire dashboard OTLP endpoints accept telemetry without auth unless API-key auth is configured (**secondary**, [aspire.dev security considerations](https://aspire.dev/dashboard/security-considerations.md), snippet only).
- Anthropic rate-limit headers include workspace identifiers (`anthropic-workspace-id`), so usage records can link to the account (**verified**, [Anthropic rate limits](https://platform.claude.com/docs/en/api/rate-limits)). This is an account-linkage concern for logs; keep it internal.
- Bazaar.md: GCs are append-only, never purged; edits keep history; DMs are two-member GCs and the owner can read all of them (**verified**, file read). This makes GCs the content store, and telemetry should not duplicate them.
- Redaction tooling, scrubbing libraries, and retention defaults for OTel backends were **not researched** (**unverified**).

### Inferences
- Log/trace fields should be an allow-list (agent id, GC id, model id, provider id, sizes, durations, error codes). Anything else is dropped before the exporter, so new fields are opt-in.
- Secrets: API keys and GitHub App private keys should never reach logs. Scrub at the gateway HTTP layer (headers) and at the exporter, and add a test that a planted key does not appear in exported spans.
- Retention: telemetry (traces/logs) short, for example days, and usage records long, because cost and quota history are business records. The actual periods are an owner decision, not settled here.

### Gaps
- Retention defaults and legal constraints (counsel review is pending for the public release per Bazaar.md).
- Specific redaction library choice for .NET (not researched).

---

## Recommendation for v1 (synthesis, not verified as a single source)

1. Instrument with OpenTelemetry .NET from the start; export OTLP to the **Aspire standalone dashboard** on the NUC, localhost only, auth on. Use `Resume` mode only if cross-restart history is needed for debugging.
2. Put model-call telemetry behind one Bazaar mapping layer, pinned to a semconv version; keep content capture off.
3. Store usage as Postgres rows (one per provider call, raw usage fields, rate-limit headers, error codes, `retry-after`), and derive quota state from them. Drive the Observatory and company dashboard from Bazaar events and Postgres, not from the telemetry backend.
4. Model `RateLimited` and `SpendCapped` as distinct quota states to feed the vacation simulation.
5. Testing: fake gateway plus cassettes behind the `IChatClient`-style interface; deterministic gating tests; LLM judges as nightly, non-gating jobs. Microsoft.Extensions.AI.Evaluation is the candidate .NET eval library; keep Safety/Foundry out of CI.
6. Defer Grafana/Loki/Tempo, SigNoz, Jaeger, Langfuse and Phoenix until a concrete need. Their Windows support and footprint remain unverified.

## Conflicts and items to flag

- The Aspire overview page says telemetry persists; the standalone page says it does not by default. The standalone page was used (see section 2).
- Microsoft.Extensions.AI's Evaluation Safety package depends on Azure Foundry; this sits uneasily with the goal of an offline, deterministic test suite and with the platform's email/backend-agnostic stance. Keep it out of the offline suite.
- The DECIDED cost chip (GC UI) and the REJECTED Resource Economy (no money budgets) can coexist only if cost is display and analysis, not a gate. The notes assume that.
- Bazaar.Server.md says a NIM "~1,000 req/day" figure has no source (**verified** by reading the file). Any quota table built on it must stay labelled unverified.

## Items I could not verify (summary)

- GenAI semconv status and exact attribute names (tool calls, error type, finish reasons, content capture default, opt-in env var).
- Microsoft.Extensions.AI OpenTelemetry integration details (Microsoft Learn page returned 404).
- OpenTelemetry .NET NuGet package names and Npgsql instrumentation.
- Footprint, Windows support and licences of Jaeger, Grafana/Loki/Tempo, SigNoz, Phoenix, OpenLIT; Aspire dashboard licence and numeric telemetry limits.
- Langfuse official hardware requirements (only hosting-template figures found).
- LiteLLM and OmniRoute and FreeLLMAPI quota/usage docs beyond search snippets.
- Other providers' rate-limit headers and 429 shapes; current price tables.
- promptfoo, Inspect, DeepEval and .NET VCR/cassette libraries.
- Log redaction and retention defaults.
