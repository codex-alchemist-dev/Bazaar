# The Constellation and Git Solar System

Status: DRAFT for owner review (2026-10-25) · Owner questions: 10
Depends on: Bazaar.Constellation.md, Bazaar.Server.md (Vault index, GC access), Bazaar.Windows.md (rendering host, theme) · Used by: Bazaar.Windows.md, Bazaar.Linux.md, Bazaar.Android.md

## Purpose
The Constellation is the visual view of the knowledge graph that the Vault and GCs form. The Git Solar System applies the same renderer to commit history and Vault branches. Both show what connects to what, and what changed while away.

## Decided inputs
- The Constellation and the Vault are two views of one graph, not two systems (Bazaar.Constellation.md §1).
- Reverse Gravity Layout: the focused document is the centre and everything else orbits it (§1).
- Vim-style navigation triggers real restructuring of the graph, not only camera moves (§1).
- Native Skia rendering through Avalonia; no D3-Force, WebGPU or Win2D. A 3D renderer may follow on the same data (§1).
- Git Solar System: branches as planets, commits as bodies, merges as comets; extended to Vault branching (§2).
- Vault: Markdown in git is the source of truth; PostgreSQL indexes it; `[[wiki-links]]` (§3).
- The server does all compute; clients render (CLAUDE.md, decisions in force).
- GCs are append-only. DMs are two-member GCs that the owner can read (Bazaar.md §2).
- One theme file drives Avalonia and Monaco. Navigation uses a mnemonic-opened palette with no Ctrl chords (Bazaar.md §UI).
- Avalonia.Headless is the UI test path (CLAUDE.md).
- Owner direction, not yet in Bazaar.Constellation.md: the Constellation also holds drafts and links internal docs and plan files (Conflicts 4).

## Design
- **Graph service (server, [PROPOSED]).** Builds a per-viewer projection from the Vault index (`documents`, `links`), GC references, Kanban tasks, GitHub PR data and the git DAG. Emits a versioned snapshot, then deltas.
- **Layout engine (server library, [PROPOSED]).** ForceAtlas2-style forces with Barnes-Hut (quadtree in 2D, octree-ready for 3D). No Avalonia dependency. Runs in fixed-budget ticks per view.
- **Clustering (server, [PROPOSED]).** Louvain communities, recomputed when the graph version changes, not per frame.
- **Client renderer (Bazaar.Client.Core, [PROPOSED]).** A custom Avalonia control that draws through `ICustomDrawOperation` to Skia (secondary source). It interpolates between received position frames, and owns camera, hit-testing and level of detail. It runs no simulation.
- **Reverse gravity ([PROPOSED]).** On focus change, the server pins the focused node at the origin, gives each node a target ring radius by hop distance, and warm-starts from current positions so the change animates. The ring mechanism is an inference from the research, which names only attraction scaling.
- **Commands ([PROPOSED]).** Navigation commands send a `FocusRequest` to the server. Keys are mnemonic, never Ctrl chords.

| Zoom tier | Draws |
|---|---|
| Far | Cluster glyphs and cluster-to-cluster edges |
| Mid | Node shape and type colour, labels for the focused neighbourhood |
| Near | All labels, edge kinds, hover preview (plain text, see Conflicts 5) |

## Data and interfaces
| Node kind | Key | Source |
|---|---|---|
| doc, research, draft | Vault path | Vault index |
| decision | decision ID | GC record and Vault |
| pr | owner/repo#n | GitHub App webhook |
| task | task ID | Kanban |
| gc | GC ID | GC log (DM nodes: members only) |
| screenshot | asset ID | Asset store |
| commit, branch | sha, ref | Git DAG index |

Edge kinds: `links_to` (wiki-link), `backlink` (derived), `decides`, `implements` (PR to task), `mentions` (GC to node), `derived_from`, `parent`, `merges`.

```csharp
public sealed record GraphSnapshot(long GraphVersion, IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges);
public sealed record LayoutFrame(long GraphVersion, long LayoutSeq, string FocusId, ReadOnlyMemory<float> Xy); // Xy: packed x,y per node index
public sealed record FocusRequest(string ViewId, string NodeId);
```

Git DAG index (per commit on the writer path): sha, parents, author identity, timestamp, refs, touched paths, GC and task trailers, merge flag.

## Behaviour and edge cases
- **Ordering.** Frames carry `GraphVersion` and `LayoutSeq`. The client drops any frame older than the newest applied.
- **Node additions.** New nodes start at a neighbour's position (warm start), so nothing jumps.
- **Reconnect.** The client requests a snapshot and re-seeds from its last frame.
- **Limits.** Above a node cap (PROPOSED 10,000 visible), the view shows clusters only.
- **Hit-testing.** A uniform spatial grid rebuilt per frame. No linear scan.
- **Visibility.** The projection is filtered per viewer. DM bodies never enter the graph. DM titles appear only to members.
- **Stale data.** When disconnected, the last snapshot is shown and marked stale.
- **Concurrency.** One layout engine per view. Concurrent layouts are capped.

