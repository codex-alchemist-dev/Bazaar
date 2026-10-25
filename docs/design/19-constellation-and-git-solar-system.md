# The Constellation and Git Solar System

Status: RECONCILED with DECISIONS-LOG (2026-10-25) · Owner questions: 9
Depends on: 01-gc-data-model.md, 02-wire-protocol-and-sync.md, 04-postgres-schema-and-migrations.md, 08-memory-system.md, 12-kanban-and-dependencies.md, 17-coding-agent-harness.md, 18-vault.md, 21-github-integration.md, 27-client-architecture.md, 35-spikes-plan.md · Used by: 28-windows-client.md, 29-android-client.md, 30-linux-client-and-packaging.md

## Purpose
The Constellation is the visual view of the knowledge graph that the Vault and GCs form. The Git Solar System applies the same renderer to commit history and Vault branches. Both show what connects to what, and what changed while away. The design source is the deep-dive `Bazaar.Constellation.md`; this doc turns it into buildable parts.

## Decided inputs
- **One graph, two views.** The Constellation and the Vault are two views of one knowledge graph (Bazaar.Constellation.md §1; DECISIONS-LOG round 11, C38). The Vault holds almost every durable written artifact, including each agent's memory notes, so document nodes come from the Vault and need no second store (18).
- **Vault access applies to the graph.** Every read, search hit and link resolution is checked against the caller's principal (18, Access). The owner reads all. An agent reads its own private memory. Other agents do not read private memory. The projection uses the same filter (C38, 18).
- **Compute location.** Layout and clustering run on the server; clients render and run no simulation. This follows CLAUDE.md, whose listed exceptions (on-device inference, Studio relay, local editor and terminal hosts, fold arrangement) do not include the graph (DECISIONS-LOG round 4, C17).
- **Licence.** The public build is OSI-only, with no paid Avalonia controls (DECISIONS-LOG round 4, C23). Node previews use plain Skia text.
- **Renderer.** Reverse Gravity Layout; Vim-style navigation that restructures the graph; native Skia through Avalonia; no D3-Force, WebGPU or Win2D (Bazaar.Constellation.md §1).
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
- **Graph service (server, [PROPOSED]).** Builds a per-viewer projection from the Vault index (`vault_docs`, `vault_links`, 18), GC references (`gc`, `gc_mention`, 04), Kanban tasks (12), PR data (21) and the Git DAG index. Emits a versioned snapshot, then deltas.
- **Layout engine (server library, [PROPOSED]).** ForceAtlas2-style forces with Barnes-Hut (quadtree in 2D, octree-ready for 3D). No Avalonia dependency. Runs in fixed-budget ticks per view.
- **Clustering (server, [PROPOSED]).** Louvain communities, recomputed when the graph version changes, not per frame.
- **Client renderer (`Bazaar.Client.Core`, [PROPOSED]).** A custom Avalonia control that draws through `ICustomDrawOperation` to Skia (secondary source). It interpolates between received position frames, and owns camera, hit-testing and level of detail. It runs no simulation. Android shows a read-only drawer first (29).
- **Reverse gravity ([PROPOSED]).** On focus change, the server pins the focused node at the origin, gives each node a target ring radius by hop distance, and warm-starts from current positions so the change animates. The ring mechanism is an inference from the research, which names only attraction scaling.
- **Commands ([PROPOSED]).** Navigation commands send a `FocusRequest` to the server over the socket. Keys are mnemonic, never Ctrl chords (27).

| Zoom tier | Draws |
|---|---|
| Far | Cluster glyphs and cluster-to-cluster edges |
| Mid | Node shape and type colour, labels for the focused neighbourhood |
| Near | All labels, edge kinds, hover preview (plain text) |

## Data and interfaces
| Node kind | Key | Source |
|---|---|---|
| Vault document (`type`: index, plan, deep-dive, research, constitution, decision, archive, memory, summary, twin) | Vault document ID (ULID; survives renames) | `vault_docs` (18) |
| draft | OPEN: 18's type list has no `draft` (Q3) | Vault, if adopted |
| pr | owner/repo#n | PR data from 21 (polled) |
| task | task ID | Kanban (12) |
| gc | GC ID | `gc` plus type registry (04, 01); DM nodes members and owner only |
| screenshot | asset ID | GC attachments (Q8); bytes over HTTP (02) |
| commit, branch | sha, ref | Git DAG index (see Conflicts) |

Decisions are Vault documents of `type = decision` (18, C24), not a separate node kind. Memory notes are Vault documents of `type = memory`; the node shows their `tier`.

Edge kinds: `links_to` (wiki-link, from `vault_links`), `backlink` (derived), `decides` (decision record to the node it decides; `decidedIn` GC message reference), `implements` (PR to task), `mentions` (from `gc_mention`), `derived_from`, `supersedes`, `contradicts` (front-matter relations, 08), `parent` (GC nesting, including Thinking GCs), `merges` (git).

```csharp
public sealed record GraphSnapshot(long GraphVersion, IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges);
public sealed record LayoutFrame(long GraphVersion, long LayoutSeq, string FocusId, ReadOnlyMemory<float> Xy); // Xy: packed x,y per node index
public sealed record FocusRequest(string ViewId, string NodeId);
```

Git DAG index (per commit on the writer path): sha, parents, author identity, timestamp, refs, touched paths, GC and task trailers, merge flag. Read through `IGitProvider`.

