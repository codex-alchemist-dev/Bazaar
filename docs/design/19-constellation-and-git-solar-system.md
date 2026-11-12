# The Constellation and Git Solar System

Status: RECONCILED with DECISIONS-LOG (2026-10-25), cross-doc pass done · Owner questions: 4
Depends on: 01-gc-data-model.md, 02-wire-protocol-and-sync.md, 04-postgres-schema-and-migrations.md, 08-memory-system.md, 12-kanban-and-dependencies.md, 17-coding-agent-harness.md, 18-vault.md, 21-github-integration.md, 27-client-architecture.md, 35-spikes-plan.md · Used by: 28-windows-client.md, 29-android-client.md, 30-linux-client-and-packaging.md

## Purpose
The Constellation is the visual view of the knowledge graph that the Vault and GCs form. The Git Solar System applies the same renderer to commit history and Vault branches. Both show what connects to what, and what changed while away. The design source is the deep-dive `Bazaar.Constellation.md`; this doc turns it into buildable parts.

## Decided inputs
- **One surface.** The Constellation is the whole knowledge workspace, the Obsidian and Milanote replacement, covering the Vault, the canvas, agent memory and links (owner, 2026-11-01). It renders on Vulkan across clients, over one knowledge graph (Bazaar.Constellation.md §1; DECISIONS-LOG round 11, C38). The Vault holds almost every durable written artifact, including each agent's memory notes, so document nodes come from the Vault and need no second store (18).
- **Vault access applies to the graph.** Every read, search hit and link resolution is checked against the caller's principal (18, Access). The owner reads all. An agent reads its own private memory. Other agents do not read private memory. The projection uses the same filter (C38, 18).
- **Compute location.** Layout and clustering run on the server; clients render and run no simulation. This follows CLAUDE.md, whose listed exceptions (on-device inference, Studio relay, local terminal, branch sync) do not include the graph (DECISIONS-LOG round 4, C17).
- **Licence.** The public build is OSI-only, with no paid Avalonia controls (DECISIONS-LOG round 4, C23). Node previews use plain Skia text.
- **Renderer.** Reverse Gravity Layout; Vim-style navigation that restructures the graph; no D3-Force or WebGPU (Bazaar.Constellation.md §1).
- **Renderer (owner, 2026-10-30, supersedes the earlier Direct3D 12 choice).** The Constellation renders with **Vulkan** on every client: Windows, Linux and Android. Vulkan is the primary backend, and it follows the latest Vulkan API and its new versions (forward-facing rule). This overrides the Skia choice in Bazaar.Constellation.md §1 for all heads. Owner, 2026-11-01: Skia through Avalonia is the fallback where Vulkan is unavailable, behind the renderer interface (#173); the split and thresholds are in doc 36. Mesh shading and other advanced features come through extensions, each gated by a runtime capability check with a fallback path.
- **Renderer interface.** A thin `IConstellationRenderer` in Client.Core keeps the renderer behind one seam. Its Vulkan implementation is the primary one; the Skia-through-Avalonia fallback is the only other backend in scope (owner, 2026-11-01). Any other backend (for example D3D12, or Metal later) is an optional future addition and is not planned now.
- **Binding (chosen, subject to WP7 spike).** Vortice.Vulkan, MIT licence, generated from the current Vulkan headers and kept current with new Vulkan releases, with thin bindings and no windowing forced on the caller. Fallback if it lags a needed Vulkan version: Silk.NET.Vulkan (also MIT). Either binding stays behind `IConstellationRenderer`. The choice is recorded here; WP7 confirms it.
- **Future goal: 3D space mode.** The owner may want a 3D space view later. Layout and projection outputs are dimension-agnostic where cheap: positions are packed floats with a dimension count (2 or 3), and 2D is the flattened default. No 3D renderer is in scope now.
- **Git.** Git access goes through one `IGitProvider`; LibGit2Sharp stays the default until spike S23 says otherwise (DECISIONS-LOG round 3, C12).
- **GCs.** Append-only. DMs are two-member GCs and the owner reads all of them (CLAUDE.md). Thinking GCs nest under working GCs and peers read them on request (DECISIONS-LOG round 3, C11). GC types come from the registry in 01 (round 6, C19).
- **Done.** For a card tied to a PR, Done means the PR is merged (round 8, C28).
- **Transport.** Realtime over the WebSocket; blobs and search over authenticated HTTP (02, round 6, C15). A stream is state unless 02's stream registry lists it as a log.
- **GitHub ingress.** The home host polls by default and accepts no inbound connections; webhooks are optional (round 8, C30).
- **Table ownership.** This doc owns no tables. It defines SQL views over tables that other docs own (round 6, C16).
- **Memory metadata.** Memory notes carry `source_trust` (provenance, 15) and `tier` (T0 to T3, 08) in front matter (round 7, C20).
- **Erasure.** Personal content is stored as per-person encrypted payloads; erasure destroys the key and leaves a tombstone (round 2, C01).
- **Owner direction, not yet in Bazaar.Constellation.md:** the Constellation also holds drafts and links internal docs and plan files (source undated in the draft).

## Design
- **Graph service (server, [PROPOSED]).** Builds a per-viewer projection from the Vault index (`vault_docs`, `vault_links`, 18), GC references (`gc`, `gc_mention`, 04), Kanban tasks (12), PR data (21) and the Git DAG index. Emits a versioned snapshot, then deltas on the `graph:<id>` stream (02 registry).
- **Layout engine (server library, [PROPOSED]).** ForceAtlas2-style forces with Barnes-Hut (quadtree in 2D, octree-ready for 3D). No Avalonia dependency. Runs in fixed-budget ticks per view.
- **Clustering (server, [PROPOSED]).** Louvain communities, recomputed when the graph version changes, not per frame.
- **Client renderer (`Bazaar.Client.Core`, [PROPOSED]).** A custom Avalonia control that hosts the renderer behind `IConstellationRenderer`; the renderer is Vulkan on all platforms with the Skia fallback (owner decisions, 2026-10-30 and 2026-11-01, see Decided inputs). Avalonia stays for chrome, Kanban and text-heavy editing panes; Monaco stays separate (doc 36). The control hosts the swap chain with `ICustomDrawOperation`-style interop (secondary source). It interpolates between received position frames, and owns camera, hit-testing and level of detail. It runs no simulation. Android shows a read-only drawer first (29).
- **Reverse gravity ([PROPOSED]).** On focus change, the server pins the focused node at the origin, gives each node a target ring radius by hop distance, and warm-starts from current positions so the change animates. The ring mechanism is an inference from the research, which names only attraction scaling. Ring count is `constellation.ring_depth`.
- **Commands ([PROPOSED]).** Navigation commands send a `FocusRequest` to the server over the socket. Keys are mnemonic, never Ctrl chords (27).

| Zoom tier | Draws |
|---|---|
| Far | Cluster glyphs and cluster-to-cluster edges |
| Mid | Node shape and type colour, labels for the focused neighbourhood |
| Near | All labels, edge kinds, hover preview (plain text) |

## Data and interfaces
| Node kind | Key | Source |
|---|---|---|
| Vault document (`type`: index, plan, deep-dive, research, constitution, decision, archive, memory, summary, twin, charter, draft; the list matches 18) | Vault document ID (ULID; survives renames) | `vault_docs` (18) |
| draft | Vault document with `type = draft` (18, [PROPOSED]) | Vault |
| objective | Vault document with front matter `kind: objective` [PROPOSED, doc 38] | Vault |
| phase | Vault document with front matter `kind: phase` [PROPOSED, doc 38] | Vault |
| pr | owner/repo#n | PR data from 21 (polled) |
| task | task ID | Kanban (12) |
| gc | GC ID | `gc` plus type registry (04, 01); DM nodes members and owner only |
| screenshot | asset ID | GC attachments by default (setting); bytes over HTTP (02) |
| commit, branch | sha, ref | Git DAG index (see Conflicts) |

Decisions are Vault documents of `type = decision` (18, C24), not a separate node kind. Memory notes are Vault documents of `type = memory`; the node shows their `tier`.

Objectives and phases are Vault documents of `type = plan` with front matter `kind` [PROPOSED, doc 38]: the `vault_docs` type CHECK (0009_vault.sql) has no `objective` or `phase` value.

Edge kinds: `links_to` (wiki-link, from `vault_links`), `backlink` (derived), `decides` (decision record to the node it decides; `decidedIn` GC message reference), `implements` (PR to task), `mentions` (from `gc_mention`), `derived_from`, `supersedes`, `contradicts` (front-matter relations, 08), `parent` (GC nesting, including Thinking GCs), `merges` (git), `depends_on` (objective to objective, from front matter) [PROPOSED, doc 38].

```csharp
public sealed record GraphSnapshot(long GraphVersion, IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges);
public sealed record LayoutFrame(long GraphVersion, long LayoutSeq, string FocusId, ReadOnlyMemory<float> Xy); // Xy: packed x,y per node index
public sealed record FocusRequest(string ViewId, string NodeId);
```

Git DAG index (per commit on the writer path): sha, parents, author identity, timestamp, refs, touched paths, GC and task trailers, merge flag. Read through `IGitProvider`.

### Settings [PROPOSED, doc 03 registry; DECISIONS-LOG round 13]
Every row is a plain value or view choice, so it is a setting rather than a question. None loosens a visibility or access rule. Each change is written to `audit_log` (15). Surfaced in advanced settings (27, 32).

| Key | Type | Default | Scope (install/project/agent) | Who may change | Change needs approval? |
|---|---|---|---|---|---|
| `constellation.node_cap` | int (1,000 to 50,000) | 10000 | install | operator | No (audited) |
| `constellation.ring_depth` | int (1 to 6) | 3 | install | operator | No (audited) |
| `constellation.git_layout` | enum `deterministic` or `force` | `deterministic` | project | project owner or operator | No (audited) |
| `constellation.orbit_radius` | enum `age` or `equal` | `age` | project | project owner or operator | No (audited) |
| `constellation.screenshot_sources` | list of `gc_attachments`, `test_artifacts` | `gc_attachments` | install | operator | No (audited) |

**Decided 2026-11-11 (safe default, coordinator).** These rules fix how `constellation.orbit_radius` maps to body radii. They are the shipped safe default, not an owner decision.
1. `age`: a body's radius scales linearly between `MinOrbitRadius` and `MaxOrbitRadius` by its last-activity age. Older bodies sit on outer orbits. The ratio is clamped to 0 to 1.
2. `equal`: every body uses one uniform radius, which is `MinOrbitRadius`.
3. If all ages are equal, the radius is `MinOrbitRadius`.
4. Defaults: `MinOrbitRadius` 10 (equal to the current `OrbitSpacing` default) and `MaxOrbitRadius` 100. Both live on `LayoutOptions`. The operator may change them. `OrbitSpacing`'s default is unchanged.

`constellation.git_layout` (deterministic or force) is still a caller dispatch between `SolarOrbitLayout` and `ForceLayout`. It is not mapped onto `LayoutOptions`.

## Behaviour and edge cases
- **Ordering.** Frames carry `GraphVersion` and `LayoutSeq`. The client drops any frame older than the newest applied.
- **Node identity.** Nodes key on the Vault document ID, not the path, so a rename keeps the node's place. Unresolved links stay as dim ghost nodes.
- **Node additions.** New nodes start at a neighbour's position (warm start), so nothing jumps.
- **Reconnect.** The client requests a snapshot and re-seeds from its last frame. Snapshots and frames are entries on `graph:<id>` (02 registry).
- **Visibility.** The projection is filtered per viewer by the Vault access rule (18), the DM rule and the Thinking-GC rule. DM bodies never enter the graph. DM titles appear to members and the owner. Thinking GCs are not nodes [PROPOSED, default]. Private memory notes appear only to the owner and their agent.
- **Erasure.** When erasure (C01) or a Vault removal takes effect, the node and its edges leave the projection on the next graph version. The Vault's own erasure answer is OPEN (18).
- **Limits.** Above `constellation.node_cap`, the view shows clusters only.
- **Hit-testing.** A uniform spatial grid rebuilt per frame. No linear scan.
- **Stale data.** When disconnected, the last snapshot is shown and marked stale.
- **Concurrency.** One layout engine per view. Concurrent layouts are capped.

## Scale and level of detail (owner, 2026-10-30; [PROPOSED] numbers)
Full design, budgets and benchmark plan: `constellation-scale-and-lod.md`.
- **Target scale.** Millions of nodes company-wide, with billions as the design ceiling. Only the visible, aggregated set reaches the GPU per frame.
- **Far zoom.** Louvain communities drawn as glyphs; aggregation runs server-side (layout and clustering stay on the server, C17).
- **Streaming.** Node positions and attributes are sent as viewport chunks or tiles, with a per-viewer cap. Chunks outside the view are evicted.
- **GPU path.** Instanced draws for nodes and edges, GPU-side frustum and LOD culling, compute-shader layout in later work. Mesh shading only behind a capability check.
- **Tiers.** PC and phone have separate per-frame draw and memory budgets. Phone uses fewer visible instances and no hover previews above the mid zoom tier.

## Performance budgets (all [PROPOSED] until spiked)
| Target | Budget |
|---|---|
| Client render, 5k nodes, Windows | 60 fps, ≤ 10 ms draw |
| Client render, 10k nodes | 60 fps (stretch; Q1) |
| Client render, 1M aggregated instances, PC | 60 fps, ≤ 12 ms draw (PROPOSED; measured on PC) |
| Client render, phone (Android Vulkan) | 60 fps at the phone tier, ≤ 14 ms draw (PROPOSED; measured on device) |
| CPU interpolation, 1M nodes | ≤ 2 ms per frame (benchmark: tests/Bazaar.Constellation.Bench) |
| Server layout tick, 10k nodes | ≤ 8 ms per tick on the owner's PC (absolute budget; S14 measures it on the owner's PC) |
| Focus change to settled layout, 5k nodes | ≤ 1 s |
| Android | not gated until the spike |

