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

## 4. GPU vector and graph search

- **cuVS / CAGRA** (NVIDIA): Meta and NVIDIA report CAGRA builds indexes up to 12.3 times faster than CPU HNSW and answers single queries up to 4.7 times faster on a 100-million-vector set; batch throughput is much higher (verified, vendor-reported). FAISS 1.10 can use it, and a CAGRA index can be converted to HNSW for CPU serving.
- **Scale check.** A vault with even a few million notes is small for this. Exact (brute-force) search over a few hundred thousand vectors is fast on a CPU with `pgvector`, and graph hops are index look-ups. GPU search pays off for millions to billions of vectors or for very high query rates. Neither is Bazaar's v1 shape.
- **Hardware check.** cuVS and FAISS-GPU target NVIDIA CUDA. The owner's PC has an Intel Arc B580 (project memory). CAGRA will not run there. The honest consequence: GPU vector search must be an optional provider module for operators with NVIDIA hardware, never a requirement. The GPU is more usefully spent on the embedding model and the reranker, which are the real compute in the retrieval path. (This conclusion rests on vendor documentation of CUDA support; the Constellation memory thread should confirm the available Arc runtimes.)

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
| Evaluation | Build a labelled set of real vault questions before choosing weights, depth or any model. Track answer quality and tokens per correct answer. Use LongMemEval and LoCoMo only as a sanity check. | Doc 08 and the retrieval doc already require this. |

## 7. Evaluation plan

1. Collect 100 to 200 real questions agents asked of the vault, plus a few written by the owner, each with the set of notes that should have been returned. Include path questions ("how does A connect to B"), impact questions ("what depends on this") and "current answer" questions.
2. Run these configurations: similarity only; plus one hop; plus two hops with PageRank; plus path bundle; plus reranker. Record recall at 5 and 10, tokens used, and latency. Run the graph kernel on CPU cores and, where available, GPU, on the real graph and a synthetic 10x graph.
3. Add temporal questions ("what did we decide before X changed") and isolation checks (an agent must never receive a note or count it cannot read).
4. Only then decide depth, weights, summary use and whether any language-model-built links are worth their cost.

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
- Reranker latency and accuracy: <https://dev.to/gabrielanhaia/rerankers-and-the-latency-budget-when-cross-encoders-are-worth-it-3efc>, <https://redis.io/blog/top-reranking-models-rag-accuracy/>
- In-repo: `docs/design/constellation-memory-graph.md`, `19-constellation-and-git-solar-system.md` ("Agent memory governance"), `08-memory-system.md` (sections 6, 7, 8), `retrieval-and-token-efficiency.md`.
