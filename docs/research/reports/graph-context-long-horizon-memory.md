# Graph context for long-horizon agent memory

Status: RESEARCHED (2026-11-20). Nothing here is DECIDED. Every recommendation is a proposal for the owner and for the Constellation memory thread; none changes a DECIDED item.

Question from the owner: how does graph context help an agent keep working over a long stretch of time, and what should GraphDRAG do about it?

GraphDRAG is retrieval over Constellation's own vault nodes: the notes, research, internal docs and decisions that agents write, per project and across linked projects. It is not a separate memory graph (doc 19, "Agent memory governance"; `constellation-memory-graph.md`).

## 1. Short answer

A long-running agent has far more written down than fits in its context window, so something must choose what to load. Plain similarity search finds notes that sound like the question. It misses notes that are needed but do not sound like it: the decision a plan was based on, the earlier note this one replaced, the research behind a conclusion. Links between notes fix exactly that gap. The agent finds a few notes by similarity, then follows links to the notes they depend on.

What the evidence says, in order of confidence:

0. The owner wants real graph reasoning, not snippets pasted into a prompt. Section 2b sets that out: typed path and dependency queries over the vault's DAG-shaped edges, returned as a structured bundle, on a multi-core CPU kernel with optional GPU providers.
1. Graphs help most on questions that need several linked facts (multi-hop) and on broad "what is going on across all of this" questions. They do not help, and sometimes hurt, on simple look-up questions. A fair 2025 to 2026 study (GraphRAG-Bench, ICLR 2026) found GraphRAG often does worse than plain vector search overall.
2. The expensive part of most graph systems is building the graph with a language model (extracting entities and relations from text). Bazaar does not need that for its core graph, because agents already write `[[wiki-links]]`, `derived_from` and `supersedes` into notes. Those links are free, exact and reviewable.
3. Time matters. Memory that cannot say "this was true then, replaced now" gives agents stale answers. Bazaar already keeps revisions and a `Supersedes` edge, which is most of what the temporal-graph systems (Zep/Graphiti) add.
4. A reranker (a small model that re-scores the shortlist) is the cheapest measurable gain and should come before anything fancier.
5. Nothing in the published numbers should be trusted for Bazaar until it is measured on Bazaar's own vault (doc 08 already says this; there is still no labelled set).

## 2. How graph context improves long-horizon work

Four mechanisms, each with a plain example.

- **Reaching notes that do not look like the question.** The agent asks "why did we pick Postgres NOTIFY for GC fan-out?" The decision note matches. The benchmark that justified it does not mention "fan-out" at all but is linked from the decision. One hop finds it.
- **Following history.** A note was replaced by a newer one. The link from new to old lets the agent return the current note and, if asked, the reason it changed, instead of returning both and letting them disagree.
- **Summarising a neighbourhood.** For "what do we know about the mail system", similarity returns twenty small notes. A summary written over the cluster (a hub or community summary) answers in a few hundred tokens and points to the notes underneath.
- **Spreading attention from several seeds.** If three different notes in the top results all point at one fourth note, that note is probably important. Ranking by how much of the graph's "flow" lands on a note (personalised PageRank, as in HippoRAG) captures this and costs almost nothing at query time.

Over many sessions the same graph also acts as a table of contents the agent can navigate by choice, which is what MemGPT/Letta and A-MEM do in different ways.

## 2b. Real graph reasoning, not snippets in a prompt (owner clarification, 2026-11-20)

The owner wants retrieval that uses the graph's structure itself: paths, multi-hop chains, dependency and decision chains, run on a GPU or many cores. Search results pasted into context are not that. This section is what that means in practice.

**The vault is already a typed graph, and part of it is a DAG.** `DerivedFrom` and `Supersedes` point only backwards in time, so they cannot form a cycle. That makes a decision chain a real directed acyclic graph: a decision supersedes an earlier one, rests on research, which rests on a measurement. `Link` edges are general and may form cycles, so traversals must track visited nodes.

**Operations that are graph reasoning (and what each answers):**

| Operation | Answers | Cost on a CSR graph |
|---|---|---|
| Ancestors / descendants along `DerivedFrom` | "What is this conclusion built on?" "What breaks if this fact changes?" | Linear in the reachable subgraph. |
| Supersede chain walk to the head | "What is the current answer, and how did it change?" | Linear in chain length. |
| Shortest and k-shortest typed paths between two seeds | "How is note A connected to decision B?" The path itself is the evidence. | Bounded BFS, ms on millions of edges. |
| Personalised PageRank from several seeds | Which notes the seeds jointly point at (HippoRAG). | A few sparse matrix-vector passes. |
| Subgraph extraction around seeds, scored by relevance | The connected neighbourhood, not isolated hits (the idea behind G-Retriever and PathRAG-style methods; background, not re-checked). | Bounded BFS plus scoring. |
| Topological order of a dependency subgraph | Present premises before conclusions. | Linear. |
| Reverse impact ("who depends on this") | Staleness: which notes need a re-check when one is superseded. | Linear on the reverse index. |

**What the agent should receive.** Not a flat list of snippets. A structured bundle: the seed notes, the paths found between them with edge types and direction ("D3 supersedes D1; D3 derived from R7"), each note's one-line card, and the head of each supersede chain. Token cost is mostly the paths and cards, so it is cheaper than dumping bodies, and the structure is what lets the model follow a chain of reasoning without re-deriving it. Bodies stay fetchable by id (retrieval doc rule 2).

