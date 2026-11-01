# Constellation scale and level of detail (billions of nodes)

Status: PROPOSED (2026-11-01) · Owner question: "what if we zoomed all the way out and wanted to see the billions?" · Extends: 19-constellation-and-git-solar-system.md (WP7, scale section) · Decision context: DECISIONS-LOG round 17 (Vulkan with GPU instancing; Skia as fallback)

Every number here is a PROPOSED budget or a calculation. Nothing is measured on a GPU yet. Section 7 lists what needs the owner's PC, and the phone numbers need an Android device.

## 1. The answer in one paragraph
At full zoom-out, billions of nodes are not drawn as billions of dots. They are drawn as **density and structure**: cluster glyphs (Louvain communities, server-side) plus a density field accumulated from the nodes. This is how point-cloud and particle systems handle the same problem. Individual nodes appear as the camera moves into a tile, and only that tile's nodes are streamed and drawn. The renderer therefore never holds all billions at once; it holds a bounded working set chosen by the view. At far zoom a node is sub-pixel anyway, so per-node dots would add cost and no information.

## 2. What we must not do
- **Hold all nodes resident.** Per-node GPU data is roughly 16 to 24 bytes (2D position, cluster id, type and state). One billion nodes is 16 to 24 GB before edges. That does not fit a phone, and it is most of a PC's GPU memory.
- **Run force layout over the whole graph.** The 8 ms layout tick in doc 19 is for the neighbourhood around the focus. Whole-graph layout at billions is not feasible per frame. The server must produce a hierarchical, precomputed layout, and force simulation runs only inside a focused neighbourhood (ring depth, doc 19).
- **Rebuild the CPU spatial grid every frame at scale.** Measured on the CPU benchmark (tests/Bazaar.Constellation.Bench): a 1M-node grid rebuild took about 118 ms, and interpolation about 44 ms. Per-frame CPU work at that size is not viable. Culling moves to the GPU, and hit-testing uses the tile index (section 4) rather than a rebuilt grid.

## 3. Level-of-detail hierarchy (server-side, precomputed)
- **Structure.** A quadtree (2D; octree-ready for 3D) over layout positions, with tiles at each level. Each tile holds: node count, bounding box, Louvain cluster histogram, and a fixed-size sample of its nodes chosen by importance (degree, recency, pinned). Leaves hold all nodes.
- **Build.** Built by the server when the graph version changes, not per frame. Incremental: only tiles touched by changed nodes are rebuilt. The build is a batch job, so its budget is minutes per million-node change set, not frame time. That number is OPEN and needs a spike.
- **Stored form.** Each tile is a binary blob: positions quantised to 16 bits per axis relative to the tile origin (4 bytes per node in 2D), cluster id, type and state. Tiles are served over authenticated HTTP as blobs (doc 02, doc 19 Transport).
- **Open question for the owner.** The wire is JSON through `System.Text.Json` (S6, DECISIONS-LOG). Bulk tile payloads would be raw binary over HTTP, which is a different format. Confirm that tile blobs are an exception, or pick a binary tile format. This is a schema or wire choice, so it needs one answer.

## 4. Streaming and selection
- **Viewport selection.** The client asks for the tiles the camera needs at the level that matches screen density. A tile is requested when its projected size passes a threshold; a coarser ancestor is drawn until then.
- **Working set cap.** Each tier caps resident tiles. Least-recently-used tiles are evicted when the cap is hit. Tiles in view are never evicted.
- **Hit-testing.** The tile index gives the candidate tiles for a pointer position. Only those tiles' nodes are tested, on the GPU or in a bounded CPU pass. The full-graph grid is not used.
- **Consistency with doc 19.** Frames still carry `GraphVersion` and `LayoutSeq`. A tile from an older version is dropped when a newer one is applied.