## Performance budgets (all [PROPOSED] until spiked)
| Target | Budget |
|---|---|
| Client render, 5k nodes, Windows | 60 fps, ≤ 10 ms draw |
| Client render, 10k nodes | 60 fps (stretch; see Question 2) |
| Server layout tick, 10k nodes | ≤ 8 ms per tick on the NUC |
| Focus change to settled layout, 5k nodes | ≤ 1 s |
| Android | not gated until the spike |

No measured frame times exist for Avalonia/Skia graphs at these sizes (research gap).

## Git Solar System
- Branches are orbits around the default branch. Commits are bodies on their branch's orbit, ordered by time.
- Merge commits draw as comets from the merged branch to its target.
- Vault branches (`agent/<task>`) are planets. Writer-queue merges are comets.
- The layout is deterministic, not force-based ([PROPOSED]), so history does not jitter as it grows.

## Testing
- **Layout engine:** xUnit v3, no Avalonia. Fixed seed. Invariants: no NaN, settling, hop ordering, determinism. Barnes-Hut matches brute force within tolerance at 1k nodes.
- **Clustering:** two cliques joined by one edge yield two communities.
- **Projection:** real PostgreSQL fixture. Unresolved links kept. Non-members never see DM nodes.
- **Git DAG:** fixture repo with a merge yields one comet edge.
- **Client:** Avalonia.Headless with Skia. Golden image at 200 nodes. Hit-test and keyboard-only walk produce the expected `FocusRequest` sequence.
- **Performance:** benchmark at 1k, 5k and 10k on the NUC, nightly, not a PR gate.

## Work packages
1. `Bazaar.Contracts`: graph and layout records, JSON source generation.
2. SQL projection views over `documents`, `links`, GC, task and PR tables, with visibility filter. Depends on 1.
3. Layout core: ForceAtlas2 forces, quadtree Barnes-Hut, tick API. No dependencies.
4. Reverse gravity: hop rings and warm start. Depends on 3.
5. Louvain clustering. Depends on 2.
6. Git DAG indexer. Depends on 2.
7. Client renderer: custom control, LOD, interpolation. Depends on 1, 3.
8. Spatial grid hit-testing. Depends on 7.
9. Navigation command model and keymap. Depends on 7.
10. Accessible list mirror. Depends on 7.
11. Git Solar System orbit layout and comet rendering. Depends on 6, 7.
12. Benchmark spike at 5k and 10k on Windows and Android. Depends on 7.

## Conflicts
1. **Compute location.** CLAUDE.md says the server does all compute. The research recommends a client background-thread layout. This doc follows CLAUDE.md. Client-side layout would be an exception that needs explicit owner approval.
2. **Spike target.** The owner's direction asks for 10k nodes at 60 fps. The implementation plan (Wave 4) schedules a 5k-node spike. No source measured either.
3. **Git layout.** The research suggests force layout over the commit DAG. This doc proposes deterministic orbits, a departure from the research.
4. **Drafts.** Drafts are not in Bazaar.Constellation.md. Drafts may be private, so they need the same visibility rule as DMs.
5. **Licence.** Avalonia Pro controls (Markdown Viewer, Rich Text Editor) carry per-seat fees and seat limits. Node previews use plain Skia text to avoid them. NativeWebView needs Accelerate, which this design does not use.
6. **Naming.** Bazaar.md's glossary still says "Mood Board". Bazaar.Constellation.md is the source of the rename.
7. **Unverified.** `ICustomDrawOperation` usage and Louvain are secondary-sourced. Accessibility beyond Linux AT-SPI2 (UIA, TalkBack) is unverified.

## Questions for the owner
1. **Layout location.** Server (recommended: matches "server does all compute") or client-local (faster focus response, breaks the rule)?
2. **Performance gate.** 5k or 10k at 60 fps? Default: 5k must pass on the Windows NUC desktop, 10k is a stretch.
3. **Drafts.** Are drafts visible to all viewers or only the owner and author? Default: owner and author.
4. **DMs in the graph.** Default: DM nodes appear only for members, and the owner sees all, matching the DM rule.
5. **Git layout.** Deterministic orbits or force layout? Default: deterministic.
6. **Orbit radius.** By commit age or equal spacing? Default: by age.
7. **Node cap.** Default 10,000 visible before cluster-only view.
8. **Reverse gravity depth.** Default: one ring per hop, three rings shown, the rest clustered.
9. **Accessibility.** Is the keyboard list mirror required for v1? Default: yes.
10. **Screenshots.** Node source: GC attachments only, or test artifacts too? Default: GC attachments only.