No measured frame times exist for Avalonia/Skia graphs at these sizes (research gap). Spike S14 in 35 measures these.

## Git Solar System
- Branches are orbits around the default branch. Commits are bodies on their branch's orbit, ordered by time. Orbit radius follows `constellation.orbit_radius`.
- Merge commits draw as comets from the merged branch to its target.
- Vault branches (`agent/<task>`) are planets. Writer-queue merges (18) are comets.
- The layout is deterministic by default (`constellation.git_layout = deterministic`), so history does not jitter as it grows. `force` is available as a setting.

**DECIDED 2026-11-11 (coordinator).** These defaults let WP11 build on the solar system without waiting on the owner:
1. Comet direction runs from the merged branch tip to the merge commit.
2. Every parent edge is kept, not only merge comets, so plain parent edges are drawn too.
3. The git DAG indexer (`src/Bazaar.Server/Constellation/GitDag/`) is the source of these edges.

## Testing
- **Layout engine:** xUnit v3, no Avalonia. Fixed seed. Invariants: no NaN, settling, hop ordering, determinism. Barnes-Hut matches brute force within tolerance at 1k nodes.
- **Clustering:** two cliques joined by one edge yield two communities.
- **Projection:** real PostgreSQL fixture. Unresolved links kept. Non-members never see DM nodes. Thinking GCs produce no nodes. Another agent's private memory is absent; the owner sees it. An erasure tombstone removes the node on the next version.
- **Git DAG:** fixture repo with a merge yields one comet edge, read through `IGitProvider`.
- **Client:** Avalonia.Headless with the Vulkan renderer on a software Vulkan device (CI-capable). Golden image at 200 nodes. Hit-test and keyboard-only walk produce the expected `FocusRequest` sequence.
- **Settings:** each setting's default and bounds are tested; a change writes an audit entry.
- **Performance:** benchmark at 1k, 5k and 10k on the owner's PC (spike S14), nightly, not a PR gate.