**Reasoning loops.** For hard questions the agent can drive traversal itself in a bounded loop (expand, inspect, choose next edge), in the style of "think on graph" methods (background). Bounds: at most N tool calls, a token cap, and the same per-hop access check. This is optional and is where most cost lies.

**Where the GPU and many cores help, honestly.** Pointer-chasing traversal on a vault-sized graph (even tens of millions of edges) is memory-bound and finishes in milliseconds to tens of milliseconds on a multi-core CPU using a compressed sparse row layout and parallel frontier expansion. A GPU wins on big batch workloads: many simultaneous queries, personalised PageRank for many seeds at once, whole-graph analytics (communities, centrality) during consolidation, and learned scoring such as graph neural networks. The clear GPU wins in this pipeline are the embedding model, the reranker, and any graph neural scorer. Per section 4, NVIDIA's graph library (cuGraph) will not run on the owner's Arc B580; the portable design is: a CSR graph kernel interface with a multi-core CPU implementation as the baseline (`Parallel.For` over frontier chunks, SIMD where useful) and an optional GPU provider module (cuGraph on NVIDIA, a compute-shader or SYCL provider for Arc) selected by the operator. Keep CPU as the reference so results can be compared.

**Honest status of evidence.** The graph-structured methods above mostly report gains on multi-hop QA over text corpora (section 3). No benchmark covers decision-chain or dependency queries over a vault, so the labelled set in section 7 must include path, impact and "current answer" questions, and the CPU/GPU choice should follow measured latency on the real graph, not the vendor numbers.

## 3. The systems, what they do, what is measured

"Verified" means the claim appeared in a source read this session. "Background" means it comes from the papers as the author remembers them and was not re-checked; treat those as leads. Almost all headline numbers are reported by the authors of the method.

