# Coding-agent harness for Bazaar: tools, edit formats, context, sandboxing, subagents, verification

Research date: 2026-10-24. Budget: about 21 tool calls, so coverage is partial. Most primary sources read are dated 2024 to late 2025. I did not find a 2026 primary source for any harness's current tool set. Labels: **verified** = primary source read in this session; **secondary** = search snippet or aggregator; **unverified** = from memory or not checked.

Bazaar context read: `/home/user/Bazaar/docs/deep-dives/Bazaar.md` (agents each have a VM; GCs are the coordination system; Claude lane uses Anthropic API keys only, never Pro/Max subscription credentials; subagents and workers run on Haiku 5.5). `/home/user/Bazaar/research_notes/Bazaar stack feasibility research/agent_runtime_dotnet.md` (prior note: use `IChatClient` seam, official `Anthropic` NuGet package, official C# MCP SDK, build the agent loop custom; sandboxing "not researched").

## Key Question 1: Prior art (Claude Code, Codex CLI, OpenHands, Aider, Cline/Roo, OpenCode, SWE-agent, Goose)

### Takeaway
The best-documented prior art is Claude Code subagents, Aider, OpenHands SDK, SWE-agent and Codex CLI's repo. For Cline/Roo, OpenCode and Goose I fetched nothing, so their tool sets are unverified here. Across the sources that were read, the shared pattern is a small tool set (read, search, edit, shell), an isolated execution workspace, and a context-isolation mechanism for subagents.

### Cited Findings
- Claude Code subagents: each runs "in its own context window with a custom system prompt, specific tool access, and independent permissions," and only the summary returns to the parent. Tool access is an allowlist (`tools`) or denylist (`disallowedTools`) per subagent. Default nesting allows subagents to spawn subagents "up to three layers below the main conversation"; `CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH=1` turns nesting off. Default concurrency cap of 20 subagents per session (`CLAUDE_CODE_MAX_CONCURRENT_SUBAGENTS` adjusts it). Model resolution order: per-invocation parameter, then subagent frontmatter, then `CLAUDE_CODE_SUBAGENT_MODEL`, then main model. — [Claude Code subagents docs](https://code.claude.com/docs/en/sub-agents) (**verified**; read at the redirect target of docs.claude.com; page version not shown)
- Cognition's critique of multi-agent designs: "Don't Build Multi-Agents" describes Claude Code's subagents as "usually only tasked with answering a question, not writing any code," and says they do not work in parallel with the main agent. — [Cognition, "Don't Build Multi-Agents"](https://cognition.com/blog/dont-build-multi-agents) (**verified**; dated 12 June 2025; a vendor's characterization). This conflicts with the Claude Code docs above, which document parallel subagents with a concurrency cap of 20. The docs are the more current source, so Cognition's description looks outdated for the current product; this is an inference, not a verified change.
- OpenHands (current SDK, not the older app): the SDK runs an agent reasoning-action loop; tools follow an "Action/Observation/Executor pattern"; workspaces are `LocalWorkspace`, `DockerWorkspace` (spawns a container running an Agent Server) and `RemoteAPIWorkspace` (HTTP to a server). Pre-built tools named on the page include `BashTool` and `FileEditor`. The page gives no version or date and does not mention a browser tool. — [OpenHands SDK architecture overview](https://docs.openhands.dev/sdk/arch/overview) (**verified**, no date)
- SWE-agent: the paper's abstract reports pass@1 of 12.5% on SWE-bench and 87.7% on HumanEvalFix; submitted 6 May 2024, v3 on 11 Nov 2024. The abstract says the Agent-Computer Interface helps agents "create and edit code files" and "navigate entire repositories." The ablation numbers for linter-guarded edits and windowed file viewing were not in the abstract, so they are **unverified** here. — [arXiv:2405.15793](https://arxiv.org/abs/2405.15793) (**verified** abstract only)
- Aider: edit formats are whole, diff (search/replace), diff-fenced, udiff, and editor-diff/editor-whole used in architect mode (architect model plans, editor model writes the edit). Aider picks a format per model; `--edit-format` overrides it. "For lesser known models aider will default to using the 'whole' editing format." — [Aider edit formats](https://aider.chat/docs/more/edit-formats.html) (**verified**, no date)
- Codex CLI: Apache-2.0 licence, installable via npm (`@openai/codex`) or Homebrew. The README did not mention sandbox modes, approvals or AGENTS.md, and it gives no version. — [openai/codex](https://github.com/openai/codex) (**verified**)
- Codex sandbox and approval docs: not retrieved. The Codex Security page only points to "Agent approvals and security," "Sandboxing," "Permission modes," and "Windows sandbox" pages, and I did not read them. — [learn.chatgpt.com/docs/security](https://learn.chatgpt.com/docs/security) (**verified** pointer only)
- Cline/Roo Code, OpenCode, Goose: not fetched. Tool sets, permission models and edit formats are **unverified** in this note.

### Inferences
- Bazaar can reuse the Claude Code pattern: a per-subagent tool allowlist, a fresh context per subtask, and a summary returned to the parent. Those are the controls that matter for a multi-agent team. The OpenHands SDK's action/observation/executor split is a clean shape for Bazaar's tool layer, and it maps onto the prior note's plan of exposing Bazaar's own capabilities as an in-process MCP server.
- Because Cognition's critique and the Claude Code docs disagree on how much parallelism subagents get, Bazaar should measure this rather than adopt either view by default.

### Gaps
- Current (2026) tool sets, permission models and sandbox defaults for Claude Code, Codex, Cline/Roo, OpenCode and Goose: not verified.
- Browser tooling in OpenHands: the page did not say.

## Key Question 2: Which edit format works best for weaker and cheaper models

### Takeaway
The evidence is aged and is not on Haiku 5.5, DeepSeek V4, or current free-tier models. On the older Aider Exercism leaderboard, whole-file output had the highest format compliance for weak models, while diff formats gave better pass rates and token efficiency for strong models. The current Aider polyglot leaderboard shows DeepSeek scoring well for low cost, but it gives no format-by-model breakdown for small models. For Bazaar, the defensible choice is to measure the edit format per model on Bazaar's own tasks.

### Cited Findings
- Aider's older Exercism Python leaderboard (dated 12 April 2025 on the page, and replaced by the polyglot leaderboard) reports "pass rate / correct-edit-format rate." Examples: `diff` format for claude-3-5-haiku-20241022: 75.2 / 95.5. `whole` format for claude-3-haiku-20240307: 47.4 / 100.0. For `diff`, the lowest format-compliance rows included llama3-70b-8192 at 73.5% and gemini-exp-1121 at 83.5%. Several small models scored 100% format compliance on `whole`. The page's notes say "The 'whole' format is the easiest for an LLM to use, but it uses a lot of tokens," and "Models which can use one of the diff formats are much more efficient, using far fewer tokens." — [Aider edit leaderboard](https://aider.chat/docs/leaderboards/edit.html) (**verified**; dated 12 April 2025; model versions are 2024 generation)
- The same page notes that diff-fenced was for Gemini models that "often fail to follow diff's fencing approach," and that udiff was used with GPT-4 Turbo to reduce "lazy coding," where other formats led the model to "skip large sections of code and leave placeholder comments." — [Aider edit formats](https://aider.chat/docs/more/edit-formats.html) (**verified**; no dates given)
- Aider's current polyglot leaderboard: 225 Exercism exercises across C++, Go, Java, JavaScript, Python and Rust; last updated 20 November 2025. Top rows: gpt-5 (high) 88.0%, $29.08, diff; gemini-2.5-pro-preview-06-05 (32k think) 83.1%, diff-fenced. Cheap models: DeepSeek-V3.2-Exp (Reasoner) 74.2% at $1.30; DeepSeek-V3.2-Exp Chat 70.2% at $0.88. claude-3-5-haiku-20241022: 28.0% at $6.06. Several rows show $0.00 cost, which the page does not explain. — [Aider leaderboards](https://aider.chat/docs/leaderboards/) (**verified**; the cost figures are Aider's own run costs, and the page does not say which API or price tier)
- The page does not give separate malformed-edit or syntax-error rates for small models. — [Aider edit leaderboard](https://aider.chat/docs/leaderboards/edit.html) (**verified**, absence noted)
- SWE-bench-family edit data for small models: not retrieved.

### Inferences
- For Haiku-class and free-tier models, the older data suggest that whole-file or a simpler edit format maximises format compliance, at a token cost. Diff formats give better pass rates for strong models. This is an inference from the 2025 Aider leaderboard, not a finding about Haiku 5.5 or DeepSeek V4.
- A practical design: default to a whole-file or line-anchored edit tool for cheap models, measure format-compliance and pass rate per model on Bazaar tasks, and only then move to search/replace. Keep the choice in each agent's whitelist config, since Bazaar requires per-agent model whitelists.
- Architect/editor splitting (a strong planner and a cheaper editor) is a plausible Bazaar pattern. Aider documents it, but Aider's own page does not give its benefit with numbers.

### Gaps
- No published edit-format benchmark for Haiku 5.5, DeepSeek V4 Flash or Pro, or NVIDIA NIM free-tier models was found.
- The old and new Aider leaderboards use different tasks and cannot be compared row by row.
- SWE-bench edit-format ablations were not retrieved.

## Key Question 3: Context engine (curated context, code indexing, embeddings)

### Takeaway
Anthropic's guidance is to keep the smallest set of high-signal tokens, use a hybrid of upfront retrieval and just-in-time tool reads, and use compaction plus structured notes for long work. Aider's repo map is a graph-ranked, token-budgeted summary of the repo. For .NET, a tree-sitter binding exists on NuGet, but Roslyn and LSP were not verified in this session.

### Cited Findings
- Anthropic (29 Sep 2025): context is "a finite resource with diminishing marginal returns," so the aim is "the smallest set of high-signal tokens." Upfront embedding retrieval is contrasted with just-in-time retrieval (keep file paths or stored queries, load with tools at runtime). The article recommends a hybrid. Compaction: summarise near the context limit; "clearing old tool results is a light, safe form of compaction." Structured notes: "regularly writes notes persisted to memory outside of the context window" (NOTES.md, to-do lists). — [Effective context engineering for AI agents](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents) (**verified**)
- Aider repo map: built from the whole git repo, "each source file is a node and edges connect files which have dependencies," ranked by a graph algorithm, and cut to the `--map-tokens` budget, which defaults to 1k tokens. It "only includes the most important identifiers." The fetched page did not mention tree-sitter or PageRank by name. — [Aider repo map](https://aider.chat/docs/repomap.html) (**verified**; the tree-sitter and PageRank detail is **unverified** from this source)
- .NET tree-sitter: TreeSitter.DotNet 1.3.0 is described on NuGet as .NET bindings for tree-sitter, bundling native parsers for 28+ grammars, MIT licence, with types Language, Parser, Tree, Node, TreeCursor, Query. Search snippets disagree on platform support (one lists only win-x64 and win-x86; another lists Windows, Linux and macOS). A separate `TreeSitter 1.0.0` binding is said to be tested only on Linux. — [TreeSitter.DotNet on NuGet](https://packages.nuget.org/packages/TreeSitter.DotNet/) and [RtkSharp.TreeSitter.Trimmed on NuGet](https://www.nuget.org/packages/RtkSharp.TreeSitter.Trimmed/) (**secondary**, from search snippets; not read in full)

### Inferences
- A curated context package per request should be a small, ordered list: the current file and its diff, the GC thread that asked for the work, the task's Kanban dependency state, and a repo map. Everything else should be fetched by tool, which matches Anthropic's just-in-time guidance.
- Bazaar's GCs already hold the coordination record, so the context builder should pull decisions and task state from GCs rather than from a second store. This is consistent with the DECIDED rule that GCs are the coordination system.
- For C#, Roslyn gives exact symbol information and is the likely first choice for a .NET-only server. Tree-sitter fits the multi-language case (Luau, GDScript, Godot C#). These are **unverified** inferences; Roslyn and LSP were not researched.

### Gaps
- No evidence was gathered on whether embeddings-based code search improves agent outcomes compared with grep plus repo maps.
- Roslyn workspace APIs, LSP servers for .NET, and the cost of indexing a repo in a VM: not researched.
- Tree-sitter platform support for the chosen .NET package: conflicting snippets, not resolved.

## Key Question 4: Running tools inside an isolated VM from a host server

### Takeaway
The only verified remote-execution pattern is OpenHands' split between a local, Docker or remote workspace that exposes a server API over HTTP. Patterns for guest-agent transport, streaming, timeouts and resource limits were not verified in this session, so they remain open.

### Cited Findings
- OpenHands SDK workspaces: `LocalWorkspace` (single process), `DockerWorkspace` (container running an Agent Server), `RemoteAPIWorkspace` (connects over HTTP). Tools "run alongside the agent in whatever environment workspace configures (local/container/remote)." — [OpenHands SDK architecture overview](https://docs.openhands.dev/sdk/arch/overview) (**verified**, no date)
- The prior Bazaar note records that the official C# MCP SDK (`ModelContextProtocol`, Apache-2.0) supports client and server roles and HTTP transport (`HttpClientTransport`). Its stdio transport was not confirmed. — `/home/user/Bazaar/research_notes/Bazaar stack feasibility research/agent_runtime_dotnet.md` (**verified** as a file read; its MCP claims are secondary to the SDK repo)
- Anthropic harness article: agents used browser automation (Puppeteer MCP) for end-to-end tests. The authors noted limits, such as browser-native alert modals being invisible to the tooling. — [Effective harnesses for long-running agents](https://www.anthropic.com/engineering/effective-harnesses-for-long-running-agents) (**verified**; 26 Nov 2025)

### Inferences
- A guest agent inside each VM that exposes a small API (run command, read file, apply edit, stream output) and is called over an authenticated channel from the host is consistent with the OpenHands `RemoteAPIWorkspace` shape. Bazaar could also expose that guest API as an MCP server. Both options are **unverified** against any Bazaar code.
- Streaming output, per-call timeouts and resource limits (CPU, memory, disk, network) should be part of the guest API from the start. Their exact design was not researched.
- Bazaar.md says the VM topology is OPEN (nested WSL2 vs two VMs per agent, pending a benchmark). The harness design should not depend on that choice; it should only need a workspace API.

### Gaps
- Timeouts, streaming protocols, and resource-limit defaults in current harnesses: not verified.
- Sandbox security models (Codex, Claude Code, OpenHands) beyond the OpenHands workspace list: not verified.
- Whether any harness uses SSH rather than a guest agent: not verified.

## Key Question 5: Subagents and delegation

### Takeaway
Fresh context per subtask with a summary returned to the parent is the common pattern, and Claude Code documents it in detail. Cognition argues that subagents that do not share full context make systems fragile, and that critique has not been tested against Bazaar's own GC design.

### Cited Findings
- Claude Code: a non-fork subagent "doesn't see your conversation history, the skills you've already invoked, or the files Claude has already read." Its initial context includes its own system prompt, the task message written for it, and CLAUDE.md files. "The verbose output stays in the subagent's context while only the relevant summary returns to your main conversation." Subagents spend their own tokens while running, and "returning many detailed results can still consume main-conversation context." Limits: subagents "work within a single session," and the built-in Explore and Plan agents "are one-shot." — [Claude Code subagents](https://code.claude.com/docs/en/sub-agents) (**verified**)
- Cognition (12 June 2025): two principles, "Share context, and share full agent traces, not just individual messages" and "Actions carry implicit decisions, and conflicting decisions carry bad results." Its Flappy Bird example shows two subagents drifting apart in style. The post says multi-agent designs that break these principles are "fragile." — [Cognition, "Don't Build Multi-Agents"](https://cognition.com/blog/dont-build-multi-agents) (**verified**; a critique from one vendor, which sells a single-threaded agent)
- Anthropic (29 Sep 2025): sub-agents handle focused work in clean context windows and "return only a condensed, distilled summary of its work." Best suited to complex research and parallel exploration. — [Effective context engineering](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents) (**verified**)
- Anthropic harness article: the failure modes it lists (declaring victory too early, one-shotting, marking features done prematurely, leaving broken or undocumented state) show up in single-agent runs. It names a multi-agent setup with specialised testing or QA agents as an open question. — [Effective harnesses](https://www.anthropic.com/engineering/effective-harnesses-for-long-running-agents) (**verified**)

### Inferences
- Map Bazaar's delegation onto the Claude Code shape: a subagent gets a fresh context, a task message, a tool allowlist and a model, and returns a summary plus a link to its full trace stored in a GC or the database. That addresses Cognition's "share full traces" point without sharing a whole context window.
- Bazaar's subagent model policy (all subagents on Haiku 5.5) matches Claude Code's `CLAUDE_CODE_SUBAGENT_MODEL` pattern. Bazaar's per-agent whitelists need to be enforced server-side, not only through an environment variable.
- The Claude Code docs allow subagents to spawn subagents by default. For Bazaar, a depth limit should be set explicitly, since Bazaar also has loop guards in its GC design (the prior note's Key Question 4 is unsourced).

### Gaps
- Empirical comparisons of subagent designs on coding benchmarks: not retrieved.
- Failure modes specific to cheap models as subagents (instruction drift, format errors): not researched.

## Key Question 6: Verification (tests, build, UI checks, fake success)

### Takeaway
Agents tend to claim success too early unless the harness requires an external check. The strongest source here is Anthropic's harness article: a machine-readable feature list that only the agent can flip to passing, end-to-end checks, and a rule against editing tests. SWE-agent's linter-guarded edits are the other concrete example, but their effect size was not verified.

### Cited Findings
- Anthropic harness: the feature list is JSON because "the model is less likely to inappropriately overwrite it than Markdown." The rule: "It is unacceptable to remove or edit tests because this could lead to missing or buggy functionality." Fake success observed: "Claude marks features as done prematurely." The fix was self-verification before a feature is marked passing, and a basic end-to-end check at session start. — [Effective harnesses](https://www.anthropic.com/engineering/effective-harnesses-for-long-running-agents) (**verified**)
- Aider's polyglot benchmark measures whether a model "edits code and follows instructions without human intervention" and reports pass rate and cost. — [Aider leaderboards](https://aider.chat/docs/leaderboards/) (**verified**)
- SWE-agent: the ACI is credited with helping agents edit files and navigate repos. Linter-guarded editing is described in the paper body, which I did not read. — [arXiv:2405.15793](https://arxiv.org/abs/2405.15793) (**verified** abstract only; the linter claim is **unverified**)
- Claude Code subagent docs: the parent receives only a summary, so the parent cannot inspect the work unless it asks for the trace. — [Claude Code subagents](https://code.claude.com/docs/en/sub-agents) (**verified**)

### Inferences
- Bazaar's verification gate should be a server-side check, not an agent's claim. A PR is "done" only when the server has run the build and the tests in the agent's VM and recorded the exit codes and logs, and a different agent or the human has reviewed the diff. Agent self-reports should be treated as data.
- Fake-success checks to build in: (a) the test count must not drop versus the base branch; (b) test files must not be edited in a PR without a reviewer flag; (c) a build must run from clean; (d) UI changes need an Avalonia.Headless test (Bazaar's rule), not a screenshot claim.
- The harness should record every tool call with its exit code, so a reviewer can replay a run. That fits Bazaar's append-only GC rule and the prior note's replayable-transcript idea.

### Gaps
- No published data was found on how often agents fake success, or how well LLM judges catch it.
- Headless UI verification for Avalonia in an agent VM: not researched (Bazaar's own rule covers the test type only).
- Test-impact and flaky-test handling: not researched.

## Cross-cutting conflicts and open items for the main session
- Subscription credentials: Bazaar.md says Pro/Max subscription credentials must never go through the gateway. If the harness wraps the Claude Code CLI inside each VM, that must use API keys only. This note did not verify Anthropic's current terms for that use; Bazaar.md cites its own research for the policy, and the main session should check it before any harness choice.
- Edit-format choice for cheap models is the least-evidenced decision. Recommend a small in-house benchmark (Bazaar's own repo tasks, per model) before fixing a format per agent.
- Age of evidence: most measured data is from 2024 and 2025 models. Haiku 5.5, DeepSeek V4 and current free-tier models have no measurements in the sources read.

## Source list (all read in this session)
- https://aider.chat/docs/leaderboards/edit.html
- https://aider.chat/docs/more/edit-formats.html
- https://aider.chat/docs/leaderboards/
- https://aider.chat/docs/repomap.html
- https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents
- https://www.anthropic.com/engineering/effective-harnesses-for-long-running-agents
- https://arxiv.org/abs/2405.15793
- https://cognition.com/blog/dont-build-multi-agents
- https://code.claude.com/docs/en/sub-agents
- https://github.com/openai/codex
- https://learn.chatgpt.com/docs/security
- https://docs.openhands.dev/sdk/arch/overview
- https://packages.nuget.org/packages/TreeSitter.DotNet/ (search snippet only)
- /home/user/Bazaar/docs/deep-dives/Bazaar.md
- /home/user/Bazaar/research_notes/Bazaar stack feasibility research/agent_runtime_dotnet.md