## Work packages
1. `Bazaar.Contracts`: graph and layout records, JSON source generation; register `graph:<id>` in 02's registry.
2. SQL projection views over `vault_docs` and `vault_links` (18), `gc`, `gc_mention` (04), task tables (12) and PR tables (21), with the visibility filter. Depends on 1 and on those tables existing.
3. Layout core: ForceAtlas2 forces, quadtree Barnes-Hut, tick API. No dependencies.
4. Reverse gravity: hop rings and warm start. Depends on 3.
5. Louvain clustering. Depends on 2.
6. Git DAG indexer through `IGitProvider`. Depends on 2 and on S23.
7. Client renderer: custom control, LOD, interpolation. Depends on 1, 3.
8. Spatial grid hit-testing. Depends on 7.
9. Navigation command model and keymap. Depends on 7.
10. Accessible list mirror. Depends on 7.
11. Git Solar System orbit layout and comet rendering. Depends on 6, 7.
12. Benchmark spike at 5k and 10k on Windows and Android (35 S14). Depends on 7.
13. Settings wiring for the five `constellation.*` keys (03 registry, 27 advanced settings). Depends on 03.

## Conflicts
1. **Git layout.** The research suggests force layout over the commit DAG. This doc proposes deterministic orbits as the default and keeps force layout as a setting (`constellation.git_layout`). The research departure is recorded here; the default is [PROPOSED] and not an owner decision.
2. **Spike target.** The owner's direction asks for 10k nodes at 60 fps. The implementation plan (Wave 4) and 35 S14 gate on 5k, with 10k as stretch (Q1).
3. **Unverified.** `ICustomDrawOperation` usage and Louvain are secondary-sourced. Accessibility beyond Linux AT-SPI2 (UIA, TalkBack) is unverified.
4. **For 18.** Git DAG index table owner is not assigned. The 18 fix list proposes this doc for the Vault repo and 17 for code repos [PROPOSED]; this doc follows that proposal until 18 records it.
5. **Stale deep-dive.** `Bazaar.md` line 75 still says "Mood Board"; the Constellation is the renamed view (register stale-line table). Fix in the deep-dive pass.

