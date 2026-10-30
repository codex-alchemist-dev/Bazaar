# Graph index (Graphify-style knowledge graph, .NET)

Status: PROPOSED (2026-10-30). This is a design reference only. A first code slice (`Bazaar.Graph` and `bazaar-graph`) was built in a closed PR (#118) and is not in this repo; it is kept as a reference for extraction and caching, not as a port. A code symbol graph is a candidate, pending the doc 17 repo-map decision. Owner questions are at the end.

## Why

Agents spend most of their tokens reading whole files to answer "what calls this?" or "where is this defined?". A graph built once from the codebase and docs lets an agent ask a small question and read a few lines back. The concept comes from Graphify (https://github.com/Graphify-Labs/graphify). Its code is not copied: this is a clean-room design written from the concept (EXTRACTED vs INFERRED edges, a SHA256 incremental cache, a god-node report, and a query interface). Graphify's licence has not been verified, so no code or config is taken from it.

## Scope

In scope for the reference slice:

- Extract from C# (`.cs`) and Markdown (`.md`).
- Nodes: file, namespace (from `using`), type, member (method, constructor, property), section (Markdown heading), external (an unresolved base type).
- Edges: `contains`, `imports`, `inherits`, `calls`, `links`.
- Each edge carries a confidence tag: `EXTRACTED`, `INFERRED`, or `AMBIGUOUS`.
- A `graph.json` file that is both the output and the incremental cache.
- A query CLI: `build`, `search`, `neighbors`, `gods`.

Out of scope for now (and for this doc's status): other languages, embeddings, Obsidian or wiki export, a vector index, a watch mode, and MCP exposure. Each is a later slice.

## Parsing

| Language | Binding | Why |
|---|---|---|
| C# | `Microsoft.CodeAnalysis.CSharp` (Roslyn) | First-party, no native code, and it parses the language exactly. |
| Markdown | hand-written line scanner | Headings, `[[wiki-links]]` and relative `[text](path)` links are simple to recognise, and a parser dependency buys nothing here. |
| Other languages | Tree-sitter (.NET binding), decided in a later slice | Tree-sitter covers many languages with one API, but the .NET bindings ship native grammars, so each one needs a licence and supply-chain check first. |

Roslyn is used without a `Compilation`, so no references are needed. Extraction is per file and does not need a build. Name resolution is therefore heuristic. That is why every edge carries a confidence tag.

## Graph model

```
Node  { Id, Kind, Label, File, Line }
Edge  { Source, Target, Relation, Confidence }
File  { Path, Sha256, Nodes, Edges, Refs }     // one per source file: its cache entry
Graph { Version, Root, Files, Nodes, Edges }   // Files is the cache; Nodes and Edges are derived
```

Ids are stable and readable:

- file: `src/Foo/Bar.cs`
- type or member: `src/Foo/Bar.cs#Outer.Inner.Method` (overloads get `/2`, `/3`, ...)
- Markdown section: `docs/x.md#heading-slug`
- external type: `external:Name`

**Pending references.** The per-file extractor cannot know what a name refers to, so it records a `Ref` (source, relation, name). The builder resolves refs after every file is read.

## Resolution rules

For a ref with a name:

1. No candidate: for `inherits`, add an `external:` node and an `EXTRACTED` edge to it. For `calls` and `links`, drop the ref.
2. One candidate: `INFERRED`.
3. More than one: if one is in the same file, link only to those, `INFERRED`. Otherwise, if there are at most three, link to all of them as `AMBIGUOUS`. Otherwise drop it, because a name like `Get` would flood the graph.

A relative path link resolves to a file with `EXTRACTED` confidence only when that file exists.

## Incremental cache

`build` loads the previous `graph.json` if one exists. For each source file it computes SHA256 over the bytes. If the hash matches the cached entry, the cached fragment is reused and the file is not parsed. Resolution always runs over all fragments, since a change in one file can change what another file's names resolve to.

Deleted files drop out because the walk no longer sees them.

## Query CLI

```
bazaar-graph build [root] [--out <path>]          default out: <root>/.bazaar-graph/graph.json
bazaar-graph search <text> [--graph <path>]        nodes whose label or id contains text
bazaar-graph neighbors <id|label> [--depth N] [--graph <path>]   default depth 1, max 3
bazaar-graph gods [--top N] [--graph <path>]       highest-degree nodes (the "god nodes" report)
```

Output is plain text, one line per node or edge, so an agent can read it at a fraction of the cost of the source file:

```
src/Bazaar.Graph/Build/GraphBuilder.cs#GraphBuilder.Build  -calls->  src/Bazaar.Graph/Storage/GraphStore.cs#GraphStore.Save  [INFERRED]
```

`--graph` defaults to `.bazaar-graph/graph.json` under the current directory.

## How Bazaar agents use it

An agent runs `bazaar-graph neighbors <symbol>` before it opens a file, and reads only the lines that matter. The graph is a derived artifact. It is not committed to git, is rebuilt from the working tree, and is not a source of truth. The Vault stays the source of truth for agent memory (see CLAUDE.md, Decisions in force).

## Testing

Every behaviour change ships with a test. The reference slice's tests covered the extractors (types, members, usings, calls, inheritance, headings, wiki-links, path links), resolution (inferred, ambiguous, dropped, external), incremental reuse (an unchanged file is not reparsed; an edited file is updated), JSON round-trip, and the query commands.

## Questions for the owner

1. Tree-sitter or another parser for non-C# languages, and which grammars come first?
2. Should `graph.json` be committed for agents that have no local checkout, or always be built on demand?
3. Is a Roslyn `Compilation` (with references) worth its cost, for exact call targets instead of name resolution?
4. Should the `AMBIGUOUS` cutoff (currently three) and the dropped-name rule be configurable per repo?
