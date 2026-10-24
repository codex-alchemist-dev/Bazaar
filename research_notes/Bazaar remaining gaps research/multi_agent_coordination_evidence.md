# Multi-Agent Coordination Evidence for Group-Chat-Style Systems (as of 2026-10-24)

Scope: evidence review for Bazaar's no-master-orchestrator design (GCs with @mentions, Council voting, peer-review reputation, Skimmer Bouncer gatekeeper, loop guards, persistent agents). Read-only research; no code was installed or run.

Verification labels used below: **[verified]** = primary source page read in this session (abstract or full page via WebFetch); **[secondary]** = search snippet, news or blog summary, or an abstract surfaced by search that I did not open; **[unverified]** = claim I could not source.

Caveat on the search method: WebFetch was used on a limited set of primary pages (about 9 fetches). Most paper-level numbers below come from search snippets and are marked [secondary]. Several papers were only read at abstract level. Nothing here is a full replication.

Key local context read: `/home/user/Bazaar/docs/deep-dives/Bazaar.md` (Council = DECIDED; Skimmer Bouncer, Psych Ward, Mirror Agent = PROPOSED; no master orchestrator = DECIDED) and `/home/user/Bazaar/research_notes/Bazaar stack feasibility research/agent_runtime_dotnet.md` (its Key Question 4, on GC coordination and gatekeepers, and Key Question 6, on reputation and voting, were explicitly unsourced; this note partly fills those gaps).

---

## Key Question 1: Prior multi-agent software-team systems and what they teach

### Takeaway
The best-documented success is orchestrator-worker research (Anthropic, 2025), which works on breadth-first, parallelisable tasks but costs about 15x chat tokens and is reported as a poor fit for coding and shared-context work. The most-cited critique (Cognition, 2025) argues that parallel subagents with unshared context produce conflicting assumptions. The large-sample failure study (MAST, 2025) finds failures clustered into design, inter-agent misalignment and verification. None of the reviewed primary sources I read evaluates a peer-chat (GC-only, no orchestrator) architecture, so Bazaar's core coordination model is untested by published evidence.