## Questions for the owner
ANSWERED: layout runs on the server; clients render only (DECISIONS-LOG round 4, C17).
ANSWERED: DM nodes appear to members, and the owner sees all DMs, per the decided DM rule (CLAUDE.md).

1. **Performance gate.** 5k or 10k at 60 fps on the owner's PC? Default: 5k must pass on the Windows owner's PC desktop; 10k is a stretch. Stays a question: it is an acceptance gate set against the owner's 10k direction (Conflict 2).
2. **Drafts.** Visible to all viewers, or only the owner and author? Default: owner and author, the same rule as DMs. Also confirm `draft` as a Vault document type (18 lists it as [PROPOSED]). Stays a question: visibility is an access rule and the type is a file-layout choice.
3. **Accessibility.** Is the keyboard list mirror required for v1? Default: yes. Product scope.
4. **Memory and Thinking GCs in the graph.** Should other human members see agent memory notes (18's default is owner and owning agent only)? Should Thinking GCs ever be nodes? Default: no and no; the owner can see both through the Vault and GC views. Stays a question: this is an access rule for private material (DECISIONS-LOG round 3, C11).

MOVED TO SETTINGS: constellation.node_cap, constellation.ring_depth, constellation.git_layout, constellation.orbit_radius, constellation.screenshot_sources (DECISIONS-LOG round 13)
