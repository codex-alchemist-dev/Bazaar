# Constellation scale and level of detail (billions of nodes)

Status: PROPOSED (2026-11-01; tile format decided by the coordinator, owner-approved) · Owner question: "what if we zoomed all the way out and wanted to see the billions?" · Extends: 19-constellation-and-git-solar-system.md (WP7, scale section) · Decision context: DECISIONS-LOG round 17 (Vulkan with GPU instancing; Skia as fallback)

Every number here is a PROPOSED budget or a calculation. The first GPU measurements are in section 10 (PC, 2026-11-01); phone numbers are still unmeasured. Section 7 lists what needs the owner's PC, and the phone numbers need an Android device.

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
- **Decided (coordinator, owner-approved, 2026-11-01).** Bulk tile payloads may be binary over HTTP. This is a scoped exception to the JSON-only wire rule. Control messages stay JSON through `IWireCodec`. Tiles use the versioned format below (`tile_format_version = 1`).
- **Tile format v1 (little-endian).**
  - Header, 64 bytes: magic `BZTL` (4 bytes); `tile_format_version` u16 = 1; `dimensions` u8 (2 or 3); `flags` u8 (bit 0: sampled, bit 1: full leaf); `node_count` u32; `sample_count` u32; `graph_version` i64; `tile_id` (16 bytes, ULID); bounding box as four f32 (min x, min y, max x, max y); `quant_bits` u8 (16); reserved to 64 bytes.
  - Origin and scale: f32 origin per axis and f32 scale per axis, so position = origin + q * scale.
  - Body: `node_count` records of quantised positions (u16 per axis), then `node_count` u32 cluster ids, then `node_count` u16 type and state packed. Sampled tiles repeat the record layout for `sample_count` entries.
  - Readers reject an unknown version and any `node_count` that does not match the body length. A new version requires a registry entry in doc 02.

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

## 8. Open questions and the defaults taken
1. **Tile payload format.** Answered: binary exception with the v1 format in section 3.
2. **Starting caps (section 6).** Default taken: use the section 6 numbers as starting caps. They are settings under the configurability rule, with the floors fixed. Tuned after the PC and phone measurements (section 7).
3. **Layout scope.** Default taken: precomputed hierarchical layout for the whole graph on the server, with force layout only inside focus neighbourhoods (section 2).
4. **Meaning of "billions".** Default taken: one company graph per install. The tile index is sized per install, so a multi-company graph would need a separate design.

Items that need the owner's PC or an Android device are in section 7, not here.

## 9. Relation to other docs
- 19: the renderer and layout sections should point here for scale. Doc 19's 10k-node layout budget stays for the focus neighbourhood.
- 02: tile blobs (format v1) need a registry entry in the stream and blob registry. The JSON-only wire rule has the scoped binary exception recorded in section 3.
- 03: the caps in section 6 are settings rows, added with the other `constellation.*` keys.
- DECISIONS-LOG round 17: the owner decision there says the target is billions; this doc is how the design meets it.

## 10. Measured results: Windows PC, Intel Arc B580 (2026-11-01)
Measured by `tests/Bazaar.Constellation.Vulkan.Bench` (Release build, offscreen 1280x720 R8G8B8A8 target, dynamic rendering, one instance per node, 16 bytes per node, 30 warm-up and 200 measured frames per row). Frame time is wall clock per submitted frame, fenced each frame. GPU time is from timestamp queries.

Probe: Intel(R) Arc(TM) B580 Graphics (discrete, 11.9 GiB device-local heap), Vulkan 1.4.356 (instance API 1.4.0), `VK_EXT_mesh_shader` present, `pageableDeviceLocalMemory` true. The integrated Intel GPU also reports mesh shaders.

**Node-count sweep** (instances drawn capped at 2 000 000; resident window capped at 1.5 GiB):

| Total nodes | Resident nodes | Instances per frame | Upload (ms) | Resident (GiB) | Wall p50 (ms) | Wall p95 (ms) | GPU p50 (ms) | GPU p95 (ms) |
|---|---|---|---|---|---|---|---|---|
| 1M | 1M | 1M | 18.8 | 0.015 | 0.62 | 0.92 | 0.50 | 0.79 |
| 10M | 10M | 2M | 738 | 0.15 | 1.86 | 1.96 | 1.72 | 1.72 |
| 100M | 100M | 2M | 2 397 | 1.49 | 1.12 | 1.71 | 1.00 | 1.54 |
| 1B | 100.7M window | 2M | 1 412 | 1.50 | 1.68 | 4.27 | 1.54 | 4.05 |

At 1B the whole graph would be 16 GB at 16 bytes per node, so it cannot be resident. The row measures a 100.7M-node window with 2M instances drawn, which is the tile model in section 4 with the cap applied.

**Instance-count sweep** (100.7M resident):