| System | Idea | Measured result | Cost | Fit for Bazaar |
|---|---|---|---|---|
| **Microsoft GraphRAG** (2024) | A language model extracts entities and relations, the graph is clustered into communities, each community gets a summary; "global" questions are answered from the summaries. | Strong on whole-corpus questions, weaker than vector search on local fact look-up (Microsoft's own BenchmarkQED, verified). | Highest of the group: one third-party estimate is about 40k tokens and 0.10 USD per document (verified, secondhand). | Take the idea of per-cluster summaries. Skip the extraction pipeline. |
| **LightRAG** (2024) | Lighter graph build; two-level retrieval (entity level and theme level); incremental updates. | Authors report wins over GraphRAG on their own judged comparisons (background). One independent benchmark shows it among the faster to index (verified). | Moderate, about 8k tokens per document in one estimate (verified, secondhand). | The incremental-update property matters: vault notes change constantly. |
| **HippoRAG / HippoRAG 2** (2024, 2025) | Build a graph, then rank by personalised PageRank from the query's seed nodes. | Authors report better multi-hop retrieval than similar systems and 10 to 30 times cheaper than iterative retrieval (partly verified). HippoRAG 2 is near parity with newer methods on multi-hop (verified, one paper). One benchmark found HippoRAG the slowest to index, contradicting the authors (verified). | Offline build cost is real and disputed. Query time is cheap. | Best fit for the "expansion from seeds" step, run over the link graph Bazaar already has. |
| **RAPTOR** (2024) | Recursively cluster chunks and summarise them into a tree; retrieve across levels. | Authors report large gains on long-document questions (background; the exact figure was not re-checked). | One summarisation pass per level. | Good model for hub and community summaries. |
| **Zep / Graphiti** (2025) | Temporal knowledge graph: every fact has "valid from / to" and "learned at" times; new facts supersede old ones instead of overwriting. | Authors report up to 18.5 percent better accuracy and 90 percent lower latency on LongMemEval than full-context, and 94.8 vs 93.4 percent against MemGPT on DMR (verified, vendor-reported, no independent replication found). | Language model extraction on every write. | Copy the temporal model. Bazaar's revisions and `Supersedes` already hold most of it. |
| **A-MEM** (2025) | Each new note is linked to related old notes by a language model; old notes are updated as new ones arrive (Zettelkasten style). | Authors report large gains over MemGPT-style baselines on LoCoMo (verified as reported by a secondary summary; exact figures not confirmed) and big token savings. No independent replication found. | One model call per write. | Natural during consolidation, under the reader-session rules of doc 08 section 7. |
| **MemGPT / Letta** (2023, kept up since) | The agent manages its own memory tiers with tool calls. | Used as a baseline everywhere; no independent benchmark found this session. | Extra tool calls and tokens per turn. | Bazaar's working, episodic and semantic tiers already match; do not add a second tier system. |
| **Graph-of-thought memory** | Keep reasoning steps as a graph and revisit them. | Mixed, mostly small studies (background). | Varies. | Thinking GCs already hold this; do not duplicate (doc 19, memory of Thinking GCs). |
| **Rerankers** | A small model re-scores the shortlist against the query. | Typically the largest single precision gain for the cost. Cross-encoder over top 100 is roughly 140 ms; top 25 roughly 45 ms and loses about two nDCG points versus top 100; a language-model reranker over top 50 is about 700 ms (verified, vendor blogs, hardware dependent). | Linear in shortlist size. | Do it, small shortlist, local. |

### What is measured about graphs versus plain search

- Plain vector search wins on single-fact and fine-detail questions. Graph methods win on multi-hop and reasoning-heavy ones and give more varied summaries (verified, "RAG vs GraphRAG", 2025).
- GraphRAG-Bench (ICLR 2026) reports GraphRAG frequently underperforms plain RAG on many real tasks, and exists to find where it does not (verified). Another 2026 paper argues vector search is "almost enough" (verified, title only).
- An older study cited in that work found 13.4 percent lower accuracy than plain RAG on Natural Questions (verified, secondhand).
- Conclusion: use the graph to add candidates and context, not to replace similarity search. Keep similarity as the base and let the graph widen it.

### Long-horizon benchmarks to use

LongMemEval (long chat histories, temporal questions), LoCoMo (long multi-session dialogue), DMR (MemGPT's original), MemoryAgentBench (retrieval, learning, conflict resolution), GraphRAG-Bench (fact, reasoning, summary, creative). All are chat-shaped. None tests a vault of plans, decisions and research. Bazaar needs its own labelled set built from real vault questions (section 7), with the public ones as a sanity check.

## 3b. Wider scan: open-source projects worth knowing (2026-11-20)

Method: web search plus a read of each repo's GitHub page (About box for licence and stars). Limits: the pages did not show last-commit dates, so "activity" below is stars, commit count and any dated news the page carried, not a verified last-commit date. LICENSE files were not opened; licences are as the About box shows them. Stars are rounded and change fast.

**AGPL check.** Bazaar is AGPL-3.0. MIT and Apache-2.0 code can be combined with it (Apache-2.0 is compatible with GPLv3 and AGPLv3). The Server Side Public License (SSPL) is not OSI-approved and is not compatible with our OSI-only rule. "Unverified" means no licence was confirmed; do not copy code from those until it is. Nearly every project below is Python, while Bazaar is .NET everywhere, so "adopt" mostly means a sidecar process or an optional module; for most, the useful thing is the idea reimplemented in C# from the paper.

Ranked by usefulness to GraphDRAG:

| # | Project | Licence | Stars, activity | Core idea | Measured results | Verdict |
|---|---|---|---|---|---|---|
| 1 | [HippoRAG 2](https://github.com/OSU-NLP-Group/HippoRAG) | MIT | 4.1k; has an MCP server | Knowledge graph plus personalised PageRank from the query's seed nodes. | Authors report strong multi-hop retrieval; indexing cost is disputed (section 3). | **Borrow the idea.** Implement PPR in C# over our CSR link graph (section 2b). |
| 2 | [Graphiti](https://github.com/getzep/graphiti) (Zep) | Apache-2.0 | 33k; 1,080 commits | Temporal context graph: facts have validity windows; hybrid semantic, keyword and graph search. | Vendor-reported LongMemEval and DMR gains (section 3). Needs Neo4j, FalkorDB or Neptune. | **Borrow the temporal model** (`valid_from`/`valid_to`). Skip as a dependency: Python, an external graph database, and a model call on every write. |
| 3 | [LLMLingua family](https://github.com/microsoft/LLMLingua) | MIT | 6.8k | Small model drops low-value prompt tokens; LLMLingua-2 uses a compact encoder. | Claims up to 20x; 2x to 5x with small loss on its benchmarks (section 4b). | **Adopt as an optional sidecar provider** for bulky prose only. Not for code, ids or decisions. |
| 4 | [Cognee](https://github.com/topoteretes/cognee) | Apache-2.0 | 33k; releases v1.6.0 and v1.6.1 in Sept 2026 | Self-hosted knowledge graph plus vector store from documents, code and chats; has a local mode that needs no LLM. | No independent numbers found. | **Borrow ideas** (no-LLM build mode, code-and-docs graph). Skip as a dependency. |
| 5 | [MemOS](https://github.com/MemTensor/MemOS) | Apache-2.0 | 12.5k; news dated Aug 2026 | Memory "operating system": inspectable graph memory, hybrid retrieval, reusable skills. Needs Neo4j and Qdrant. | Self-reported only. | **Borrow ideas** (skill reuse from memory, inspectable graph). Skip. |
| 6 | [LightRAG](https://github.com/HKUDS/LightRAG) | MIT | 40.8k; news to mid-2026 | Dual-level (entity and theme) retrieval; incremental updates. | Author-reported wins over GraphRAG; third-party indexing time is mid-pack. | **Borrow incremental update** and dual-level retrieval. Skip. |
| 7 | [Hindsight](https://github.com/vectorize-io/hindsight) | MIT | 13.3k at its ACL 2026 paper; runs on Postgres | Agent memory that separates facts, experiences and beliefs; multiple retrieval paths merged. | Vendor reports 94.6% on LongMemEval-s; note most serious systems now score in the 90s, so that benchmark barely separates them. | **Borrow ideas and read the paper.** It is the one that already targets Postgres. Not verified in depth. |
| 8 | [Agentic Context Engineering (ACE)](https://github.com/ace-agent/ace) | Unverified (paper is CC BY 4.0) | ICLR 2026 paper | An evolving "playbook" of lessons that is edited incrementally instead of rewritten, to avoid context collapse. | Paper reports gains on agent benchmarks; not re-checked. | **Borrow the idea** for consolidation (doc 08 section 7): incremental note edits, never full rewrites. Check licence before touching code. |
| 9 | Code-graph MCP servers: [codebase-memory-mcp](https://github.com/DeusData/codebase-memory-mcp), [stakgraph](https://github.com/stakwork/stakgraph), cgraphy, GitCortex | Unverified | Small, new | Tree-sitter symbol graphs for coding agents, with PageRank ranking in cgraphy. | The codebase-memory paper (arXiv 2603.27277) was not read in depth. | **Borrow ideas** for doc 17's code intelligence. Do not copy until licences are confirmed. |
| 10 | [Mem0](https://github.com/mem0ai/mem0) | Apache-2.0 | 69k | Memory layer that fuses semantic, keyword and entity signals. Search results say graph memory was removed from the open-source SDK in v3 and benchmark figures come from the managed platform (secondary, unverified). | Self-reported. | **Skip.** Our fused retrieval already covers it. |
| 11 | [Letta](https://github.com/letta-ai/letta) (ex-MemGPT) | Apache-2.0 | 25.5k | Stateful agents with self-managed memory tiers. Its README says active development has moved to `letta-code`. | No independent numbers found. | **Skip.** Our tiers already match. |
| 12 | [Microsoft GraphRAG](https://github.com/microsoft/graphrag) | MIT | 36.7k | Entity graph, communities, summaries. README says it is "largely in maintenance mode". | See section 3. | **Borrow community summaries only.** Skip the pipeline. |
| 13 | [nano-graphrag](https://github.com/gusye1234/nano-graphrag) | MIT | 4.0k; about 1,100 lines | Small readable GraphRAG. | None. | **Skip**; useful as a readable reference. |
| 14 | [FAISS](https://github.com/facebookresearch/faiss) | MIT | 41.8k | Vector search library, GPU and cuVS backends. | Section 4. | Optional; `pgvector` is the default. |
| 15 | [cuVS](https://github.com/NVIDIA/cuvs) and [cuGraph](https://github.com/rapidsai/cugraph) | Apache-2.0 | 0.9k and 2.3k | GPU vector search (CAGRA) and graph analytics. | Section 4. | **Optional NVIDIA-only provider.** Not usable on the owner's Arc B580. |
| 16 | TurboQuant open-source ports (several, community) | Mixed: some MIT, some Apache-2.0 or MIT, several with no licence shown | Small, new | Rotation plus a few bits per value (section 4b). | See section 4b. | **Reimplement from the paper in C#.** The method is short; community ports are of unknown quality. |
| 17 | [FalkorDB](https://github.com/FalkorDB/FalkorDB) | **SSPLv1** | 10.5k | Graph database on sparse matrices, OpenCypher, Redis module. | None checked. | **Skip.** Not OSI-approved. |
| 18 | [agent-memory-leaderboard](https://github.com/AML-memory/agent-memory-leaderboard) | No licence shown | 1.1k | Public leaderboard for agent memory (text, coding, multimodal). | Self-submitted scores. | **Use as a benchmark reference only;** copy no code. |
| 19 | [Awesome-Agent-Context-Compression](https://github.com/YerbaPage/Awesome-Agent-Context-Compression) | MIT | 105 | Curated paper list on compressing agent context. | None. | Reading list. |

Recent papers worth reading, found by title only and not read: "Prune the Summary, Not the History: Source-Judged Compaction for Long-Horizon Agents" (arXiv 2610.20927), "Beyond Token Savings: A Systematic Study of Context Compression in LLM Agents" (2609.32961), "Adapting Context Compression for Long-Horizon Agents with Counterfactual Continuations" (2609.36526), "When Is Graph Structure Worth Its Cost?" (2609.18099), "MEMTIER" (2605.03675), "DreamBench-SWE" (2608.20664, memory hygiene for software agents). The first two bear directly on the 50 to 80 percent goal and should be read before doc 64 is finalised.

Pattern across the field: none of these is a drop-in for Bazaar, because Bazaar's graph is authored (links in notes), isolated per agent and project, and held in Postgres and git. The ideas worth taking are small: PPR ranking, temporal validity, incremental note edits, optional compression, and quantized storage.

## 3c. Enterprise patterns for agentic GraphRAG, and the institutional knowledge map (owner-supplied source, 2026-11-20)

Source: Partha Sarkar, "A Practitioner's Guide to 6 Agentic GraphRAG Patterns" (Towards Data Science, 15 Nov 2026; the shared link's title is "Agentic GraphRAG Explained With 6 Enterprise Patterns"). Read in full. Evidence level: a practitioner article, not peer-reviewed. It reports no measurements. Its figures ("over 50 percent", "over 70 percent" fewer tokens or round-trips) are asserted, not tested, so none are used as evidence here. Its examples use Neo4j and Cypher; Bazaar would express the same traversals through the graph kernel of section 2b.

The article's central claim fits our design: the graph should not be a passive store queried once at the end of a prompt. It should be an **institutional knowledge map** (decisions, precedents, exceptions, who resolved what) that an agent navigates step by step, and that agents write back to as they work. Static GraphRAG has two named failures: broad queries explode into thousands of nodes that flood the context, and one-shot generated graph queries are brittle.

| Pattern | What it is | Bazaar equivalent and fit |
|---|---|---|
| 1. Operational memory (decision precedent) | Decisions, waivers and committee rulings become connected nodes (`APPROVED_UNDER`, `CONDITIONED_ON`, `APPROVED_BY`), so an agent finds a precedent and its conditions, not just the policy text. Needs expiry rules so outdated precedents do not persist. | Closest match to the company knowledge tier: decision notes, Council rulings and DECISIONS-LOG entries with typed edges for conditions and approver, plus `valid_to` and `Supersedes`. Strong fit. |
| 2. Execution memory | Tool calls, parameters, errors and working trajectories stored as a graph, so agents skip known failing paths. Warns that temporary glitches must not blacklist healthy tools. | Fits `memory-procedure` and failure lessons (doc 08). Sharing across agents must go through Hive grants and the sanitizer (doc 08 section 6), not a shared write; the article's "every agent learns from every agent" conflicts with our isolation rule. Count failures over time before marking a path bad. |
| 3. User context graph | A lean schema (`User - ESTABLISHES -> Constraint - APPLIES_TO -> Project`), details kept as text on the node, retrieved by relevance; superseded preferences are retired. Warns of cross-user leakage. | Fits per-agent and per-person memory. Copy the lean-schema rule (few edge types, detail in the note body) to avoid ontology sprawl. Leakage warning matches our isolation floor. |
| 4. Self-enriching topology | A system graph enriched by every resolved incident: symptom, root cause, who resolved it, who was affected; new incidents are routed to the team that fixed similar ones; a blast radius is computed by traversal. | Fits Kodo health and CI failures: link a failure to past root causes and the member that fixed them; reverse-impact traversal (section 2b) is the blast radius. A "who fixed this before" graph is personal data about members: owner-visible, Council-gated. |
| 5. Hierarchical multi-scale | Recursive community clusters (levels 0 to 3) with a summary at each level; the agent zooms in and out with a tool call and finishes on atomic facts; summaries must be refreshed when the underlying nodes change. | Matches the summary recommendation (RAPTOR, GraphRAG). Add: a `zoom_in(cluster)` tool, per-scope summaries, and summary staleness driven by reverse impact when member notes change. |
| 6. Adaptive ReAct graph loop | Reason, act, observe, one hop at a time, with backtracking, instead of one generated multi-join query. Costs sequential latency; needs hard limits and cycle detection. | Matches the bounded agent-driven traversal in section 2b. Our tools take typed edges and ids, so there is no free-form query language for the model to get wrong. |

**What this adds to the recommendations:**

1. **Typed decision edges.** Beyond `Link`, `DerivedFrom` and `Supersedes`, add a small, closed set: `ConditionedOn`, `ApprovedBy`, `Precedent` (this decision follows that one), `ResolvedBy` and `Affects`. Keep it small, with detail in the note body (pattern 3's lean-schema rule). Edge kinds are a schema choice, so this needs one answer in doc 64.
2. **Precedent lifecycle.** Every precedent and exception note gets `valid_to` or a review date, and retrieval flags expired ones instead of returning them as current.
3. **Write-back as a first-class step.** When an agent acts under a precedent, it writes a link from the new work to the precedent. This is already how notes link; make it part of the work flow so the map grows from normal work.
4. **Institutional map scope.** The map is the company tier (`company/`), built from decision, precedent, incident and role notes, with the same isolation rules: per-hop access checks, grants for any crossing, and summaries per scope.
5. **Routing by past resolution.** For Kodo and CI failures, offer "who resolved a similar failure before" as an owner-visible suggestion, never an automatic assignment, and treat the people-edges as personal data (section 5).
6. **Failure memory needs a threshold.** Record failures with counts and time; mark a path "bad" only after repeats, so a transient outage does not blacklist a tool.

## 4. GPU vector and graph search

- **cuVS / CAGRA** (NVIDIA): Meta and NVIDIA report CAGRA builds indexes up to 12.3 times faster than CPU HNSW and answers single queries up to 4.7 times faster on a 100-million-vector set; batch throughput is much higher (verified, vendor-reported). FAISS 1.10 can use it, and a CAGRA index can be converted to HNSW for CPU serving.
- **Scale check.** A vault with even a few million notes is small for this. Exact (brute-force) search over a few hundred thousand vectors is fast on a CPU with `pgvector`, and graph hops are index look-ups. GPU search pays off for millions to billions of vectors or for very high query rates. Neither is Bazaar's v1 shape.
- **Hardware check.** cuVS and FAISS-GPU target NVIDIA CUDA. The owner's PC has an Intel Arc B580 (project memory). CAGRA will not run there. The honest consequence: GPU vector search must be an optional provider module for operators with NVIDIA hardware, never a requirement. The GPU is more usefully spent on the embedding model and the reranker, which are the real compute in the retrieval path. (This conclusion rests on vendor documentation of CUDA support; the Constellation memory thread should confirm the available Arc runtimes.)

## 4b. Making it efficient: cutting the context window by 50 to 80 percent (owner goal, 2026-11-20)

The goal is to shrink the hard context an agent carries on long-horizon work by about 50 to 80 percent, with GraphDRAG filling in what is no longer carried. The first point to be clear about: **there are two different things called "efficiency", and they have different tools.**

- **Fewer tokens in the prompt** (what the model must read each turn). This is what shrinks the context window. Levers: retrieve on demand, clear stale material, compact structured bundles, summaries, prompt compression.
- **Smaller or faster storage and search** (what the retrieval system holds in memory). This is what TurboQuant, RaBitQ, PQ, binary and int8 quantization and Matryoshka embeddings do. They make the index smaller and search faster. They do **not** remove a single token from the prompt.

TurboQuant also targets the model's KV cache (the memory a model keeps for the text it has already read). That saves GPU memory on the machine running the model. It only helps if Bazaar runs the model itself. For hosted models (the Claude lane, FreeLLMAPI, direct provider keys) the provider controls the KV cache and Bazaar cannot apply it. It would matter only for local models on the server or the NPU.

### Levers that cut prompt tokens

| Lever | Measured effect | Quality cost | Applies to Bazaar |
|---|---|---|---|
| **Retrieve on demand instead of preloading** (agent asks the vault for a card or path when it needs it) | Anthropic reports context editing, which clears stale tool results, cut token use by 84 percent in a 100-turn web-search test, and the memory tool plus clearing improved task performance by 39 percent over baseline (self-reported; the 84 percent is for clearing, not for on-demand retrieval itself). No source isolates on-demand versus preload head to head. | Small when notes are written before old results are cleared. Risk: the agent forgets to ask. | Yes, the main lever. GraphDRAG is the "ask" side; the Vault is the store. |
| **Clear stale tool results and old turns** | Same 84 percent figure above. | Low if findings were saved as notes first. | Yes. Pair with a "save before clear" rule in the agent runtime (doc 06). |
| **Compact structured bundles** (paths and one-line cards instead of bodies, bodies fetched by id) | Not measured by anyone for vaults; cost is paths plus cards, usually a small fraction of bodies. Measure in section 7. | Low to none if cards are good. | Yes, already the retrieval doc's card format. |
| **Hierarchical summaries** (hub and cluster summaries, RAPTOR style) | Replaces tens of small notes with one summary for broad questions. | Loses detail; keep links to the notes underneath. | Yes, for broad questions only; per access scope (section 5). |
| **Prompt compression** (LLMLingua-2 and LongLLMLingua drop low-value tokens) | LLMLingua-2 reports 2x to 5x compression with 1.6x to 2.9x lower end-to-end latency, and about 1.6 points lower on its benchmark average at 3x (44.0 to 42.4; my reading of the table, check before quoting). Microsoft reports LongLLMLingua gaining 17.1 percent at 4x on question-aware RAG (vendor claim). An independent 2025 study found the methods help most at higher compression ratios; results vary by task. | Moderate and task-dependent; can damage exact identifiers, code and numbers. | Use on bulky prose such as old research or pasted logs, never on code, ids, decisions or anything that must be quoted exactly. Run as an optional last step. |
| **Prompt-cache-aware packing** | Cached prefixes cost less and answer faster, but still occupy the window. So this cuts cost and latency, not window size (background; not measured here). | None. | Yes. Put the stable part first (rules, Soul, constitution, pinned cards) and the changing retrieved bundle after the cache breakpoint, in a stable order, so retrieval changes do not invalidate the cache. |

Reading the numbers honestly: the 50 to 80 percent target is plausible from clearing, on-demand retrieval and compact bundles together, because one vendor reports 84 percent from clearing alone on one test. It is not proven for Bazaar, and none of these numbers are Bazaar measurements. It becomes a number only after the labelled set and a token-per-outcome measurement exist (section 7).

### Levers that shrink and speed the index

| Method | What it is | Reported numbers | Fit |
|---|---|---|---|
| **TurboQuant** (Google, ICLR 2026) | Random rotation, then a few bits per value, no training data needed; near the theoretical limit for distortion. | Paper reports quality-neutral KV cache at 3.5 bits per channel and marginal loss at 2.5 bits; secondary sources claim about 6x KV memory saving and up to 8x faster attention logits on H100 (secondary, unverified). Secondary reports say it beats PQ and RaBitQ on recall on GloVe and OpenAI embeddings (unverified). | Good candidate for embedding storage because it needs no training pass and handles streaming inserts (notes arrive all the time). Independent open implementations exist but are community code; no Bazaar-ready library was checked. Treat as a candidate to benchmark, not a decision. |
| **RaBitQ** (and Elastic's BBQ derived from it) | 1 bit per dimension with correction factors, plus rescoring on original vectors. | About 32x smaller; multi-bit variants reach 4x to 8x without rescoring; Elastic reports 2 to 5x faster queries than PQ at equal accuracy (vendor claim). | Strong, well-documented option for large indexes. Needs rescoring against full vectors. |
| **Product quantization (PQ)** | Splits vectors into pieces and codes each. | Usually at least 32x compression; recall often below 80 percent unless reranked (verified, from the RaBitQ literature). | Older baseline; use only if a library gives it for free. |
| **Binary or int8 with rescore** | Keep 1-bit or 8-bit codes, fetch extra candidates, rescore with full vectors. | Hugging Face reports about 96 percent of retrieval quality at 32x smaller and up to 32x faster with rescoring, 92.5 percent without (read from a translated page; check the English original). Naive binary needs 10 to 100 times more candidates reranked (Elastic). | Simple and portable; works on CPU. |
| **Matryoshka embeddings** | A model trained so its first N dimensions work alone; cut the vector short. | Vendors say loss is small and grows as vectors get shorter; one source says below roughly 256 dimensions quantization noise becomes risky (informal). | Pick an embedding model that supports it; combine with int8 or binary. Choose the model only after the labelled set exists (retrieval doc rule 4). |

What this means for Bazaar's size: the vault index is small next to what these methods are built for. A million notes at 1024 float32 dimensions is about 4 GB, which fits in memory. Quantization is a headroom and speed measure for later and for large operators, not a v1 requirement, and it must never replace rescoring with full vectors. A sensible order: int8 or Matryoshka plus rescore first (supported by pgvector `halfvec` and common models), then benchmark TurboQuant and RaBitQ-style codes on the real vault.

### CPU and GPU fit

- **CPU (multi-core):** binary, int8 and RaBitQ-style codes use fast bit and integer operations; the graph kernel from section 2b runs here as the reference.
- **GPU:** the embedding model, the reranker, any prompt-compression model and graph scorers. TurboQuant and RaBitQ search kernels exist mostly in NVIDIA-oriented stacks (cuVS); on the owner's Arc B580 they are CPU-side or need a custom provider (section 4).
- **Model-side KV cache quantization (TurboQuant's other use):** only for locally served models; not available for hosted models.

### Putting the pieces in order

1. Retrieve on demand with compact path-and-card bundles, with notes saved before old context is cleared. (Biggest lever, no new model.)
2. Cache-aware packing: stable prefix, then the retrieved bundle.
3. Summaries for broad questions, per access scope.
4. Optional prompt compression for bulky prose only.
5. Quantized index (int8 or Matryoshka plus rescore first; TurboQuant and RaBitQ as benchmarked options) when index size or speed becomes a measured problem.

## 5. The open question: should hops follow any cross-project link?

**Recommendation: no. Follow a link out of a project only when the reader is already allowed to read the target. Cross-project access is only through explicit shares (Hive copies and role grants). A bare link never grants reach.**

Why:

1. **Leakage through the graph itself.** Hop expansion can reveal that a note exists, how many exist, or what a summary says, even if the body is never shown. The existing retriever already drops unreadable notes before scoring, so a hidden note never appears as a hop target or a count (`constellation-memory-graph.md`, Retrieval step 3). That is the correct rule and it should be kept as the only rule.
2. **Summaries are the bigger risk.** A community or hub summary is text written from many notes. If a summary is built across a project boundary, one reader can learn from a note they cannot open. Summaries must be built and cached per access scope and never across scopes.
3. **Private memory stays private.** Doc 19 says each agent's memory is isolated and a role grant is needed to read anything else. A hop must never enter another agent's private area, whatever links point there. Links from project notes into private notes should resolve to nothing for every reader except the owning agent.
4. **Quality gain is unmeasured and probably small.** None of the systems above studied cross-boundary hops. Related projects already share what matters through Hive copies, which are ordinary in-scope notes carrying `origin_project` and `origin_note`, so they are traversed like any other note. Allowing raw cross-project hops would add candidates that are mostly unrelated and cost tokens.
5. **Doc 08 already forbids the shortcut.** Crossing projects is "a copy note, never a link target of private text" (doc 08 section 6). Following arbitrary links would reopen what that rule closes.

What to do instead, to keep most of the quality:

- Evaluate the grant at read time on each hop with the caller's identity, not at index time, so revoking a grant takes effect at once.
- When a link points at a project the reader cannot read, store it as a dangling edge and return nothing. Optionally record a counter for the owner only ("12 links to projects not shared") so the owner can decide to widen a grant. Do not tell the agent.
- Let agents propose a share (a Hive copy request) when they keep needing material from another project. The owner or Council approves it, as doc 08 already provides.
- Let the operator choose a stricter or looser mode through the configurability rule, with the strict mode as default: `graph.cross_project_hops = none | granted | linked`. `linked` should not ship in v1, because it cannot meet the isolation floor; if kept at all, it must require the owner to opt in per project pair.

## 6. Recommendations by design choice

| Choice | Recommendation | Basis |
|---|---|---|
| Graph source | Use the vault's own links (`Link`, `DerivedFrom`, `Supersedes`), keyed by `doc_id`. No language-model entity extraction in v1. | Extraction is the dominant cost in every system above; Bazaar's links are free and exact. |
| Seeds | Keep lexical plus vector fused by RRF as the base. | Similarity wins on fact look-up; graphs only add to it. |
| Expansion | One hop by default, two at most (as built). Add personalised PageRank over the same edges as an optional ranker once a labelled set exists. | HippoRAG results; cheap at query time. |
| Graph reasoning | Add typed path queries (ancestors, descendants, supersede head, k-shortest paths, reverse impact) over a CSR snapshot of the `doc_id` graph, and return a structured bundle of paths plus cards instead of flat snippets. | Section 2b; owner clarification. |
| Graph engine | One kernel interface (`IGraphKernel`): multi-core CPU reference implementation first, optional GPU provider modules (cuGraph on NVIDIA; Arc provider if measured worthwhile). Per-hop access check stays in the engine, not the provider. | Section 2b and 4; Arc B580 cannot run CUDA libraries. |
| Institutional knowledge map | Company tier holds decision, precedent, incident and role notes with a small closed set of typed edges (`ConditionedOn`, `ApprovedBy`, `Precedent`, `ResolvedBy`, `Affects`), `valid_to` on every precedent, write-back links from new work, and per-scope isolation. "Who resolved this before" is an owner-visible suggestion only. | Section 3c (Sarkar, 2026). |
| Agent-driven traversal | Optional bounded expand/inspect loop for hard questions; cap calls and tokens; same access checks. | Think-on-graph style methods (background). |
| Edge weights | Keep Link 1.0, DerivedFrom 0.8, Supersedes 0.5. Tune on the labelled set, not by hand. | No published values transfer. |
| Reranking | Add a local cross-encoder reranker over a shortlist of 25 to 50, after expansion and before packing. | Best measured gain per cost; 45 to 140 ms. |
| Time | Add `valid_from` and `valid_to` on revisions and on `Supersedes` edges. Return only currently valid notes by default; allow "as of" queries. Never delete. | Zep/Graphiti; Bazaar already keeps all revisions. |
| Summaries | Build hub and cluster summaries per access scope, in the agent's consolidation window, from the reader session, tagged with the highest input trust. Use them only for broad questions. | GraphRAG, RAPTOR; leakage rule in section 5. |
| Link creation | Let agents write links when they write notes. During consolidation, let a reader session propose extra links (A-MEM style) as suggestions that carry a lower trust label until accepted. | A-MEM; doc 08 section 7. |
| Tiering | Keep the existing working, episodic, semantic tiers. Do not add MemGPT-style self-paging. | Avoid a second system doing the same job. |
| Cross-project hops | Only through grants and Hive copies, checked per hop at read time. | Section 5. |
| GPU | Embeddings and reranker on the available GPU. Vector search stays in Postgres. cuVS/CAGRA only as an optional module for NVIDIA servers, once corpus size justifies it. | Section 4. |
| Budget | Keep "expand, then search, then pack" (retrieval doc rule 1). Report the basis and scope of each returned note. | Existing rules. |
| Context cut (50 to 80 percent target) | Deliver it with retrieve-on-demand, "save before clear" in the runtime, compact path-and-card bundles and cache-aware packing. Summaries for broad questions. Prompt compression optional and never on code, ids or decisions. Report tokens per correct answer against a full-context baseline. | Section 4b. |
| Index quantization | Not needed for v1 size. First int8 or Matryoshka plus rescore; benchmark TurboQuant and RaBitQ-style codes on the real vault. Always rescore with full vectors. | Section 4b. |
| Model KV cache quantization | Only for locally served models; out of scope for hosted models. | Section 4b. |
| Evaluation | Build a labelled set of real vault questions before choosing weights, depth or any model. Track answer quality and tokens per correct answer. Use LongMemEval and LoCoMo only as a sanity check. | Doc 08 and the retrieval doc already require this. |

## 7. Evaluation plan

1. Collect 100 to 200 real questions agents asked of the vault, plus a few written by the owner, each with the set of notes that should have been returned. Include path questions ("how does A connect to B"), impact questions ("what depends on this") and "current answer" questions.
2. Run these configurations: similarity only; plus one hop; plus two hops with PageRank; plus path bundle; plus reranker. Record recall at 5 and 10, tokens used, and latency. Run the graph kernel on CPU cores and, where available, GPU, on the real graph and a synthetic 10x graph.
3. Add temporal questions ("what did we decide before X changed") and isolation checks (an agent must never receive a note or count it cannot read).
4. Measure context directly: tokens carried per turn and tokens per correct answer on long tasks, full-context baseline versus GraphDRAG with each lever switched on in turn, so the 50 to 80 percent claim is a measured number.
5. Only then decide depth, weights, summary use and whether any language-model-built links are worth their cost.

## 8. Evidence gaps

- Almost every number above is reported by the method's authors. Few independent replications were found.
- The RAPTOR, LightRAG and HippoRAG 1 figures in this document come from background knowledge and were not re-checked against the papers.
- No study covers cross-project or access-controlled graph traversal; section 5 rests on design reasoning, not measurement.
- Public benchmarks are chat-shaped; none matches a vault of plans and decisions.
- Intel Arc support for the needed GPU runtimes was not verified.

## Sources

- Zep: a temporal knowledge graph architecture for agent memory, <https://arxiv.org/abs/2501.13956>
- A-Mem: agentic memory for LLM agents, <https://arxiv.org/abs/2502.12110>
- HippoRAG 2, <https://github.com/OSU-NLP-Group/HippoRAG>; third-party indexing-cost comparisons in GraphRAG-Bench, <https://arxiv.org/abs/2506.05690>, and RAGU, <https://arxiv.org/pdf/2607.11683>
- RAG vs GraphRAG: a systematic evaluation, <https://arxiv.org/abs/2502.11371>
- BenchmarkQED (Microsoft), <https://www.microsoft.com/en-us/research/?p=1140429>
- Accelerating GPU indexes in Faiss with NVIDIA cuVS, <https://engineering.fb.com/2025/05/08/data-infrastructure/accelerating-gpu-indexes-in-faiss-with-nvidia-cuvs/>
- TurboQuant, <https://arxiv.org/abs/2504.19874>; RaBitQ, <https://arxiv.org/html/2409.09913v1>; Elastic BBQ, <https://www.elastic.co/search-labs/blog/better-binary-quantization-lucene-elasticsearch>
- Embedding quantization (Hugging Face), <https://huggingface.co/blog/embedding-quantization>; Voyage flexible dimensions, <https://docs.voyageai.com/docs/flexible-dimensions-and-quantization>
- LLMLingua-2, <https://arxiv.org/abs/2403.12968>; prompt compression empirical study, <https://arxiv.org/pdf/2505.00019>
- Anthropic context management (context editing, memory tool), <https://www.anthropic.com/news/context-management>
- Sarkar, "A Practitioner's Guide to 6 Agentic GraphRAG Patterns", <https://towardsdatascience.com/a-practitioners-guide-to-6-agentic-graphrag-patterns/>
- Reranker latency and accuracy: <https://dev.to/gabrielanhaia/rerankers-and-the-latency-budget-when-cross-encoders-are-worth-it-3efc>, <https://redis.io/blog/top-reranking-models-rag-accuracy/>
- In-repo: `docs/design/constellation-memory-graph.md`, `19-constellation-and-git-solar-system.md` ("Agent memory governance"), `08-memory-system.md` (sections 6, 7, 8), `retrieval-and-token-efficiency.md`.
