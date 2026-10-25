# Vault (Markdown in git) and Constellation (force-directed graph): technology research

Scope: as of 2026-10-24. Labels: **verified** (primary source read in this session), **secondary** (search snippet or aggregator, not the primary page), **unverified** (no usable source found, or inference). Inferences are marked as such. Decided items (Markdown in git as source of truth, PostgreSQL index, pg_search on Linux with Postgres FTS on Windows, Avalonia with Skia, native rendering not D3/WebGPU, reverse-gravity layout, Vim navigation) are taken as given. Tool calls used: about 17.

---

## 1. Markdown parsing and Obsidian-compatible round-tripping

### Takeaway
Use Markdig (BSD-2-Clause, verified) as the parser. It gives a source-positioned AST and lossless trivia round-trip, and it handles YAML front matter and task lists. Its core does not appear to ship Obsidian `[[wiki-link]]` support, so Bazaar should write a small inline extension for wiki-links rather than depend on a third-party package whose licence and maintenance are unverified.

### Cited Findings
- Markdig is released under BSD-2-Clause ("This software is released under the BSD-Clause 2 license") — [Markdig GitHub](https://github.com/xoofx/markdig) — **verified** (primary page read).
- Markdig targets "NETStandard 2.0, NETStandard 2.1, NETCoreApp 2.1 and NETCoreApp 3.1" as of 0.20.0+, and "To build from source, it requires .NET 10.0." — [Markdig GitHub](https://github.com/xoofx/markdig) — **verified**. Note: the page text is internally inconsistent about the latest target, so confirm the NuGet package targets before pinning.
- Markdig provides an AST "with precise source code location for syntax tree, useful when building a Markdown editor" — [Markdig GitHub](https://github.com/xoofx/markdig) — **verified**.
- Markdig parses "trivia (whitespace, newlines and other characters) to support lossless parse ⭢ render roundtrip" — [Markdig GitHub](https://github.com/xoofx/markdig) — **verified** (the linked Roundtrip.md was not read).
- YAML front matter is "parsed without being evaluated, and removed from HTML output"; task lists are supported — [Markdig GitHub](https://github.com/xoofx/markdig) — **verified**. Front matter is kept as raw text by Markdig, so Bazaar must parse YAML itself (inference).
- Markdig passes "more than 600+ tests from the latest CommonMark specs (0.31.2)" — [Markdig GitHub](https://github.com/xoofx/markdig) — **verified** (the page's claim; CommonMark 0.31.2 was not checked independently).
- Markdig's NuGet listing shows version 1.3.2 and describes it as "CommonMark compliant, extensible" — [Markdig on NuGet](https://www.nuget.org/packages/markdig) — **secondary** (search snippet only).
- The Markdig GitHub page does not list a wiki-link extension. A search for a Markdig wiki-link package found only third-party options: `bwets.Markdig.Extensions` 0.1.4 (bundles Obsidian wiki links plus admonitions, Mermaid, syntax highlighting) and `MfGames.Markdown` 3.0.0 (published 2025-12-16, wiki links to HTML and Gemtext). Their licences and maintenance were not checked — [bwets on NuGet](https://www.nuget.org/packages/bwets.Markdig.Extensions); [MfGames registry](https://src.mfgames.com/mfgames-cil/-/packages/nuget/mfgames.markdown) — **secondary / unverified**.
- Obsidian link syntax, verified from the Obsidian help page (now at obsidian.md/help/links):
  - Basic link `[[Three laws of motion]]`; folder paths from vault root with forward slashes; non-Markdown files need an extension, e.g. `[[Figure 1.png]]`.
  - Display text: `[[Example|Custom name]]`. The page says "Aliases are a separate feature for reusing alternate names across the vault."
  - Heading links `[[About Obsidian#Links are first-class citizens]]`; same-note heading `[[#...]]`.
  - Block links `[[2023-01-01#^37066d]]`; identifiers are "Latin letters, numbers, and dashes"; structured blocks need the identifier on its own line. The page warns "Block references are specific to Obsidian and not part of the standard Markdown format."
  - Rename: "Obsidian can automatically update internal links in your vault when you rename a file" (toggle: Settings → Files and links → Automatically update internal links).
  - Unresolved links: linking a missing note creates it at the path in the link. Characters `# | ^ : %% [[ ]]` in link text "may not work as a link."
  — [Obsidian Help: Links](https://obsidian.md/help/links) — **verified**.

### Inferences
- Obsidian-compatible round-tripping needs two rules: (a) keep the raw source text of every block and only rewrite spans that changed (use Markdig source positions, not re-rendering), and (b) keep `^block-id` lines and front matter byte-identical unless the user or agent edits them. Markdig's trivia support helps but was not tested here.
- Bazaar should maintain its own link resolver: resolve `[[target]]` by note path, then by title or alias, then by heading or block anchor. Ambiguous titles need a deterministic tie-break (path-first, as Obsidian's folder-path links suggest). Renames should go through a Bazaar "move" command that rewrites inbound links in the same commit, because git has no rename-aware link model (inference).
- Blocks and headings are addressable by Markdig's AST position plus a generated stable ID written back into the file; without written IDs, heading links break on renames.

### Gaps
- Did not verify the licence and maintenance of the wiki-link NuGet packages.
- Did not verify Markdig's latest release date (the GitHub release list was empty in the fetch).
- Did not read Obsidian's or Logseq's own file-format internals beyond the help page; Foam and Logseq link models were not researched.

---

## 2. Git as the store: LibGit2Sharp, multi-agent writes, Markdown conflicts

### Takeaway
Keep one writer service that owns the repo's write path. Give each agent its own branch and worktree for drafting, merge through the writer, and use git's merge attributes to make Markdown merges predictable. Do not use the `union` driver for prose.

### Cited Findings
- LibGit2Sharp is MIT-licensed and wraps libgit2 ("a native Git implementation") — [LibGit2Sharp GitHub](https://github.com/libgit2/libgit2sharp) — **verified** (page read). The page did not state the supported libgit2 version, the latest release, worktree support, merge API coverage, or thread-safety rules — **gap**.
- Git will not check out the same branch in two worktrees; one worktree per branch — [Git worktrees for parallel agents (vendor blog, Verdent)](https://www.verdent.ai/guides/review-merge-conflicts-parallel-ai-agents) — **secondary** (practitioner/vendor source). The git-scm manual is the authoritative reference and was not fetched.
- Practitioner sources agree that worktrees give each agent a directory and branch on a shared object store, and that conflicts move to merge time — [Augment Code guide](https://augmentcode.com/guides/git-worktrees-parallel-ai-agent-execution); [OpenReplay](https://blog.openreplay.com/git-worktrees-ai-agents/) — **secondary**, vendor-tier evidence.
- Gitignored files do not transfer when syncing a worktree back, so builds need re-verification after sync — [Verdent/mindstudio-type guides, search snippet](https://www.verdent.ai/guides/review-merge-conflicts-parallel-ai-agents) — **secondary**.
- Git merge attributes, verified from git-scm.com/docs/gitattributes:
  - `merge` attribute "Set" = built-in 3-way text merge; "Unset" = take current branch and mark conflicted; `merge=<driver>` = custom driver.
  - Built-in `union` driver: "take lines from both versions, instead of leaving conflict markers. This tends to leave the added lines in the resulting file in random order and the user should verify the result. Do not use this if you do not understand the implications."
  - Custom drivers are defined in config (`[merge "name"] driver = ...`) and must write the result to `%A` and exit zero on clean merge.
  — [git-scm: gitattributes](https://git-scm.com/docs/gitattributes) — **verified**.
- Jujutsu (jj) records conflicts as first-class objects in commits: "conflicts are objects in the model", and descendants can be rebased even when they conflict — [jj conflicts doc (mirror)](https://git.joshthomas.dev/mirrors/jj/src/commit/8ef00a34983334dfd5039c5961582c502967d9a1/docs/conflicts.md) — **secondary** (mirror of primary docs, not the official site). Concurrent multi-workspace behaviour was not found — **gap**.
- Dolt is a MySQL-compatible SQL database with Git-style branching, merging and diffing, stored as Prolly Trees; licence Apache-2.0 per several secondary sources — [dev.co Dolt page](https://dev.co/databases/open-source/dolt); [DoltHub docs](https://docs.dolthub.com/concepts/dolt) — **secondary**. Dolt would replace Markdown-in-git as the source of truth, which is DECIDED against, so it is noted only as a reference point.

### Inferences
- Recommended write path (inference): agents draft in their own worktree/branch (`agent/<task>`, as CLAUDE.md requires for writers); the writer service serialises merges into the Vault branch with a queue; each commit's author is the agent's GitHub App identity (the git author field), with the GC and task ID in the commit trailer.
- Non-overlapping edits to different documents merge cleanly under git's default text merge, so "last writer wins" is only needed at the file level for files two agents both edit. Recommended policy: refuse the second write with a conflict to the owner GC instead of silently overwriting (inference, consistent with the append-only GC rule).
- For append-only artefacts (e.g. a decision log, or the Constellation's event index) an attribute such as `merge=union` could be safe, but only if the file is verified to be line-appended. For Markdown prose, `merge=text` with a conflict surfaced to a GC is the safer default (inference).
- Performance with thousands of files: LibGit2Sharp and the git CLI both read the index and object store; a scale test was not run. Unverified.
- Git's own merge driver semantics mean Bazaar can keep the stock text merge and add a custom driver for front matter (YAML) if merges there prove noisy (inference).

### Gaps
- LibGit2Sharp version, worktree and merge coverage, and thread-safety rules were not confirmed from primary sources. Check the NuGet package page and the repo's CHANGES.md before committing to it. The git CLI may be simpler for worktrees (inference).
- No primary-source evidence was found on how jj or Dolt behave under many concurrent writers.
- No benchmark numbers for thousands of files in git or LibGit2Sharp were found.

---

## 3. PostgreSQL index: documents, links, backlinks, tags, hybrid search

### Takeaway
A plain relational schema (documents, links, tags, headings/blocks, embeddings) indexed on commit is enough. pgvector 0.8.7 provides HNSW and IVFFlat; hybrid search means full-text or BM25 plus vector, fused with Reciprocal Rank Fusion.

### Cited Findings
- pgvector: indexed `vector` supports up to 2,000 dimensions and indexed `halfvec` up to 4,000 dimensions; stored vectors up to 16,000 for both types. HNSW and IVFFlat are both supported — [pgvector GitHub](https://github.com/pgvector/pgvector) — **verified** (page read; the licence text link was not followed, so the licence is unconfirmed here). The page's install instructions reference v0.8.7.
- The pgvector page says to "Use together with Postgres full-text search for hybrid search" and suggests "Reciprocal Rank Fusion" or a cross-encoder to combine results — [pgvector GitHub](https://github.com/pgvector/pgvector) — **verified**.
- pg_search is AGPL-3.0 (ParadeDB has used AGPL-3.0 since its start, per the ParadeDB blog; PGXN lists AGPL 3) — [PGXN pg_search](https://pgxn.org/dist/pg_search); [ParadeDB AGPL blog](https://paradedb.com/blog/agpl) — **secondary**. A commercial licence is offered, per PGXN.
- pg_search prebuilt extensions are listed for Debian, Ubuntu, RHEL and macOS, with PostgreSQL 14+; no Windows build was listed — [PGXN pg_search 0.21.9](https://pgxn.org/dist/pg_search/0.21.9/) — **secondary**. This matches the DECIDED Windows fallback to Postgres FTS plus pgvector.
- Obsidian and Logseq graph models: the Obsidian help page confirms that links are the graph edges (see Section 1). Logseq and Foam models were not read — **gap**.

### Inferences
- Suggested tables (inference): `documents(id, path, title, frontmatter jsonb, commit_sha, updated_at)`; `links(src_doc, src_anchor, target_raw, target_doc NULL-if-unresolved, target_anchor, alias, kind)`; `backlinks` as a view over `links`; `tags(doc_id, tag)`; `blocks(doc_id, block_id, start_line, end_line, text)`; `chunks(doc_id, block_id, embedding halfvec(N), tsv)`.
- Unresolved links stay as rows with `target_doc NULL` so they resolve when the target appears (matches Obsidian's create-on-link behaviour).
- Incremental indexing on commit: diff the commit (`git diff --name-status old..new`), re-parse only changed files, delete and re-insert their rows in one transaction, then re-resolve links whose `target_raw` matches a new or renamed title. Re-embed only changed blocks (inference).
- Hybrid query: run a lexical query (pg_search BM25 on Linux, `ts_rank` on Windows) and a vector query (pgvector HNSW), then fuse with RRF. Using `halfvec` keeps index size down if dimensions exceed 2,000 (inference).
- Keep the Constellation graph as a query over `links` plus a cached adjacency list in the client, not a separate graph database (inference; the Constellation doc says PostgreSQL is the index).

### Gaps
- Did not measure index build or query times at thousands of documents.
- Did not verify whether pgvector installs on Windows PostgreSQL in the current Windows build. Unverified.

---

## 4. Force-directed layout: algorithms, libraries, performance

### Takeaway
ForceAtlas2 (Jacomy et al., 2014) is the best-documented CPU layout for graphs of this size: degree-dependent repulsion, linear attraction, optional LinLog, adaptive speed, and optional Barnes-Hut. The paper's own benchmarks put it well below 100,000 nodes. A simple CPU implementation with Barnes-Hut is a reasonable starting point for 1k–10k nodes; a GPU path is a later optimisation. I found no .NET ForceAtlas2 library in this session.

### Cited Findings
- ForceAtlas2 (PLOS ONE 9(6): e98679, 2014), verified from the paper text:
  - Repulsion "is proportional to the produce of the degrees plus one" of the two nodes, so hubs repel less than leaves; attraction "depends linearly on the distance"; "Strong gravity" pulls compactly.
  - LinLog mode "just uses a logarithmic attraction force," giving tighter clusters.
  - Adaptive speed: local speed lowered by "swinging"; global speed is "adaptive heating" rather than cooling.
  - Barnes-Hut: "we implemented the optimization of Barnes Hut"; it "may be counter-productive on small networks."
  - Benchmarks (68 networks, 5 to 23,133 nodes; average quasi-optimal time at 90% of max quality): Yifan Hu 333 ms, ForceAtlas2 638 ms, ForceAtlas2 LinLog 1,184 ms, Fruchterman-Reingold 20,201 ms. Quick-and-dirty (50%): ForceAtlas2 68 ms, Fruchterman-Reingold 7,853 ms.
  - "is not adapted to networks bigger than 100,000 nodes."
  — [Jacomy et al., PLOS ONE](https://journals.plos.org/plosone/article?id=10.1371/journal.pone.0098679) — **verified** (page read, equations not legible in the fetch). Benchmark hardware is not stated in the fetched text and the figures are from 2014 CPUs.
- Louvain community detection (Blondel et al., 2008) optimises modularity in two phases (local moving, then aggregation) and reports faster runtime than the other methods they compared — [UCLouvain record](https://research.dial.uclouvain.be/entities/publication/18449700-2340-48c7-866c-37686912bcd2); [NetworkX louvain_communities](https://networkx.org/documentation/latest/reference/algorithms/generated/networkx.algorithms.community.louvain.louvain_communities.html) — **secondary** (abstract and docs, not the full paper).
- Cosmograph / Cosmos computes force layouts on the GPU in WebGL fragment and vertex shaders. Its docs say most CPU layouts "choke at around 100,000 nodes" and that real-time simulation works for "hundreds of thousands" of nodes. These are vendor claims with no independent benchmark. Its WebGL dependence is noted as a known issue on iOS 15.4+ — [Cosmograph concept docs](https://cosmograph.app/docs-general/concept/); [Cosmos on GitHub, search snippet](https://github.com/supernotes/cosmos) — **secondary, vendor claim**. Cosmos is a web library, so it cannot be used in the native Skia client; it is a design reference only.
- Avalonia: override `Render` and call `InvalidateVisual` for custom drawing; implement `ICustomDrawOperation` for direct SkiaSharp canvas access, which the docs suggest for "complex charts, 3D rendering, or game graphics"; the performance guide recommends a custom hit-test for scenes with many objects — [Avalonia custom rendering docs](https://docs.avaloniaui.net/docs/graphics-animation/custom-rendering); [Avalonia performance docs](https://docs.avaloniaui.net/docs/app-development/performance) — **secondary** (search snippets of primary docs; not read in full).
- One community report says a Skia-based custom renderer in Avalonia was "average" with large point data; this is anecdotal — [NoesisGUI forum](https://www.noesisengine.com/forums/viewtopic.php?p=18246) — **secondary, anecdotal**.

### Inferences
- Layout placement for 1k–10k nodes: a CPU ForceAtlas2-style loop with Barnes-Hut (quadtree/octree) and a fixed-tick budget per frame should fit a 16 ms frame at 10k nodes, but this is an estimate, not a measurement. Run the layout on a background thread, double-buffer positions, and render from the last completed snapshot (inference from Avalonia's threading model and the Cosmos design).
- Level of detail: draw edges as batched geometry, render node labels only above a zoom threshold, cluster nodes into Louvain communities and draw one glyph per community when zoomed out (semantic zoom). None of these were benchmarked (inference).
- Reverse gravity: implement it as a per-frame change to the target: pin the focused node at the layout centre, scale attraction towards it by hop distance, and warm-start the simulation from current positions so changes animate instead of jumping. Each focus change is then an incremental re-layout, not a full recompute (inference; this is a design mapping, not a published technique).
- A GPU path (compute shaders or Skia-independent compute) is only needed if measured CPU cost exceeds budget at 10k nodes. Defer until a spike measures it.
- 3D later: the layout produces positions in R^n. ForceAtlas2 and Barnes-Hut generalise to 3D octrees, so the physics model can stay renderer-agnostic as the Constellation doc requires (inference).

### Gaps
- No .NET ForceAtlas2, Barnes-Hut or d3-force port was verified. Search for "d3-force .NET port" was not run; treat any library as unknown until checked.
- No measured frame-time numbers in Avalonia/Skia for 1k–10k nodes were found. The 60 fps target is unverified.
- Louvain was not read from the full paper; the complexity claims are secondary.
- Obsidian's graph view internals (its layout engine) were not verified.

---

## 5. Existing graph viewers worth learning from

### Takeaway
Cosmograph/Cosmos is the strongest documented example of "layout on the GPU, draw everything in one pass"; Obsidian's graph view is the closest UX reference for a notes-first graph. Sigma.js and Graphology were not read and are unverified here.

### Cited Findings
- Cosmograph's concept page: layout on GPU, drawing in shaders, avoiding random memory access. "The traditional many-body repulsion approach doesn't speed up on GPUs" according to the docs — [Cosmograph concept docs](https://cosmograph.app/docs-general/concept/) — **secondary, vendor source**.
- Cosmos space-size setting: smaller space = faster; large graphs may not fit; graphs with several million nodes "may not fit at all" — [Cosmograph concept docs](https://cosmograph.app/docs-general/concept/) — **secondary**.
- Obsidian links and block/heading addressing: see Section 1 — **verified**.

### Inferences
- The Obsidian graph is link-based and uses one node per note; Bazaar's Constellation should mirror that for docs, and add node types for decisions, PRs and research (inference).

### Gaps
- Did not read Sigma.js, Graphology, Logseq or Obsidian graph-view implementation pages. Their performance and layout claims are **unverified**.

---

## 6. Git visualisation ("Git Solar System")

### Takeaway
The data needed is the commit DAG (parents per commit), refs (branches, tags, HEAD), and merge commits. LibGit2Sharp or the git CLI can produce it, but I did not verify either against a graph-walk workload.

### Cited Findings
- Git ref and worktree concepts were covered by the git-scm and vendor sources in Section 2; the commit parent structure is standard git (inference from git's object model, not separately sourced).
- No primary source on git-graph rendering libraries was read in this session.

### Inferences
- Solar System layout: branches as orbits around the default branch, commits as bodies, merges as comet-like edges. This is a force layout over the commit DAG with fixed parent edges; it can reuse the Section 4 engine with stronger attraction to parents and weaker repulsion (inference).
- Data needed per commit: sha, parents, author (agent identity), timestamp, touched paths, and GC or task trailer. Pre-index these in PostgreSQL on commit to avoid repeated git walks (inference).

### Gaps
- No libraries, benchmarks or performance numbers verified for git graph rendering.

---

## Recommendations (summary for the report writer)

1. **Parser:** Markdig for parsing and AST positions; write a small Obsidian wiki-link inline extension (aliases, heading and block anchors) rather than adopting an unverified third-party package. Treat block IDs and front matter as write-back-safe.
2. **Git write path:** one writer service with a per-document write queue; agents on `agent/<task>` branches; commits authored by the GitHub App identity; conflicts on the same file go to a GC, not a silent overwrite. Use `merge=text` for Markdown; reserve `union` for verified append-only files. Verify LibGit2Sharp coverage before adopting it, or plan on the git CLI for worktrees.
3. **Index:** relational tables for documents, links (with unresolved rows), tags, blocks and embeddings; incremental indexing from `git diff` on each commit; hybrid search via pg_search or Postgres FTS plus pgvector (halfvec, HNSW), fused with RRF.
4. **Constellation layout:** own CPU force engine with ForceAtlas2-style forces and Barnes-Hut, background thread, double-buffered positions, Skia drawing through `ICustomDrawOperation`, custom hit testing. Louvain for clusters. Defer GPU work until a 10k-node spike measures a miss. Reverse gravity as pinned focus plus warm-started incremental layout.
5. **Spike needed:** a 10k-node Avalonia/Skia benchmark for the 60 fps target; a LibGit2Sharp worktree and merge test; a Markdig round-trip test on real Obsidian-style Markdown.

## Source list (primary reads marked)
- Markdig: https://github.com/xoofx/markdig (verified)
- LibGit2Sharp: https://github.com/libgit2/libgit2sharp (verified; limited content)
- git gitattributes: https://git-scm.com/docs/gitattributes (verified)
- Obsidian links: https://obsidian.md/help/links (verified; old help.obsidian.md redirects here)
- pgvector: https://github.com/pgvector/pgvector (verified)
- ForceAtlas2 paper: https://journals.plos.org/plosone/article?id=10.1371/journal.pone.0098679 (verified)
- Louvain: https://research.dial.uclouvain.be/entities/publication/18449700-2340-48c7-866c-37686912bcd2 (secondary)
- pg_search licence: https://pgxn.org/dist/pg_search (secondary); https://paradedb.com/blog/agpl (secondary)
- Cosmograph: https://cosmograph.app/docs-general/concept/ (secondary, vendor)
- Avalonia rendering: https://docs.avaloniaui.net/docs/graphics-animation/custom-rendering (secondary, snippet)
- Dolt: https://docs.dolthub.com/concepts/dolt (secondary)
- jj conflicts: https://git.joshthomas.dev/mirrors/jj/src/commit/8ef00a34983334dfd5039c5961582c502967d9a1/docs/conflicts.md (secondary, mirror)

## What I could not verify
- Current Markdig and LibGit2Sharp release versions and dates, and LibGit2Sharp worktree/merge/thread-safety behaviour.
- Whether any Markdig wiki-link package is maintained or what its licence is.
- The ForceAtlas2 paper's equations (PLOS page rendered without them); the ForceAtlas2 PDF link returned 404.
- Any .NET port of ForceAtlas2, d3-force or Barnes-Hut.
- Frame-rate or build-time numbers for Avalonia/Skia graph rendering or for git or pgvector at scale.
- Logseq, Foam, Sigma.js and Graphology internals.
- Jujutsu multi-workspace concurrency and Dolt concurrency behaviour.
- pgvector and pg_search Windows status (pg_search: no Windows build listed; pgvector: not checked).
- Git Solar System library options.