| Instances per frame | Wall p50 (ms) | Wall p95 (ms) | GPU p50 (ms) | GPU p95 (ms) |
|---|---|---|---|---|
| 100 000 | 0.63 | 0.66 | 0.37 | 0.37 |
| 1 000 000 | 1.94 | 3.36 | 1.78 | 3.15 |
| 2 000 000 | 1.12 | 3.70 | 1.00 | 3.55 |
| 10 000 000 | 318.2 | 319.6 | 317.9 | 319.3 |
| 50 000 000 | 2 427.0 | 2 429.1 | 2 426.6 | 2 428.7 |
| 100 000 000 | not measured | | | |

**What these numbers say, and what they do not.**
- At the 2M cap the PC is well inside the section 6 frame budget (p95 4.3 ms worst row against 14 ms). Resident data at 100M is 1.49 GiB, which fits the 1.5 GiB PC cap only just.
- The jump between 2M and 10M instances is not linear: 2M draws in about 1 ms, 10M in about 318 ms. This needs an explanation before the cap is raised. The likely cause is driver-side splitting of very large instance counts, but that is not verified.
- Frame times at 1M vary between the two sweeps (0.62 ms and 1.94 ms p50). This is probably GPU clock and power state, so treat differences under about 2 ms as noise until a warm-up is longer.
- The 100M-instance row was not run. A 50M row alone takes about 2.4 s per frame, so the full row would take about 20 minutes. It was stopped.

**Not measured yet.** The swapchain and windowed present path (this benchmark renders offscreen). Mesh-shader culling (the extension is present, the path is not used). Compute culling and density splatting, indirect draws, and tile streaming from the server. Atomic-add throughput. Phone numbers (no Android device here).

**Probe note.** The driver string `0.406.842` is the probe's generic decoding of `driverVersion`. The Vulkan SDK reports the raw value `101.9034` for this Intel driver, so the probe's string formatting is wrong for Intel and needs a vendor-aware decode.

### 10.1 Investigation of the cliff above 2M instances (2026-11-01)
Experiments at 10M resident nodes, `Bazaar.Constellation.Vulkan.Bench experiments`, 60 measured frames each. GPU p50 in ms.

| Experiment | GPU p50 (ms) |
|---|---|
| 10M instances, one draw, quads (baseline) | 317.9 |
| 10M instances, one draw, degenerate quads (vertex work only) | 5.0 |
| 10M instances, 2M per draw call (same frame) | 317.9 |
| 10M instances, 1M per draw call (same frame) | 317.9 |
| 10M instances, one point per node (point list) | 159.6 |
| Point list, nodes clustered into 10% of the target | 159.6 |
| Point list, nodes clustered into 1% of the target | 169.1 |
| Quads, nodes clustered into 1% of the target | 338.2 |
| Single draw, quads, 4.0M / 4.1M / 4.2M / 4.5M / 5M / 6M / 8M instances | 2.0 / 7.3 / 12.5 / 28.3 / 54.6 / 107.3 / 212.6 |

What this rules out: a per-draw-call limit (splitting does not help), vertex fetch (degenerate quads are cheap), and scattered render-target writes (clustering makes it no faster).

What it suggests, not proven: the cost grows roughly linearly beyond about 4M instances per frame, at about 50 ms per million. A per-frame work budget in the driver is one candidate. The point-list path roughly halves the cost but is still far from the budget.

Consequence for the design: keep the instance count per frame at or below about 4M, and take the far-zoom density path (section 5) rather than per-node draws beyond that. Revisit once a second driver is measured, and once a multi-submit frame is tried.

### 10.2 Android bench on an emulator (emulator, not a phone; 2026-11-08)
Run: `Bazaar.Constellation.Vulkan.Bench.Android` on an Android 15 (API 35) x86_64 Google APIs emulator, WHPX-accelerated, with `-gpu host`. Vulkan calls go through the emulator's translation layer to the host Intel Arc B580. The emulator reports Vulkan 1.3.0 and no mesh-shader extension. Phone caps: 256 MB resident (16M nodes), 300 000 instances per frame. These numbers are not phone numbers. Section 6 phone budgets still need a physical device.

| Section | Total nodes | Resident nodes | Instances per frame | Upload (ms) | Wall p50 (ms) | Wall p95 (ms) | GPU p50 (ms) | GPU p95 (ms) |
|---|---|---|---|---|---|---|---|---|
| nodes | 1M | 1M | 300k | 982 | 2.29 | 5.97 | 1.07 | 1.87 |
| nodes | 4M | 4M | 300k | 2 538 | 1.90 | 2.90 | 1.07 | 1.91 |
| nodes | 16M | 16M | 300k | 8 490 | 1.61 | 2.38 | 1.07 | 1.86 |
| instances | 16.8M | 16.8M | 100k | 8 001 | 0.77 | 1.68 | 0.37 | 1.21 |
| instances | 16.8M | 16.8M | 300k | 8 001 | 1.69 | 2.42 | 1.07 | 1.96 |
| instances | 16.8M | 16.8M | 1M | 8 001 | 2.29 | 3.67 | 1.92 | 3.30 |

The 300k-instance cap is the phone-tier figure from section 6. Above it, the emulator tracks the PC roughly linearly and did not show the PC cliff in the range measured here (1M instances at 1.9 ms GPU p50). The emulator's translation layer dominates these numbers, so they don't say anything about phone-GPU cliffs.