### Cited Findings
- Anthropic's multi-agent research system (lead agent plus parallel subagents, Claude Opus 4 lead, Sonnet 4 subagents) "outperformed single-agent Claude Opus 4 by 90.2%" on Anthropic's internal research eval; published 13 June 2025. **[verified]** — [Anthropic engineering blog](https://www.anthropic.com/engineering/multi-agent-research-system)
- Same post: on BrowseComp, token usage alone "explains 80% of the variance"; tool-call count and model choice bring the total to 95%. Agents use about 4x chat tokens and multi-agent systems about 15x. **[verified]** — [Anthropic engineering blog](https://www.anthropic.com/engineering/multi-agent-research-system)
- Same post, "poor fits": tasks where all agents must share the same context, tasks with many inter-agent dependencies, and "most coding tasks" (fewer truly parallelisable subtasks). It also says agents "are not yet strong at real-time coordination and delegation." **[verified]** — [Anthropic engineering blog](https://www.anthropic.com/engineering/multi-agent-research-system)
- Same post, early failure modes: spawning 50 subagents for simple queries, endless searches for nonexistent sources, subagents duplicating work. Mitigations: explicit effort-scaling rules in prompts, detailed task descriptions, "start wide, then narrow down." Synchronous subagent execution is named as a bottleneck for steering. **[verified]** — [Anthropic engineering blog](https://www.anthropic.com/engineering/multi-agent-research-system)
- Same post, reliability and evaluation: checkpoints and resume, retries, full production tracing that monitors "decision patterns without reading individual conversation contents", rainbow deployments; an LLM judge scored outputs on a 0.0–1.0 rubric; human testers caught failures automation missed (SEO content farms preferred over authoritative sources). **[verified]** — [Anthropic engineering blog](https://www.anthropic.com/engineering/multi-agent-research-system)
- Cognition, "Don't Build Multi-Agents" (published 12 June 2025): multi-agent setups where subagents work in parallel and a final agent merges results are "fragile" because subagents make conflicting assumptions and cannot see each other's work. Two principles: share full agent traces, not just messages; "actions carry implicit decisions." Recommends a single-threaded linear agent by default, plus a compression model for long histories. Explicitly says the author expects better cross-agent communication later, "not yet solved." This is an opinion from a vendor with a commercial interest, not an experiment. **[verified]** — [Cognition blog (redirected from cognition.ai)](https://cognition.com/blog/dont-build-multi-agents)
- MAST, "Why Do Multi-Agent LLM Systems Fail?" (arXiv 2503.13657; submitted 17 Mar 2025, v3 26 Oct 2025; 13 authors): 1600+ annotated traces across 7 MAS frameworks; taxonomy built from 150 traces; 14 failure modes in 3 categories (system design issues, inter-agent misalignment, task verification); inter-annotator kappa = 0.88; an LLM-as-judge annotation pipeline agreed with humans. The abstract-level page gives no prevalence percentages and does not mention group chat, debate or termination specifically. **[verified]** — [arXiv 2503.13657](https://arxiv.org/abs/2503.13657)
- ChatDev and MetaGPT cost claims: ChatDev reported "under 7 minutes" and "less than $1" per project; MetaGPT about "$1.09 per project." These come from secondary comparison sites that do not link methodology. Primary papers were not read. **[secondary]** — [pragmaticcoders comparison via search](https://www.pragmaticcoders.com/resources/ai-developer-tools); [dev.co MetaGPT review via search](https://dev.co/ai/frameworks/metagpt). Gap: primary ChatDev and MetaGPT papers not fetched.
- Magentic-One (arXiv 2411.04468, submitted 7 Nov 2024): lead Orchestrator "plans, tracks progress, and re-plans to recover from errors," and directs specialists; the abstract claims "statistically competitive performance" on GAIA, AssistantBench and WebArena, with no numbers on the abstract page. Ledger and stall-reset mechanics were not read. The paper notes AutoGenBench's "built-in controls for repetition and isolation" matter when agents have side effects. **[verified at abstract level]** — [arXiv 2411.04468](https://arxiv.org/abs/2411.04468)
- AutoGen (0.2 vs 0.4+ termination): the AG2 docs say 0.2 uses `max_round` on `GroupChat`; the 0.4+ AgentChat tutorial lists termination conditions (MaxMessageTermination, TextMentionTermination, TokenUsageTermination, TimeoutTermination, etc.) and says the condition is checked after each agent response in group chat teams. A `max_turns` parameter was not found in the official 0.4 termination page. **[secondary: search snippets of docs]** — [AG2 ending a chat](https://docs.ag2.ai/latest/docs/user-guide/advanced-concepts/ending-a-chat/); [AutoGen 0.4 termination](https://microsoft.github.io/autogen/0.4.6/user-guide/agentchat-user-guide/tutorial/termination.html)
- Generative Agents (Park et al., arXiv 2304.03442; submitted 7 Apr 2023, v2 6 Aug 2023): 25 agents in a Sims-like town; memory stream, reflection and planning; ablations show each component contributes to believability. The abstract states no limitations; retrieval failure and embellishment were not verified from the full paper. **[verified at abstract level]** — [arXiv 2304.03442](https://arxiv.org/abs/2304.03442)
- Microsoft Agent Framework 1.0 (3 April 2026, per the agent_runtime_dotnet note, citing the MS blog) ships group chat, handoff and Magentic-One orchestration patterns. These are orchestrator-selected speaker patterns. **[secondary, from the existing research note]** — [MS Agent Framework 1.0 blog](https://devblogs.microsoft.com/agent-framework/microsoft-agent-framework-version-1-0/)

### Inferences
- The strongest published evidence (Anthropic) comes from an orchestrator-worker design on read-only research. Bazaar's "no master orchestrator" choice is therefore not contradicted by evidence, but it is not supported by it either. For coding work, both Anthropic's and Cognition's sources point away from parallel fan-out with unshared context, which matters for Bazaar's Kanban-plus-GC workflow.
- **Conflict to report, not argue:** Anthropic's success relies on a lead agent that holds the plan. Bazaar's DECIDED "conversation is the coordinator" goes against that pattern. The evidence does not say which is better for persistent software teams; no source tests a GC-only design.
- Shared context is the common thread. Bazaar's GC record is append-only, which fits Cognition's "share full traces" principle. Whether agents actually read the full GC or only @mention slices (and the Skimmer Bouncer's filtering) decides whether Bazaar gets that benefit.

### Gaps
- No published evaluation found of GC-only (peer-chat, no orchestrator) multi-agent software teams. Not found in this search.
- Primary ChatDev and MetaGPT papers, with their cost and success tables, were not read.
- Magentic-One ledger/stall mechanics and numeric results were not read (only the abstract).
- Cognition's "not yet solved" is a 2025 statement; I did not find a 2026 update from Cognition on the topic.
- MAST's prevalence figures for each failure mode were not on the page read; need the full PDF if the report needs them.

---

## Key Question 2: Multi-agent debate and voting

### Takeaway
Debate is not a reliable accuracy gain once compute is matched; in most matched-budget studies, majority voting over independent samples matches or beats debate. Voting works best on reasoning tasks. The decisive design risk is sycophancy: agents abandon correct answers after reading peers' reasoning, so Council votes whose reasoning is visible before the vote are exposed to this. Evidence is mostly on GSM8K/MMLU-style benchmarks, not software decisions.

### Cited Findings
- Kaesberg et al., "Voting or Consensus? Decision-Making in Multi-Agent Debate" (arXiv 2502.19130; ACL Findings 2025): compares seven decision protocols. Reported voting gains of 13.2% on reasoning and consensus gains of 2.8% on knowledge tasks; adding agents helps, extra debate rounds before voting hurt; proposed methods raised performance by up to 3.3% and 7.4%. **[secondary: search summary; primary not opened]** — [arXiv 2502.19130 v4](https://arxiv.org/html/2502.19130v4)
- Huang et al., "Large Language Models Cannot Self-Correct Reasoning Yet" (arXiv 2310.01798): replicating Du et al. (3 agents, 2 rounds, GSM8K, gpt-3.5-turbo), debate only slightly beats self-consistency at equal agent count and falls clearly behind majority voting at equal numbers of responses. **[secondary: search summary of section 4; primary not opened]** — [arXiv 2310.01798](https://arxiv.org/pdf/2310.01798)
- "Stop Overvaluing Multi-Agent Debate" (arXiv 2502.08788): 5 debate methods, 9 benchmarks, 4 models; debate underperforms self-consistency especially at comparable LLM calls or tokens. **[secondary: search summary]** — [arXiv 2502.08788v3](https://arxiv.org/html/2502.08788v3)
- "Should we be going MAD?" (ICML 2024): current MAD systems do not reliably beat self-consistency or multi-path ensembling; some variants improve with hyperparameter tuning. **[secondary: search summary]** — [ICML 2024 poster](https://icml.cc/virtual/2024/poster/34657)
- "Debate or Vote: Which Yields Better Decisions in Multi-Agent LLMs" (arXiv 2508.17536): argues majority voting accounts for most of the gains credited to debate; models debate as a stochastic process. **[secondary: search summary]** — [arXiv 2508.17536v2](https://arxiv.org/html/2508.17536v2)
- Free-MAD (arXiv 2509.11035): proposes skipping consensus-seeking; argues multiple rounds raise token cost and conformity spreads errors; notes majority voting is vulnerable when some agents are compromised by prompt injection. **[secondary: search summary]** — [arXiv 2509.11035](https://arxiv.org/html/2509.11035v1)
- Wynn, Satija and Hadfield, "Talk Isn't Always Cheap: Understanding Failure Modes in Multi-Agent Debate" (arXiv 2509.05396; ICML MAS workshop 2025): debate can lower accuracy over time, even with more capable models in the group; models often abandon correct answers for peers' flawed reasoning; mixed-capability groups can be dragged down by a weaker agent; longer debates can make results worse; on CommonSenseQA debate always hurt in their runs, mixed elsewhere. **[verified at abstract/search-excerpt level; tables not read]** — [arXiv 2509.05396](https://arxiv.org/pdf/2509.05396)
- Sycophancy in debate: Pitre et al. (ACL Findings 2025) argue sycophancy inflates cost by requiring extra rounds to reach consensus; Yao et al. (2025) report sycophancy amplifies premature disagreement collapse; an incentive prompt that paid for correct answers did not remove correct-to-incorrect flips. **[secondary: search summary]** — [arXiv 2509.23055 "Peacemaker or Troublemaker"](https://arxiv.org/pdf/2509.23055); [arXiv 2605.00914 "Cost of Consensus"](https://arxiv.org/pdf/2605.00914)
- Matched-budget study (arXiv 2605.09618, 2026): majority voting alone accounts for most of the performance gains attributed to debate; debate only slightly beats voting on average. **[secondary: search summary]** — [arXiv 2605.09618](https://arxiv.org/pdf/2605.09618)
- LLM-as-judge self-preference: Wataoka, Takahashi and Ri (arXiv 2410.21819; submitted 29 Oct 2024; NeurIPS 2024 Safe GenAI workshop). The abstract says GPT-4 shows "a significant degree of self-preference bias" and that LLMs give higher evaluations to lower-perplexity outputs than humans do, regardless of whether outputs were self-generated. No numbers on the abstract page. **[verified at abstract level]** — [arXiv 2410.21819](https://arxiv.org/abs/2410.21819)
- Position bias: Shi et al., "Judging the Judges" (arXiv 2406.07791): position bias is not random and varies by judge and task; it is affected by the quality gap between solutions. Versions report different sizes (v3: 9 judges, ~80,000 instances; another version: 15 judges, 150,000+). A 2026 AMCIS paper reports Definition A preferred 65.7% of the time in one dictionary study. **[secondary: search summary; primary not opened]** — [arXiv 2406.07791](https://arxiv.org/html/2406.07791v9); [AMCIS 2026 via search](https://aisel.aisnet.org/amcis2026/sig_svs/svs/1)
- Mitigation commonly used: run each pair in both orders. A blog (anecdotal) reports 20–40% verdict flips on closely matched pairs. **[unverified]** — [tianpan.co blog](https://tianpan.co/blog/2026-04-27-llm-judge-bias-audit-length-position-format)

### Inferences
- **Council design (DECIDED):** The evidence favours voting over open debate for reasoning-type decisions. A Council that lets agents see each other's reasoning before they vote is the setup the sycophancy papers warn about. A candidate design, untested here: each agent forms and records an independent vote first, then sees the tally and reasons, with at most one round, then the tie goes to the owner. The independent-first step is an inference, not a published result.
- Majority vote is only as good as the independence and diversity of voters. Same-model voters share errors. Prompt-injection vulnerability (Free-MAD) argues for weighting or excluding untrusted inputs.
- Weighted voting by reputation: no primary evidence found showing reputation-weighted votes beat unweighted ones for LLM agents. Treat as untested.
- Self-preference suggests that an agent judging its own work, or work from the same model family, is biased. Bazaar's "peer review" should prefer cross-family reviewers where possible (inference from the self-preference result).

### Gaps
- No primary-source reading of Kaesberg et al., Huang et al., Free-MAD or the 2026 matched-budget paper; figures are from search summaries.
- No evidence found on weighted vs majority voting specifically for LLM agents (no weights tested).
- Position bias numbers depend on which Shi et al. version is cited; not resolved.
- None of these studies test software-engineering decisions (merge/no-merge, design choices).

---

## Key Question 3: Reputation and peer review among LLM agents

### Takeaway
Recent (2026) studies find LLM reviewers are unreliable and gameable: revising a paper toward reviewer preferences inflates scores without scientific gains, and AI reviewers show excessive agreement. An LLM-scored reputation system would inherit these problems. Reputation should anchor to outcome signals (tests, merges, human acceptance) and not to LLM-written reviews alone.

### Cited Findings
- Review Arcade (arXiv 2605.28897, May 2026): on ACL papers, LLM–human review alignment is limited and varies with prompt and model; an iterative draft-revise loop raised overall scores for up to 35% of papers; the authors say naively prompted LLM reviewers are unstable and not yet generally reliable. **[secondary: abstract via search]** — [arXiv 2605.28897](https://arxiv.org/pdf/2605.28897)
- Baumann et al. (2026), as reported in news coverage (8 July 2026 date given in coverage): rewriting papers to fit model-generated reviewer preferences inflated AI review scores; ICLR 2026 data, 60 papers. **[secondary: news coverage only; primary not found]** — [Let's Data Science coverage](https://letsdatascience.com/news/ai-review-tools-vulnerable-to-manipulation-in-peer-review-dd048284)
- "Stop automating peer review without rigorous..." position paper: compares AI and human reviews of ICLR 2026, reports "hivemind" excess agreement among AI reviewers, and says AI scores are gameable via stylistic "paper laundering". **[secondary: search snippet; not opened]** — [catalyzex listing](https://www.catalyzex.com/paper/stop-automating-peer-review-without-rigorous)
- LLM-REVal (arXiv 2510.12367): simulates review pipelines with revise-review rounds to test trust in LLM reviewers. Findings not read. **[unverified findings]** — [arXiv 2510.12367](https://arxiv.org/pdf/2510.12367)
- ICML 2026 "Articulate Intuition or Genuine Analysis?": argues LLM-judge reliability should separate surface analytical form from epistemic function; pilot rubric distinguishes genuine fault-finding from fluent-sounding text. **[secondary: listing]** — [ICML 2026 listing](https://icml.cc/virtual/2026/77486)
- Self-preference bias (see Key Question 2) means a reviewer model can favour its own or familiar style. **[verified at abstract level]** — [arXiv 2410.21819](https://arxiv.org/abs/2410.21819)
- Cognition's critique (see Key Question 1) describes actions as carrying implicit decisions. Relevant to review: a reviewer who sees only the diff, not the decision context, scores the wrong thing. **[verified]** — [Cognition](https://cognition.com/blog/dont-build-multi-agents)

### Inferences
- Reputation built only from LLM peer reviews is open to reputation inflation: agents (or their authors) learn what reviewers reward. Bazaar's "reputation from peer reviews" needs anchoring to hard signals: CI results, post-merge defects, reverts, human accept/reject, and reviewer calibration against those outcomes.
- Mitigations supported in principle by the sources above (not tested in agent teams): blind review (reviewer does not know the author's identity or model), cross-family reviewers, both-order pairwise comparisons, periodic calibration of each reviewer against human-labelled outcomes, and tracking reviewer agreement to detect "hivemind" convergence.
- A reviewer's score should be stored with rationale (append-only), matching the GC record. This is consistent with the DECIDED auditability goal, but no source tested it.

### Gaps
- No primary source read for Baumann et al., the "stop automating" paper, or LLM-REVal. Findings above depend on search snippets and news coverage.
- No evidence found on reputation systems among agents specifically (e.g., reputation-weighted teams, sybil or collusion among agents). Not searched in depth.
- Whether reputation changes actual delegation outcomes: no evidence found.

---

## Key Question 4: Cheap gatekeepers and routers (Skimmer Bouncer)

### Takeaway
Routing and cascade work shows large cost savings on benchmark tasks with small quality loss, but the evidence is for query routing to models with labelled data, not for a small model deciding which of a stream of group-chat messages is relevant to a hidden expensive agent. The failure mode that matters for Bazaar (the gatekeeper silently dropping an important message) was not measured in any source I found. The Skimmer Bouncer's savings and miss rate are therefore unproven.

### Cited Findings
- RouteLLM (Ong et al., ICLR 2025; arXiv 2406.18665): trains routers on human preference data (Chatbot Arena) to send queries to a strong or weak model. Project page reports cost reductions over 85% on MT-Bench, 45% on MMLU and 35% on GSM8K versus GPT-4-only, at 95% of GPT-4's performance. **[secondary: project page via search; paper not read in full]** — [RouteLLM ICLR 2025 page](https://iclr.cc/virtual/2025/poster/30737); [Berkeley project page](https://sky.cs.berkeley.edu/project/routellm/)
- FrugalGPT (Chen, Zaharia, Zou; arXiv 2305.05176, 2023): a cascade sends queries to cheap models first and escalates when a learned DistilBERT scorer rates the answer as unsatisfactory. Claims: matches best single API at up to 98% lower cost, or +4% accuracy at the same cost; HEADLINES dataset 80% cost cut with +1.5% accuracy versus GPT-4. Caveats from coverage: needs labelled data to train the scorer; results are 2023-era APIs on three tasks. **[secondary: search summary; paper not read in full]** — [arXiv 2305.05176](https://arxiv.org/pdf/2305.05176)
- Judges and gatekeepers share a failure mode: a learned scorer can be biased (see Key Question 2, self-preference and perplexity). A small gatekeeper that prefers "familiar" messages could systematically under-wake agents for novel or unusual messages. This is an inference from the perplexity finding, not a tested result.
- Anthropic's post (see Key Question 1) notes a monitoring approach that tracks decision patterns without reading conversation content. Relevant to auditing a gatekeeper: monitor its decisions and miss rate with sampled human review. **[verified]** — [Anthropic](https://www.anthropic.com/engineering/multi-agent-research-system)

### Inferences
- The router literature supports the general idea that most queries do not need the strongest model. It does not support "a small model can skim every GC message and reliably decide relevance." Those are different tasks: routing picks a model for a query the system must answer, while the Skimmer decides whether to skip a message that might matter to someone.
- Cheaper, more reliable first-line filters that the sources suggest or imply, to test against the Skimmer: deterministic rules (@mentions, thread participation, explicit role tags, keyword and file-path matches for the agent's ownership area), embedding similarity to the agent's open tasks, and a periodic digest with a guaranteed wake for critical categories (blocked, review requested, decision called). The digest approach fits the append-only GC because nothing is hidden, only deferred. These are design inferences, not evaluated alternatives.
- An asymmetric design is safer: the gatekeeper may only defer, never drop; anything it skips still appears in the next digest; a missed-message rate is measured on a labelled set of real GC messages before enabling it.
- Calibration: the Skimmer's output should be a structured decision with confidence, logged with its input, as the existing feasibility note already proposes. Thresholds should be set on a labelled set, which FrugalGPT's own design also required.

### Gaps
- No source found on relevance filtering of chat streams, nor on miss rates of small-model message filters.
- RouteLLM and FrugalGPT figures are secondary; neither paper's error analysis was read.
- The savings figures are on benchmark queries with 2023–2024 models. No 2026 cost-accuracy study of small-model routers for multi-agent chat was found.
- Bazaar.md's "Skimmer Bouncer" is PROPOSED; this note does not argue for or against building it, it only states what is unproven.

---

## Key Question 5: Persistent long-running agents (memory drift, identity, consolidation, sleep)

### Takeaway
Evidence on persona stability over long horizons is thin and recent (2025–2026 arXiv preprints). Preprints report memory degradation, fact loss, timeline confusion and persona drift over long interactions, and propose temporal memory governance and consolidation. I found no established result showing that a "sleep" or consolidation pass improves agent behaviour over time in multi-agent teams.

### Cited Findings
- Generative Agents (Park et al., 2023): a memory stream with reflection and planning gave believable behaviour in a 25-agent sandbox, with ablations showing each component contributes. Abstract only; limitations not read. **[verified at abstract level]** — [arXiv 2304.03442](https://arxiv.org/abs/2304.03442)
- ARPM (arXiv 2605.14802, 2026): long-range interaction can cause fact loss, timeline confusion, persona drift and reduced stability; proposes temporal memory governance for long-term persona consistency. **[secondary: search snippet; not opened]** — [papers.cool 2605.14802](https://papers.cool/arxiv/2605.14802)
- Continuum Memory Architectures for Long-Horizon LLM Agents (arXiv 2601.09913): proposes memory that updates over time with consolidation into higher-order abstractions; four preliminary probes flag open problems in latency, drift and interpretability; preliminary, not a full evaluation. **[secondary: search summary]** — [arXiv 2601.09913](https://arxiv.org/html/2601.09913v1)
- DynamicMem (arXiv 2606.22877, June 2026): months-long personal-assistant memory benchmark; reports a retention-versus-agility trade-off and preference degradation over long histories. **[secondary: search summary]** — [emergentmind 2606.22877](https://www.emergentmind.com/papers/2606.22877)
- Mem0 (arXiv 2504.19413; from the existing feasibility note): reported ~90% token savings versus full context on LOCOMO, self-reported and not independently replicated. **[secondary: existing note; abstract not re-read]** — [arXiv 2504.19413](https://arxiv.org/abs/2504.19413)
- Cognition: a dedicated compression model for long action histories is recommended, but described as "hard to get right." **[verified]** — [Cognition](https://cognition.com/blog/dont-build-multi-agents)

### Inferences
- Persona drift and memory loss are reported as open problems; Bazaar's identity-stability goal has no proven mechanism yet. Keeping the raw GC record (append-only) and treating consolidated memory as a derived, versioned view (with provenance) is consistent with the sources' concerns, and lets drift be measured against the record.
- A consolidation ("agent sleep") step can be tested as a regression: compare decisions before and after consolidation on a fixed scenario set. This is untested evidence territory; no source I found validates it.

### Gaps
- No evidence found on persona consistency benchmarks with consolidation or sleep for multi-agent teams. Searched once; weak results.
- The three 2026 memory preprints were not opened; claims are from snippets.

---

## Key Question 6: Loop guards and runaway cost controls in practice

### Takeaway
The public evidence on loop control is mostly engineering practice rather than measured results. The sources I read support three controls: hard turn or termination conditions (AutoGen), effort-scaling rules for spawning (Anthropic), and sequential rather than parallel fan-out with shared context (Cognition). I found no published study measuring loop frequency or the effectiveness of cycle detection in agent group chats.

### Cited Findings
- AutoGen 0.2 uses `max_round` on `GroupChat`; AutoGen 0.4+ uses composable termination conditions checked after each agent response, including MaxMessageTermination, TokenUsageTermination and TimeoutTermination. **[secondary: docs via search]** — [AG2 docs](https://docs.ag2.ai/latest/docs/user-guide/advanced-concepts/ending-a-chat/); [AutoGen 0.4.6 termination](https://microsoft.github.io/autogen/0.4.6/user-guide/agentchat-user-guide/tutorial/termination.html)
- Magentic-One's AutoGenBench offers "built-in controls for repetition and isolation," described by the authors as important when agents' actions have side effects. **[verified at abstract level]** — [arXiv 2411.04468](https://arxiv.org/abs/2411.04468)
- Anthropic: explicit effort-scaling rules in prompts to stop over-spawning (50 subagents for simple queries was an early failure); checkpoints, retries and resume-from-error; rainbow deployments. **[verified]** — [Anthropic](https://www.anthropic.com/engineering/multi-agent-research-system)
- Debate papers (Key Question 2) show more rounds raise token cost without accuracy gains and can make answers worse (Wynn et al.; Free-MAD). **[secondary]** — [arXiv 2509.05396](https://arxiv.org/pdf/2509.05396); [arXiv 2509.11035](https://arxiv.org/html/2509.11035v1)

### Inferences
- The round-limit evidence from debate (more rounds before voting hurt) is a direct argument for a low maximum reply depth in GC threads.
- Proposed controls (design inferences, untested): per-thread maximum depth; per-agent wake rate; cycle detection on the who-replied-to-whom graph; a per-task token budget with a hard stop that posts a visible GC notice (keeps the append-only record). The Bazaar feasibility note already proposes these; this note adds no evidence for them.
- Token budgets are a real cost: Anthropic reports token use explains most of performance variance, which also means a budget cap also caps quality. This is a trade-off to measure, not a settled point.

### Gaps
- No measured loop-frequency or cycle-detector data found for multi-agent chat.
- No source found on Bazaar-style "work-life" shift/curfew throttling as a cost control.
- AutoGen 0.4 `max_turns` parameter not confirmed in official reference.

---

## Cross-cutting: what the evidence supports, risks, or leaves untested

| Bazaar design element | Status in evidence | Notes |
|---|---|---|
| No master orchestrator; GC as coordinator | Untested by published evidence; orchestrator-worker is the best-documented success | Anthropic reports poor fit for shared-context and coding work; Cognition argues for shared context. Conflict to report, not argue. |
| Append-only GC record with full traces | Supported in principle | Matches Cognition's "share full traces" principle. |
| Council: whole-team vote with stated reasoning; tie to owner | Partly supported (voting > debate on reasoning tasks) | Visible reasoning before the vote invites sycophancy; keep votes independent first, few rounds. |
| Reputation from peer reviews | Risky | LLM reviewers gameable and inflatable (2026 secondary reports); anchor to hard outcomes. |
| Skimmer Bouncer (cheap gatekeeper) | Unproven | Router/cascade savings are real on benchmarks; no evidence on missed-message rates in chat. Measure before building. |
| Loop guards, depth limits | Plausible, engineering practice | Debate evidence supports low round counts. No measured frequency data. |
| Persistent identity via memory and consolidation | Open problem | Persona drift reported in 2026 preprints; consolidation unvalidated. |
| Work-life shifts as cost control | No evidence found | Novel; untested. |

## Open items for the report writer
- Verify the Kaesberg et al. (ACL Findings 2025) and Wynn et al. figures against the PDFs before quoting numbers.
- Verify the Baumann et al. 2026 result from its primary paper; currently only news coverage.
- Read MAST's full PDF for per-mode prevalence if needed.
- Search gap: GC-style or chat-room multi-agent teams (e.g., other 2025–2026 software-team simulations with shared chat channels) were not surveyed; only a single broad search was run.
