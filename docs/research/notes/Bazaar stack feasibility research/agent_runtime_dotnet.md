# Bazaar Agent Runtime in .NET (as of October 2026): Libraries, Patterns, Build vs Adopt

Research status: partial. The search tool hit its quota after six queries, so most findings come from direct fetches of primary pages. Items marked **[UNVERIFIED]** come from memory or a secondary snippet and need checking before any decision depends on them. Several key questions have large gaps, listed in each section.

## Key Question 1: .NET LLM client options (Microsoft.Extensions.AI, Anthropic SDK, OpenAI SDK, Semantic Kernel, Microsoft Agent Framework)

### Takeaway
Use `Microsoft.Extensions.AI` (`IChatClient`) as the single seam in Bazaar, with the official Anthropic C# SDK behind it for Claude models. Microsoft Agent Framework reached 1.0 on 3 April 2026 (MIT), but its orchestration model leans toward a central orchestrator, which conflicts with Bazaar's "conversation is the coordinator" rule. Take its libraries selectively rather than adopting it as the runtime.

### Cited Findings
- Microsoft Agent Framework 1.0 was announced on 3 April 2026 for both .NET and Python. Stable 1.0 features include agents, a graph-based workflow engine with checkpointing and hydration, orchestration (sequential, concurrent, handoff, group chat, Magentic-One), MCP tool discovery, and a pluggable memory/context-provider layer. Named backends include Foundry, Mem0, Redis, and Neo4j. — [Microsoft Agent Framework 1.0 blog](https://devblogs.microsoft.com/agent-framework/microsoft-agent-framework-version-1-0/)
- The same blog says A2A is supported but "A2A 1.0 support coming soon," and that Foundry Hosted Agent Integration and Azure Durable Functions are preview features. — [Microsoft Agent Framework 1.0 blog](https://devblogs.microsoft.com/agent-framework/microsoft-agent-framework-version-1-0/)
- The blog says Semantic Kernel and AutoGen users should "migrate to Microsoft Agent Framework" and that MAF unifies Semantic Kernel's enterprise foundations with AutoGen's orchestration patterns. A search snippet claimed both older projects are in maintenance mode. The primary blog did not say that, so treat "maintenance mode" as **[UNVERIFIED]**. — [Microsoft Agent Framework 1.0 blog](https://devblogs.microsoft.com/agent-framework/microsoft-agent-framework-version-1-0/); search snippet from [Visual Studio Magazine](https://visualstudiomagazine.com/articles/2026/04/06/microsoft-ships-production-ready-agent-framework-1-0-for-net-and-python.aspx)
- Licence: the `microsoft/agent-framework` repo page lists the MIT licence. Core package named as `Microsoft.Agents.AI`; Foundry integration as `Microsoft.Agents.AI.Foundry`. The page did not state target frameworks or give a latest release tag. — [microsoft/agent-framework](https://github.com/microsoft/agent-framework)
- The official Anthropic C# SDK is the NuGet package `Anthropic` (version 10 or later). It targets .NET Standard 2.0 and above. It provides an `IChatClient` implementation via `Microsoft.Extensions.AI.Abstractions` (`client.AsIChatClient(...)`). It supports streaming (`CreateStreaming`, `IAsyncEnumerable`), automatic retries (2 by default, for connection errors, 408, 409, 429, and 5xx), per-request `WithOptions`, OpenTelemetry (`AddSource("com.anthropic.sdk.csharp*")`), and separate packages for Bedrock, Vertex, Foundry, and AWS. Major version 13 sends API version `2026-10-21`. Repo: `anthropics/anthropic-sdk-csharp`. — [Anthropic C# SDK docs](https://platform.claude.com/docs/en/cli-sdks-libraries/sdks/csharp)
- The Anthropic docs say the third-party `Anthropic.SDK` package (tghamm) is not affiliated with Anthropic. Its nubrowse listing shows version 5.10.0 dated 20 February 2026. Do not use it. — [NuGet listing via search](https://nubrowse.com/packages/Anthropic.SDK/5.5.1); [Anthropic C# SDK docs](https://platform.claude.com/docs/en/cli-sdks-libraries/sdks/csharp)
- The Anthropic C# SDK's `IChatClient` example shows MCP tools from the `ModelContextProtocol` package being passed directly into `ChatOptions.Tools`, with `UseFunctionInvocation()`. — [Anthropic C# SDK docs](https://platform.claude.com/docs/en/cli-sdks-libraries/sdks/csharp)

### Inferences
- `IChatClient` looks like the right seam. Bazaar can route every call through one `IChatClient` pipeline (model whitelist check, logging, budget, retry, and a fake for tests) and swap the provider underneath. This is a thin custom layer over the abstraction, not a framework adoption.
- Because every model call goes through OmniRoute's OpenAI-compatible endpoint, the OpenAI-protocol client may be the default and the Anthropic SDK may only be needed for Claude-specific features. This is an inference; the OmniRoute details were not researched.
- Agent Framework's orchestration patterns (group chat, handoff, Magentic-One) are built around an orchestrator that picks speakers. That is the opposite of Bazaar's "GC is the coordinator" rule, so the agent loop and routing should stay custom. Its libraries (checkpointing, memory providers, MCP client) may still be worth studying or reusing. This is an inference from the feature descriptions, not from reading MAF's code.
- Adopt Agent Framework wholesale: not recommended yet. It is 1.0 and MIT-licensed, but the coordination model does not fit the design. Re-evaluate once its A2A and hosting preview features reach GA.

### Gaps
- OpenAI .NET SDK version, licence, and `Microsoft.Extensions.AI.OpenAI` adapter status: not researched.
- Semantic Kernel's current status and exact maintenance policy: only the blog's migration language was confirmed.
- Latest version numbers of `Microsoft.Extensions.AI` and `Anthropic`: not confirmed on NuGet.
- OmniRoute's OpenAI-compatible specifics (streaming, tool-call format, model-whitelist enforcement): not researched.
- Whether Agent Framework's agent memory and checkpointing work without its orchestration layer: not verified.

## Key Question 2: Tool calling and MCP (official C# MCP SDK, hosting, sandboxing)

### Takeaway
Use the official C# MCP SDK (`modelcontextprotocol/csharp-sdk`, Apache-2.0) for both client and server roles. Sandboxing was not researched. The per-agent VM design in Bazaar.Soul is the likely boundary, but the details are unverified.

### Cited Findings
- The C# MCP SDK provides these packages: `ModelContextProtocol.Core` (minimal dependencies, low-level client or server APIs), `ModelContextProtocol` (hosting and DI extensions), `ModelContextProtocol.AspNetCore` (HTTP-based servers), and `ModelContextProtocol.Extensions.Apps`, `.Extensions.Skills`, `.Extensions.Tasks`. The repo is licensed under Apache-2.0. Both client and server roles are supported. — [modelcontextprotocol/csharp-sdk](https://github.com/modelcontextprotocol/csharp-sdk)
- The MCP SDK's `McpClient` connects to remote servers over `HttpClientTransport`, and its tools can be passed to `IChatClient` (see Key Question 1). — [Anthropic C# SDK docs](https://platform.claude.com/docs/en/cli-sdks-libraries/sdks/csharp)
- The repo page does not state the MCP spec version or the latest release. Stdio transport support was not confirmed on the page. — [modelcontextprotocol/csharp-sdk](https://github.com/modelcontextprotocol/csharp-sdk)
- Microsoft Agent Framework's agents can discover and invoke tools on MCP servers. — [Microsoft Agent Framework 1.0 blog](https://devblogs.microsoft.com/agent-framework/microsoft-agent-framework-version-1-0/)

### Inferences
- Each agent's tools can be MCP servers running inside its own VM, with Bazaar as an MCP client. Bazaar's own capabilities (Kanban, GC posting, memory CRUD) can be exposed as an in-process MCP server so every agent uses the same tool surface. This is a design inference.

### Gaps
- Sandboxing options for agent tool execution (containers, Firecracker/microVMs, Hyper-V on Windows, gVisor, and so on): **not researched**. This is the largest hole in this question.
- Spec version the C# SDK implements, and its stability/version history: not confirmed.
- Security model for MCP over HTTP with per-agent auth: not researched.

## Key Question 3: Agent memory on PostgreSQL + pgvector

### Takeaway
Build memory on PostgreSQL + pgvector with a small custom schema, using ideas from Mem0 (extract-then-update operations) and tiered-memory designs. Memory edits should go through validated tools, not raw writes. The evidence for Mem0's claims is self-reported.

### Cited Findings
- Mem0 paper (Chhikara et al., arXiv:2504.19413, submitted 28 April 2025). Reported on LOCOMO: 26% relative improvement in LLM-as-judge over OpenAI's memory system; 91% lower p95 latency than a full-context approach; more than 90% token cost savings versus full context; a graph variant about 2% better overall. These figures come from the authors' abstract and were not independently replicated here. — [arXiv:2504.19413](https://arxiv.org/abs/2504.19413)
- Microsoft Agent Framework's memory/context-provider layer names Mem0, Redis, and Neo4j as backends, and Foundry. — [Microsoft Agent Framework 1.0 blog](https://devblogs.microsoft.com/agent-framework/microsoft-agent-framework-version-1-0/)

### Inferences
- Schema suggestion (inference, not sourced): `memory_items(agent_id, scope [private|project], kind [episodic|semantic|procedural], content, embedding vector, source_ref, created_at, superseded_by, confidence)`, with an HNSW index on the embedding and B-tree indexes on `agent_id` and `scope`. Episodic memories cite GC message IDs so every memory has provenance. Semantic memories are consolidated from episodes during compaction.
- Compaction ("coffee break"): run during a shift break or idle window, not in the hot path. Summarise the last N episodes into semantic facts and keep the raw GC record, since GCs are append-only.
- Memory CRUD tools: expose `memory_add`, `memory_update`, `memory_forget` (soft delete, with `superseded_by`) as tools. Validate them server-side: scope check, size limit, duplicate detection by embedding similarity, and a provenance requirement. Log each edit so memory changes can be audited.
- Shared project memory needs write permissions per scope and conflict handling when two agents edit the same fact. This was not researched.
- Letta (formerly MemGPT) tiered memory (core/archival/recall) is a familiar idea, but no source was fetched in this session. **[UNVERIFIED]**

### Gaps
- Letta, Zep, and other memory systems' current designs and benchmarks: not fetched. Zep's dispute of Mem0's comparisons is **[UNVERIFIED]** (from memory).
- pgvector version and current HNSW/IVFFlat guidance: not researched.
- .NET-specific memory libraries: not researched.
- Memory-edit validation and poisoning defences: not researched.

## Key Question 4: Group-chat-as-coordinator (wake on @mention, inboxes, loop guards, token budgets, Skimmer Bouncer)

### Takeaway
No sources were gathered for this question. The design direction (cheap gatekeeper, per-agent inbox, depth limits) is plausible, but the cost, latency, and accuracy evidence for a gatekeeper model is missing. Build an offline evaluation on real GC transcripts before trusting the Skimmer Bouncer.

### Cited Findings
- None. The search quota ran out before any query on this topic.

### Inferences
- A gatekeeper decision (wake / skip / summarise-later) should be a structured output with a confidence value. Log every decision with its input message so it can be replayed and tuned.
- Loop guards: enforce a maximum reply depth per thread, a per-agent wake rate, and a cycle detector over the "who replied to whom" graph. Stop the thread when a limit trips and post a visible notice in the GC, which fits the append-only record.
- Per-agent inboxes can be durable queues in Postgres (`SELECT ... FOR UPDATE SKIP LOCKED`), with one consumer per agent. This is a common Postgres pattern, not sourced here.

### Gaps
- Evidence on cost, latency, and accuracy of cheap gatekeeper or router models: **none gathered**. Do not treat the Skimmer Bouncer's savings as proven.
- Literature on LLM routing and cascade classifiers (for example, RouteLLM-style work): not searched.
- Token budgeting strategies per agent: not researched.

## Key Question 5: Durable workflows (Temporal, Hangfire, Quartz.NET, Orleans, plain hosted services)

### Takeaway
Orleans is a serious fit for the persistent-agent model: each agent is a grain with single-activation semantics, durable reminders for shifts and inbox wake-ups, and optional persistent state. Temporal is strong for long multi-step workflows but adds a server to operate. Hangfire and Quartz.NET were not researched. Plain hosted services with Postgres queues remain the lowest-risk start.

### Cited Findings
- Orleans is described on its repo as a "Cloud Native application framework for .NET." It is MIT-licensed, and grains can hold volatile or persistent state in any storage system, saved with `state.WriteStateAsync()`. Reminders are "a durable scheduling mechanism for grains"; timers are their non-durable counterpart. Stateless workers have no state. The repo does not state a version number or release date. — [dotnet/orleans](https://github.com/dotnet/orleans)
- Temporal's .NET SDK reached 1.0.0 (stable) on 5 December 2023. Search results disagreed on the latest NuGet version: a NuGet feed page showing 1.14.1 (updated May 2026) and an older snapshot showing 1.11.1. Verify on nuget.org before pinning. — [Temporal changelog](https://temporal.io/changelog/net-sdk-generally-available); [sdk-dotnet 1.0.0 release](https://github.com/temporalio/sdk-dotnet/releases/tag/1.0.0); [Temporalio on NuGet](https://www.nuget.org/profiles/Temporal)
- Microsoft Agent Framework's workflow engine offers checkpointing and hydration for long-running processes. — [Microsoft Agent Framework 1.0 blog](https://devblogs.microsoft.com/agent-framework/microsoft-agent-framework-version-1-0/)

### Inferences
- Orleans grain-per-agent maps onto the persona model: one activation per agent prevents two workers from processing the same agent's inbox at once. Orleans streams (for GC fan-out) and reminders (for shift changes) fit the design, though the streams feature was not verified in this session. **[UNVERIFIED]** that Orleans streams are the right fan-out mechanism.
- Temporal fits multi-step, long-running work such as a Kanban task that waits days for a review. It adds an operational dependency.
- Suggested split (inference): Postgres as system of record for GCs and memory; Orleans grains for agent activation, inbox, and reminders; Temporal only if long workflows prove painful to build on grains. Hangfire or Quartz.NET are unnecessary if reminders cover scheduling.
- Build vs adopt: adopt Orleans for activation and scheduling; it is a bigger commitment than plain hosted services, so prototype one agent and one GC first. Do not adopt Temporal yet.

### Gaps
- Orleans current version, .NET target, and licence confirmation for the NuGet packages: not confirmed (repo page gave MIT).
- Orleans operational issues (grain activation cost, memory footprint on a single NUC, Windows deployment): not researched.
- Hangfire and Quartz.NET current versions, licences, and fit: not researched.
- Temporal server licence and self-hosting requirements: not researched.

## Key Question 6: Reputation and voting data models, Kanban with blocked-by enforcement, audit of reasoning

### Takeaway
No sources gathered. The design points are standard relational patterns, and the key choice is to make reasoning a first-class, immutable record. The inferences below are not sourced.

### Cited Findings
- None gathered.

### Inferences
- Reputation: append-only evaluation events (`reviewer_id`, `subject_agent_id`, `task_id`, `score`, `rationale`) with a materialised score view per skill. Keep Valkey as a cache for hot scores, as the design doc says.
- Council votes: `votes(vote_id, voter_id, choice, reasoning, cast_at)` with the vote record and reasoning written to the GC as a message, so the GC stays the source of truth. Ties escalate to the human.
- Kanban blocked-by: store dependencies as a `task_edges` table. Enforce "cannot move to In Progress while any blocker is not Done" in a single transaction with `SELECT ... FOR UPDATE` on the task row, plus a database check for cycles (recursive CTE on insert). Postgres can enforce this without a second service.
- Audit of personality-driven decisions: store the decision, the inputs it saw (message IDs, memory IDs, model and prompt version), and the stated reasoning as separate immutable fields, so a decision can be replayed.

### Gaps
- Any published research on agent reputation systems or LLM-judge reliability: not searched.
- Industry examples of Kanban dependency enforcement: not searched (common in Jira and similar tools, but not verified here).

## Key Question 7: Deterministic testing of agent systems (replayable transcripts, fake gateway, evaluation harness)

### Takeaway
Not researched. The recommended approach (record-and-replay of model I/O behind the `IChatClient` seam, plus a fake gateway) follows from the architecture, but no sources were gathered.

### Cited Findings
- None gathered.

### Inferences
- Put a recorder/replayer as a `DelegatingChatClient`-style middleware under `IChatClient` (Microsoft.Extensions.AI supports this pattern, per general knowledge **[UNVERIFIED]**). In tests, replay recorded responses keyed by a hash of the request. In evals, run the live model against a fixed scenario set and score outputs.
- Make the clock, random seed, and GC message IDs injectable, so a replayed transcript produces identical state.

### Gaps
- Evaluation frameworks for .NET agents (and whether Microsoft.Extensions.AI.Evaluation exists and is mature): not researched.
- Practical guidance on flaky LLM tests: not researched.

## Key Question 8: Compression tools named in the docs (RTK, Caveman)

### Takeaway
Not researched. I could not confirm what RTK or Caveman are, so this note cannot say whether they help. The design doc already says to test them empirically on real code and diffs before trusting them for source files, and that remains the right step.

### Cited Findings
- None gathered. The search quota ran out, and I did not fetch any page about these tools.

### Inferences
- Any compression of source code given to a model should be measured for correctness regressions (for example, a patch applied and its tests passing) rather than judged by token savings alone.

### Gaps
- What RTK and Caveman are, their licences, their maintenance status, and any benchmarks: **not researched**. Do not rely on either until someone checks them.

## Build-vs-adopt summary (inferences from the above)

| Area | Recommendation | Confidence |
|---|---|---|
| LLM seam | Thin custom layer over `Microsoft.Extensions.AI.IChatClient` | Medium-high |
| Claude provider | Official `Anthropic` NuGet package (not `Anthropic.SDK`) | High (documented) |
| Other providers via OmniRoute | OpenAI-protocol client; OpenAI .NET SDK not verified | Low (not researched) |
| Agent runtime | Build custom; the coordination model conflicts with Agent Framework's orchestration | Medium |
| Agent Framework | Watch; cherry-pick libraries; revisit after A2A and hosting reach GA | Medium |
| MCP | Adopt official `ModelContextProtocol` packages (Apache-2.0) | High (documented) |
| Agent activation and scheduling | Evaluate Orleans (MIT) with a one-agent spike | Medium (unverified version) |
| Long workflows | Defer Temporal; Postgres queues first | Low-medium |
| Memory | Build on Postgres + pgvector; borrow Mem0's operation model | Medium |
| Gatekeeper (Skimmer Bouncer) | Unproven; measure on real GC transcripts before building | Low (no evidence gathered) |
| Sandboxing | Unresearched | None |
| RTK / Caveman | Unresearched | None |

## Next steps to close the gaps
1. Re-run searches (after the quota resets) for: Orleans streams and current version; Hangfire and Quartz.NET status; OpenAI .NET SDK and the `Microsoft.Extensions.AI.OpenAI` adapter; sandboxing options for agent tool execution on Windows; LLM cascade and routing evidence (cost, latency, accuracy); what RTK and Caveman are.
2. Verify NuGet versions and licences directly on nuget.org for `Anthropic`, `ModelContextProtocol`, `Microsoft.Extensions.AI`, `Microsoft.Agents.AI`, `Orleans`, and `Temporalio`.
3. Spike one agent on Orleans and one on plain hosted services with Postgres queues, and compare.