## 5. Drawing at each zoom tier
| Zoom | What is drawn | How |
|---|---|---|
| Far (whole graph) | Cluster glyphs and a density field | Compute pass splats each visible tile's counts into a density texture (log tone map, one texel per few pixels). Cluster glyphs are instanced quads. No per-node work. |
| Mid | Tile-level sampled nodes plus density | Instanced points for each tile's sample. Density fades out as points fade in. |
| Near (tile in view) | All nodes of the visible tiles | Instanced points or quads. GPU frustum and LOD culling write indirect draw arguments; the draw uses indirect count (core in Vulkan 1.2). |

Density splatting uses atomic adds into a texture. Atomic throughput varies by GPU, so the density pass is one of the things section 7 says must be measured.

## 6. Budgets (all PROPOSED)
| Item | PC tier | Phone tier |
|---|---|---|
| Frame rate target | 60 fps | 60 fps at the phone tier; 30 fps floor |
| GPU frame time, p95 | ≤ 14 ms | ≤ 14 ms at 60 fps (≤ 30 ms floor) |
| Max instances drawn per frame | 2 000 000 | 300 000 |
| Resident node data on GPU | ≤ 1.5 GB | ≤ 256 MB |
| Resident tiles (CPU) | ≤ 2 GB | ≤ 512 MB |
| Density pass at far zoom, 1080p-equivalent | ≤ 3 ms | ≤ 6 ms |
| Stream bandwidth, steady pan | ≤ 20 MB/s | ≤ 4 MB/s |
| Cold start to first frame, 1B-node graph | ≤ 3 s (cached tiles) | ≤ 5 s (cached tiles) |
| Server tile build, per million changed nodes | OPEN (spike) | n/a (server-side) |

Tiers are chosen from measured device capability at startup, and the operator can tighten the caps. They cannot be loosened above the floors. Caps are settings under the configurability rule (DECISIONS-LOG round 13), not hard-coded values.

## 7. Benchmark plan and what needs the owner's PC
**Node counts:** 1M, 10M, 100M, 1B. The 1B run uses synthetic graphs with preferential-attachment structure, the same generator as spike S14, so results compare across runs.

**Per run, record:** frame time p50, p95 and max; GPU time per pass (culling, density, draw); instances drawn; resident bytes on GPU and CPU; tiles fetched per second and bytes per second; time to first frame after a cold start.

**Scenarios:** (a) fully zoomed out, (b) zoomed to a single cluster, (c) pan across the graph at a fixed zoom, (d) zoom in from far to near over 2 s, (e) focus change (reverse gravity) at 100M.

**Needs the owner's PC (Windows, GPU):** every GPU frame-time number; compute culling and density throughput; indirect draw cost; VRAM use and whether 1.5 GB holds at 1B; atomic-add throughput for splatting; driver behaviour of indirect count.

**Needs an Android device:** every phone number above (frame time, memory, thermal behaviour over a 10-minute pan). An emulator does not count.

**Can run in the cloud (CPU only):** tile build time and size at 1M to 100M nodes; tile index lookup cost; culling logic correctness on the CPU; memory footprint of tile metadata. The cloud container has 4 vCPU, so server-side timings are not representative of the NUC or PC.

**Pass criteria:** the PC tier meets the section 6 budgets at 100M and holds at 1B with the instance cap in force. The phone tier meets its budgets at 100M with its own cap. A failure at 1B on a tier means lowering the instance cap or the working set, not dropping the scale target, unless the owner decides otherwise.

## 8. Open questions for the owner
1. Tile payload format: a binary blob over HTTP as an exception to JSON-only, or another format? (Section 3.)
2. Are the section 6 numbers acceptable as starting caps? The most important are the phone instance cap and resident limit.
3. Is a server-side hierarchical layout acceptable for the whole graph, with force layout only in focus neighbourhoods? (Section 2.)
4. Does "billions" mean billions of nodes in one company graph, or billions across many companies? The answer sets the tile index size.

## 9. Relation to other docs
- 19: the renderer and layout sections should point here for scale. Doc 19's 10k-node layout budget stays for the focus neighbourhood.
- 02: tile blobs need a registry entry if question 1 is answered as binary.
- 03: the caps in section 6 are settings rows, added with the other `constellation.*` keys.
- DECISIONS-LOG round 17: the owner decision there says the target is billions; this doc is how the design meets it.