## Behaviour and edge cases
- **Ordering.** Frames carry `GraphVersion` and `LayoutSeq`. The client drops any frame older than the newest applied.
- **Node identity.** Nodes key on the Vault document ID, not the path, so a rename keeps the node's place. Unresolved links stay as dim ghost nodes.
- **Node additions.** New nodes start at a neighbour's position (warm start), so nothing jumps.
- **Reconnect.** The client requests a snapshot and re-seeds from its last frame. Snapshots and frames are stream entries in 02's registry.
- **Visibility.** The projection is filtered per viewer by the Vault access rule (18), the DM rule and the Thinking-GC rule. DM bodies never enter the graph. DM titles appear to members and the owner. Thinking GCs are not nodes [PROPOSED, default]. Private memory notes appear only to the owner and their agent.
- **Erasure.** When erasure (C01) or a Vault removal takes effect, the node and its edges leave the projection on the next graph version. The Vault's own erasure answer is OPEN (18).
- **Limits.** Above a node cap (PROPOSED 10,000 visible), the view shows clusters only.
- **Hit-testing.** A uniform spatial grid rebuilt per frame. No linear scan.
- **Stale data.** When disconnected, the last snapshot is shown and marked stale.
- **Concurrency.** One layout engine per view. Concurrent layouts are capped.

## Performance budgets (all [PROPOSED] until spiked)
| Target | Budget |
|---|---|
| Client render, 5k nodes, Windows | 60 fps, ≤ 10 ms draw |
| Client render, 10k nodes | 60 fps (stretch; Q2) |
| Server layout tick, 10k nodes | ≤ 8 ms per tick on the NUC |
| Focus change to settled layout, 5k nodes | ≤ 1 s |
| Android | not gated until the spike |

No measured frame times exist for Avalonia/Skia graphs at these sizes (research gap). Spike S14 in 35 measures these.

## Git Solar System
- Branches are orbits around the default branch. Commits are bodies on their branch's orbit, ordered by time.
- Merge commits draw as comets from the merged branch to its target.
- Vault branches (`agent/<task>`) are planets. Writer-queue merges (18) are comets.
- The layout is deterministic, not force-based [PROPOSED], so history does not jitter as it grows.

## Testing
- **Layout engine:** xUnit v3, no Avalonia. Fixed seed. Invariants: no NaN, settling, hop ordering, determinism. Barnes-Hut matches brute force within tolerance at 1k nodes.
- **Clustering:** two cliques joined by one edge yield two communities.
- **Projection:** real PostgreSQL fixture. Unresolved links kept. Non-members never see DM nodes. Thinking GCs produce no nodes. Another agent's private memory is absent; the owner sees it. An erasure tombstone removes the node on the next version.
- **Git DAG:** fixture repo with a merge yields one comet edge, read through `IGitProvider`.
- **Client:** Avalonia.Headless with Skia. Golden image at 200 nodes. Hit-test and keyboard-only walk produce the expected `FocusRequest` sequence.
- **Performance:** benchmark at 1k, 5k and 10k on the NUC (spike S14), nightly, not a PR gate.

## Work packages
1. `Bazaar.Contracts`: graph and layout records, JSON source generation; register the streams in 02's registry.
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

## Conflicts
1. **Git layout.** The research suggests force layout over the commit DAG. This doc proposes deterministic orbits, a departure from the research (Q1).
2. **Spike target.** The owner's direction asks for 10k nodes at 60 fps. The implementation plan (Wave 4) and 35 S14 gate on 5k, with 10k as stretch (Q2).
3. **Unverified.** `ICustomDrawOperation` usage and Louvain are secondary-sourced. Accessibility beyond Linux AT-SPI2 (UIA, TalkBack) is unverified.
4. **For 35 (S14).** The layout-tick budget is an absolute NUC figure here, but 35 runs layout "in the cloud, relative". 35 must measure the NUC figure.
5. **For 18.** Git DAG index table owner is not assigned (default: 18 for the Vault repo, 17 for code repos). 18's type list has no `draft` (Q3).
6. **For 02.** Confirm the stream registry entries `GraphSnapshot`, `LayoutFrame` and the `FocusRequest` command.
7. **Stale deep-dive.** `Bazaar.md` line 75 still says "Mood Board"; the Constellation is the renamed view (register stale-line table). Fix in the deep-dive pass.

## Questions for the owner
ANSWERED: layout runs on the server; clients render only (DECISIONS-LOG round 4, C17).
ANSWERED: DM nodes appear to members, and the owner sees all DMs, per the decided DM rule (CLAUDE.md).

1. **Git layout.** Deterministic orbits or force layout? Default: deterministic, so history does not jitter.
2. **Performance gate.** 5k or 10k at 60 fps? Default: 5k must pass on the Windows NUC desktop; 10k is a stretch.
3. **Drafts.** Visible to all viewers or only the owner and author? Default: owner and author, the same rule as DMs. Also: is `draft` a Vault document type? Default: yes, added to 18.
4. **Orbit radius.** By commit age or equal spacing? Default: by age.
5. **Node cap.** Default 10,000 visible before cluster-only view.
6. **Reverse gravity depth.** Default: one ring per hop, three rings shown, the rest clustered.
7. **Accessibility.** Is the keyboard list mirror required for v1? Default: yes.
8. **Screenshots.** Node source: GC attachments only, or test artifacts too? Default: GC attachments only.
9. **Memory and Thinking GCs in the graph.** Should other human members see agent memory notes (18's default is owner and owning agent only)? Should Thinking GCs ever be nodes? Default: no and no; the owner can see both through the Vault and GC views.
