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

### 10.3 PC re-run and run-to-run variance (2026-11-13)
Same bench, Release build, Intel Arc B580, Vulkan 1.4.356. GPU p50 and p95 in ms. Earlier run in section 10 for comparison.

| Row | Earlier p50 | Re-run p50 | Re-run p95 |
|---|---|---|---|
| 1M nodes, 1M instances | 0.50 | 0.58 | 0.97 |
| 10M nodes, 2M instances | 1.72 | 1.02 | 1.42 |
| 100M nodes, 2M instances | 1.00 | 1.00 | 1.39 |
| 1B nodes (100.7M window), 2M instances | 1.54 | 1.00 | 1.00 |
| 100k instances, 100.7M resident | 0.37 | 0.29 | 0.29 |
| 1M instances, 100.7M resident | 1.78 | 0.50 | 0.77 |
| 2M instances, 100.7M resident | 1.00 | 1.00 | 1.00 |
| 10M instances, 100.7M resident | 318 | 326 | 328 |
| 50M instances, 100.7M resident | 2 427 | 2 467 | 2 536 |
| 100M instances, 100.7M resident | not measured | 5 111 | 5 151 |

The cliff reproduces. Above about 2M instances per frame the cost grows about linearly with instance count, at about 50 ms per million. The 100M-instance row takes 5.1 s per frame.

Small-count rows vary between the two runs. The largest gap is the 10M-node, 2M-instance row (1.72 against 1.02 ms), and some 1M-instance rows differ by about 1.3 ms. Treat differences under about 2 ms, or any single-run number, as noise until a repeat-run spread is measured (section 10.4 once added).

### 10.4 Root-cause controls for the cliff, and the density fix (2026-11-14)
Bench modes `rootcause` and `passes`, `density`. Release build, Intel Arc B580, 10M nodes in one frame unless noted. GPU p50 in ms.

**Controls.** Each row changes one thing against the baseline:

| Control | GPU p50 (ms) |
|---|---|
| Triangles, 10M instances, on screen (three repeats: 322.3, 323.4, 323.7) | ~323 |
| Triangles, 10M instances, all moved off screen | 5.0 |
| One point per node, on screen | 162 |
| One point per node, off screen | 4.2 |
| One point per node, on screen, colour writes disabled | 4.2 |
| Triangles, 2M or 1M per render pass (5 or 10 passes per frame) | 323 / 322 |
| Points, 2M or 1M per render pass | 162 / 161 |

What this rules out: vertex fetch and clipping (off-screen draws are cheap), per-draw-call and per-render-pass overhead (splitting changes nothing), and scattered writes (clustering the nodes changes nothing). Colour writes to the target are what cost time: with writes disabled the on-screen points cost the same as the off-screen ones.

**Root cause, not proven.** The cost is about flat up to about 4M instances per frame, then rises roughly linearly at about 50 ns per instance, whatever the frame is split into. The likely cause is a per-frame limit inside the driver or hardware for on-screen primitive or fragment work. The controls do not show which. Solving it inside the driver is not in our hands, so the fix below avoids it.

**Fix: density path (section 5, far zoom).** A compute pass bins every node into a 512 x 288 grid of counters with atomic adds. One fullscreen triangle then shades the grid. Per-frame cost no longer depends on per-node rasterisation. Code: `NodeBenchmark.Density.cs`, `Shaders/density.*`.

| Nodes in the frame | Before: every node drawn (GPU p50 / p95, ms) | After: density grid (GPU p50 / p95, ms) | Speed-up |
|---|---|---|---|
| 1M | 1.01 / 2.58 | 0.040 / 0.040 | about 25x |
| 10M | 321.7 / 323.4 | 4.40 / 4.42 | about 73x |
| 100M | 5 107.7 / 5 146.4 | 68.7 / 68.9 | about 74x |

**What the fix does not solve.** The density cost still grows with node count, at about 0.69 ns per node. At 100M nodes it is about 69 ms per frame, which is about five times the 14 ms PC budget in section 6. At the 1B target it would be about 690 ms. The 1B target needs tile-level aggregates, so the grid is built from precomputed per-tile counts, not per-node atomics. That is the next step, and it belongs with the server tile format in section 3.

**Run-to-run variance.** Three repeats of the 10M triangle baseline gave 322.3, 323.4 and 323.7 ms, a spread of about 1.4 ms. The earlier full re-run gave 318 to 326 ms for the same row. Each density row was measured in a single run; repeat spread for the density path is not yet measured.

### 10.5 Per-tile aggregate path (2026-11-14)
Bench mode `aggregate`. Release build, Intel Arc B580. Nodes are generated from their global index and streamed in chunks of 16M nodes (256 MB). Each chunk is binned into one 512 x 288 grid, then discarded, so the whole graph never sits on the GPU at once. The per-frame cost is one fullscreen shade of the finished grid.

| Nodes | Build, one-time (wall ms, 3 runs) | Per-frame GPU p50 / p95 (ms, 3 runs) | Per-frame wall p50 (ms) |
|---|---|---|---|
| 100M | 1 017 / 873 / 902 | 0.025 / 0.030, 0.020 / 0.023, 0.025 / 0.025 | 0.27, 0.12, 0.14 |
| 1B | 9 660 / 9 648 / 9 632 | 0.059 / 0.060, 0.060 / 0.060, 0.059 / 0.060 | 0.33, 0.33, 0.32 |

**Against the budget.** The per-frame cost is about 0.06 ms at 1B, far inside the 14 ms PC budget in section 6. It no longer depends on node count.

**What the number does not include.**
- The build is paid once per graph version, not per frame. At 1B it is about 9.6 s on this PC, mostly CPU generation and copy. The GPU part is about 0.7 s. The server builds aggregates in batch, per section 3, so this cost moves there.
- The grid is built in the layout's fixed frame with the transform in force at build time. Panning or zooming does not re-bin it, so a view change needs either a re-bin or per-tile levels. Per-tile levels for zoom are the next step and are not built yet.
- Hit-testing and focus are not covered here.

**Variance.** Build time at 100M varies by about 15% across three runs (873 to 1 017 ms). At 1B it varies by about 0.3% (9 632 to 9 660 ms). Per-frame GPU p50 varies by about 25% at 100M (0.020 to 0.025 ms), and by under 2% at 1B. Earlier per-node density rows (section 10.4) were single runs, so their spread is still unmeasured.

### 10.6 Aggregate path re-run and the 1B frame spike (2026-11-18)
Bench modes `aggregate` and `spike`. Release build, Intel Arc B580. Same method as 10.5.

**Re-run (three runs each, GPU p50 and p95 in ms):**

| Nodes | One-time build (ms) | Per-frame GPU p50 / p95 | Per-frame wall p95 (ms) |
|---|---|---|---|
| 100M | 1 061 / 880 / 914 | 0.009 / 0.016, 0.010 / 0.010, 0.016 / 0.016 | 0.13, 0.23, 0.17 |
| 1B | 9 468 / 9 162 / 9 295 | 0.022 / 0.022, 0.018 / 0.018, 0.061 / 0.071 | 0.22, 0.21, 1.21 |

A second 1B re-run gave the same pattern: 0.018 ms in run 1, then 0.048 and 0.060 ms GPU p50 in runs 2 and 3, with wall p95 at 1.26 and 1.57 ms.

**Spike check.** One 1B build (9 299 ms), then three frame blocks with 60 s idle between them:

| Frame block | GPU p50 / p95 (ms) | Wall p95 (ms) |
|---|---|---|
| Immediately after the build | 0.026 / 0.027 | 0.28 |
| After 60 s idle | 0.059 / 0.072 | 1.29 |
| After a further 60 s idle | 0.059 / 0.071 | 1.36 |

**Reading.** The slow frames are real and repeatable, but their cause is not known. They are not explained by GPU clock recovery: idling for 60 s does not bring the fast time back. The grid and the shader are the same in every block, so the difference is in driver or runtime state, not in the data. The first block was fast, and every later block was slow.

**Next checks for the spike** (not yet run): time the same frame block in a fresh process without a build, to see if the build leaves state behind; count frames per block and compare the first 200 in a process with no prior work; and read the adapter's clock state before and after a block, if a tool for it is available on this machine.

Per-frame cost at 100M stays far inside the 14 ms PC budget in both states. At 1B the slow state is still about 0.06 ms, so the budget is not at risk from this spike.

### 10.7 Procedural topology benchmark: draw path and density path (2026-11-19)
Bench modes `topology-draw` and `topology-density`, Release, Intel Arc B580, offscreen 1280x720, 30 warm-up and 200 measured frames per cell (full per-frame splat: 10 frames at 100M, 3 at 1B and 2B, so its p95 and p99 are the maximum). Nodes and edges are generated in the shaders from the node index (`Shaders/topology.glsl`), so nothing is stored. Topologies: uniform random, clustered (64 gaussian blobs), power-law hub and spoke (1024 hubs), grid or lattice, chain or ring, each with one outgoing edge per node. 2D orthographic and 3D perspective (reversed-Z, no depth test), three viewpoints each (whole, mid zoom, close zoom). GPU times are the sum of per-pass timestamps; per-pass medians are in the CSV. Raw CSVs: `tests/Bazaar.Constellation.Vulkan.Bench/results/topo-draw.csv` (150 cells) and `topo-density.csv` (210 cells), one run each.

**Draw path** (M stride-sampled points plus 1 000 000 line segments, no culling). 76 of 150 cells meet GPU p95 <= 2.5 ms and p99 <= 3.3 ms. Cost does not depend on the node count: at M = 1M, N = 100M, 1B and 2B agree within a few percent for the same topology. The edge pass is the larger pass in 52 of the 74 failing cells: uniform random edges cost about 6 ms (2D whole), 6 ms (mid) and 17 ms (2D close); hub and spoke 4 to 7 ms in 2D whole and mid. Points cost about 0.42 ms per million when spread out, but pile up on a thin curve: the ring costs 4.2, 14.5 and 40.1 ms at 0.5M, 1M and 2M points in 2D whole (same-pixel overdraw).

**Density path.** Three variants, all ending in one fullscreen shade of a 1280x720 count grid:
- Full per-frame splat of every node, generated from its index: 3.1 to 74.7 ms at 100M (median 10.5), 37 to 918 ms at 1B (median 67), 58 to 2 316 ms at 2B (median 90). The worst cases are the grid and the ring in 2D, where many nodes land in one cell and the atomic adds serialise. None of the 30 cells meets the target.
- Aggregate: the graph is binned once into a voxel grid (2D 2048x2048, 3D 160^3, 16 MiB), then each frame projects the non-empty voxels into the screen grid. Build (one time) takes 4 to 1 438 ms. Clear, reproject and shade together cost at most 1.1 ms in any of the 90 cells (p50) and do not depend on N. 54 of 90 cells meet the target; in every one of the 36 misses the 1M-edge line pass is the larger part.
- Cached (camera still, grid already built): shade plus the edge pass only; 55 of 90 cells meet the target.

**Limits of what was measured.**
- The aggregate grid has a fixed resolution; at the close viewpoint one voxel covers many pixels, so a real close view would need a finer tile level. The cost shown for close is that of the coarse grid, not of a close-zoom tile selection.
- Edges are a fixed stride sample drawn every frame, not culled and not bundled; this is the baseline the culling and bundling work has to beat.
- The draw cells use no tile selection (`ViewSelector`) and no compute culling or indirect draws yet.
- Wall-clock p99 shows 5 to 70 ms hitches in many cells whose GPU p99 is under 1 ms (121 of 150 draw cells have wall p99 above 3.3 ms); the cause was not isolated.
- Single run per cell; run-to-run spread is not measured here (section 10.3 shows the small rows vary by up to a factor of two between runs).

### 10.8 Edge pass options (2026-11-19)
Bench mode `topology-edges`, same machine and method as 10.7, N = 1B, M = 1M points drawn every frame, 5 topologies x 2D and 3D x 3 viewpoints (30 cells per option; one run each). Section 11.1 asks for a default cap of 100k edges and no far-zoom line drawing. Results (GPU p95 <= 2.5 ms and p99 <= 3.3 ms counts as meeting the target):

| Option | Cells meeting | Edge pass p50, median / max (ms) | Total GPU p50, median / max (ms) |
|---|---|---|---|
| a. 100k edges, plain line list | 24 of 30 | 0.05 / 3.13 | 0.85 / 14.5 |
| b. 100k edges, screen-length LOD (drop below 2 px or above 400 px, or behind the camera) | 24 of 30 | 0.03 / 0.98 | 0.80 / 14.5 |
| b'. 1M edges with the same LOD | 19 of 30 | 0.36 / 5.71 | 1.51 / 14.5 |
| c. edges baked into the aggregate grid at build (4M sampled edges, 32 points each), no lines per frame (no points drawn either) | 30 of 30 | 0 | 0.14 / 0.65 |

- The 100k cap alone fixes most of the 1M-edge cost found in 10.7 (uniform 2D whole went from 6.0 ms to 0.6 ms). The remaining edge-pass cost is 3.1 ms for uniform 2D mid and close, because the edges are long.
- The screen-length LOD removes those long edges: uniform 2D close 3.1 ms to 0.02 ms, uniform 3D close 1.6 ms to 0.01 ms. It does not fix uniform 2D mid (still 3.07 ms p95 over the target), and it moves clustered and hub and spoke 2D whole slightly higher (edge pass 0.33 to 0.69 ms and 0.75 to 0.98 ms) because the vertex shader now projects both endpoints.
- With LOD, 1M edges still miss in 11 cells (hub and spoke 2D whole 10.5 ms, uniform 2D whole 9.4 ms): the LOD range keeps many mid-length edges.
- Option c has no per-frame edge cost. Its cost moves to build time (46 to 777 ms for the baked edges, on top of the node build in 10.7) and its image is edge density in the grid, not lines. It is also a different frame: it draws no points.
- The remaining misses in a and b are not edges. The ring topology piles its points on a thin curve (14.5 ms 2D whole, 8.6 ms 3D whole, 2.9 ms 3D mid), which is point overdraw, not edge cost.

### 10.9 Point overdraw, decimation and frame pipelining (2026-11-19)
Bench modes `topology-overdraw`, `topology-pipeline` and `hostjitter`. Same machine and method as 10.7 (one run per cell, 30 warm-up and 200 measured frames; the pipelining table uses 600 measured frames). 150 draw cells: N = 1B with M = 0.5M, 1M and 2M points, plus N = 100M and N = 2B at M = 1M, 5 topologies, 2D and 3D, 3 viewpoints. Edges are 100k with the screen-length LOD of 10.8 in every variant. "Meets" means GPU p95 <= 2.5 ms and p99 <= 3.3 ms. Raw CSVs: `results/topo-overdraw*.csv`, `results/topo-pipeline*.csv`.

**Cause.** The ring puts 400 to 800 points on each of about 2 400 pixels, and the rasteriser pays for the same-pixel writes: ring 2D whole costs 3.9, 14.5 and 40 ms for 0.5M, 1M and 2M points. 1 px points with no blending (option a) does not change this.

**Variants.**
- a. 1 px points, no blending: of the 30 cells that fail today, 18 meet; the ring whole views are unchanged.
- b. Density path when estimated points per pixel exceeds T: the aggregate path (clear, reproject, shade, 100k edges) costs at most 0.7 ms.
- c. Decimation: a compute pass lets one sampled point claim each 2x2-pixel cell (atomic max), a second pass appends the winners to a survivor list behind a draw-indirect header, then one indirect draw. Points outside the view are dropped. GPU p50 median 0.33 ms, max 3.6 ms.
- d. c, with b when points per pixel exceeds T. Points per pixel is estimated as M divided by the number of non-empty cells of the aggregate screen grid (counting costs at most 0.19 ms GPU and is not included in the frame totals).

| Variant | Cells meeting (GPU) |
|---|---|
| Plain draw, 100k LOD edges | 113 of 150 (the earlier 1M-edge draw: 76 of 150) |
| c. decimation | 148 of 150 |
| b. density path above T = 1 point per pixel over the plain draw | 150 of 150 (T = 2: 145, T = 4: 138) |
| d. decimation plus density above T = 2 | 150 of 150, worst p99 2.10 ms (T = 4: 149, T = 8: 148) |

Recommended threshold: T = 2 estimated points per pixel. The density path is a count image, not points, so the switch changes what is on screen. The two decimation misses are hub and spoke 2D mid (N = 1B, M = 2M, p99 4.19 ms, the LOD edge pass is 2.06 ms) and hub and spoke 3D whole (N = 100M, M = 1M, p99 3.64 ms, claim pass 0.98 ms). The decimation winner is the highest sample index in a cell, not the most important node.

**Wall-time hitches.** Fps is judged on wall time. With the benchmark at normal priority, wall p99 meets 3.3 ms in only 57 of 150 cells even under policy d, while GPU p99 is under it in all of them. Frames in flight (1, 2 and 3, draw frame with 500k points, N = 1B, 30 cells, 600 frames) lowers the wall p50 (median 0.90, 0.61, 0.38 ms) but not the p99 (median 6.81, 6.11, 5.38 ms; cells meeting 2, 2, 7 of 30). A host-only 0.5 ms busy-wait (`hostjitter`) shows the cause: p99 3.98 ms and max 24.4 ms at normal priority (110 of 6 000 intervals over 2 ms), against p99 0.506 ms and max 2.2 ms at High process priority and Highest thread priority. The machine runs CI runners and builds, so the thread is being preempted. With `BAZAAR_BENCH_BOOST=1` (High priority): the 30-cell pipeline table has wall p99 median 0.43, 0.37 and 0.41 ms for 1, 2 and 3 frames in flight and 29 of 30 cells meet 3.3 ms in each (the exception is one cell with 4.2 to 4.3 ms), so pipelining does not matter once the scheduler stops interfering. In the boosted overdraw table, plain draw meets in 135 of 150 cells (GPU) and 136 of 150 (wall p99), decimation in 150 and 149, and policy d (T = 1, 2 or 4) in 150 of 150 for both GPU and wall p99.

The real client therefore needs its render thread at a raised priority (or a dedicated high-priority thread) to hold a wall p99 near its GPU p99 on a busy machine.

### 10.10 Compute culling and indirect draws (2026-11-20)
Bench mode `topology-cull`, same machine and method as 10.9 (Intel Arc B580, offscreen 1280x720, 30 warm-up and 200 measured frames, one run per cell, `BAZAAR_BENCH_BOOST=1`, 100k LOD edges in every variant). 150 draw cells: N = 1B with M = 0.5M, 1M and 2M points, plus N = 100M and N = 2B at M = 1M, 5 topologies, 2D and 3D, 3 viewpoints. Raw CSV: `results/topo-cull.csv` (also the 10.9 baselines re-measured in this run). `vkCmdDrawIndirectCount` is supported on this device (drawIndirectCount, multiDrawIndirect, drawIndirectFirstInstance enabled).

**Path.** The M sampled points are binned once per (topology, N, M) into 4096 tiles (2D 64x64, 3D 16^3; counts, tight bounds, prefix sum, scatter; 4 compute passes). Per frame: a compute pass tests each tile (8-corner frustum test; with Hi-Z also a test against the previous frame's depth pyramid) and appends one `VkDrawIndirectCommand` per surviving tile; one `vkCmdDrawIndirectCount` draws them. Hi-Z variant: points are drawn with a reversed-Z depth buffer, then an 11-level 2x2-min pyramid (640x360 down to 1x1) is built by compute.

| Variant | Cells meeting (GPU p95 <= 2.5 ms, p99 <= 3.3 ms) | Cells meeting (wall p99 <= 3.3 ms) | GPU p50 median (ms) |
|---|---|---|---|
| cbase: plain draw | 133 of 150 | 122 | 0.565 |
| cdec: decimation + indirect draw (10.9 c) | 148 of 150 | 144 | 0.167 |
| ctile: tile cull + indirect count | 130 of 150 | 130 | 0.270 |
| cthiz: ctile + depth write + Hi-Z | 137 of 150 | 134 | 0.204 |
| cthizbig: cthiz, 24 px occluder points (3D, N = 1B, M = 1M, mid and close; 10 cells) | 9 of 10 | 8 | 0.474 |

**Results.**
- The cull pass itself is cheap: 3.3 to 7.6 microseconds p50 for 4096 tiles; the Hi-Z pyramid build 13 to 18 microseconds. Neither is a cost problem.
- Culling cuts work in the zoomed views (median 42 percent of the sampled points drawn; 2D close 0 to 1 percent) but does nothing in the whole views, where every tile is visible. The whole-view misses are therefore the same ones as before: ring (2D whole 3.5, 10 and 25 ms at 0.5M, 1M, 2M points) and clustered or hub 2D whole.
- Point overdraw is not fixed by tile culling (ctile misses 20 cells, mostly ring and clustered whole views). Decimation (10.9 c) is better in the 2D cases because it removes same-pixel points.
- Hi-Z with points as occluders removes almost nothing: tiles drawn were 155 051 of 156 012 frustum-visible (41 of 150 cells had any tile occluded); in 2D every depth is equal. With 24 px occluder points tiles are occluded (for example hub and spoke 3D close at N = 1B, M = 1M: 253 of 685 frustum-visible tiles drawn), so the test works, but the gain depends on having real occluders.
- cthiz is faster than ctile in several 3D cells (N = 1B, M = 1M: ring 3D mid GPU p50 1.82 ms to 0.63 ms, ring 3D whole 6.79 ms to 2.37 ms) because the depth test rejects the same-pixel writes early, not because tiles were culled. 2D has no such gain (ring 2D whole still 10 ms).
- Misses: ctile 20 cells, cthiz 13 cells, all in ring, clustered and hub 2D whole views and the 2M-point cells; cthiz worst GPU p99 26.2 ms (ring 2D whole, M = 2M).

**Conclusion.** For the target, the indirect-count path works and is cheap, but the thing that decides whether the B580 holds 400 fps is overdraw decimation (10.9 c, 148 of 150) combined with the density switch (10.9 d, 150 of 150), not tile culling. Tile cull plus indirect draw is the right shape for the real renderer (per-tile commands, resident tile pool), and belongs in front of decimation, but it was not measured with real tile selection: `ViewSelector` (#908) is not merged, so tiles here are a fixed grid over the sampled points.

**Limits.** One run per cell. Tiles are a uniform grid over a stride sample, not the quadtree or octree of section 3. Hi-Z occluders are the 1 to 4 px points themselves, so it is nearly idle by construction; real node discs or cluster impostors would occlude more. The Hi-Z frame uses a second rendering scope for edges and a depth attachment, so part of its difference from ctile is that. The cull path was not combined with decimation or the density switch in one frame.

### 10.11 Tile cull, decimation and density switch in one frame; Hi-Z occluders; background load (2026-11-20)
Bench mode `topology-combined`, same machine and method as 10.9 and 10.10 (Intel Arc B580, 1280x720, 30 warm-up and 200 measured frames, one run per cell, `BAZAAR_BENCH_BOOST=1`, 100k LOD edges). The same 150 draw cells. Tiles are still the fixed grid of 10.10: `ViewSelector` selection (#908) is not merged, so the cells do not use it yet. Raw CSVs: `results/topo-combined.csv`, `topo-combined-idle.csv`, `topo-combined-load.csv`. "Meets" means GPU p95 <= 2.5 ms and p99 <= 3.3 ms; wall means wall p99 <= 3.3 ms.

**The combined frame.** One command buffer: reset, tile cull (10.10), decimation over the kept tiles only (claim and select run one workgroup per kept tile: each in-view sampled point claims its 2x2-pixel cell, the winner is appended), then either the decimated points (one indirect draw) or the aggregate density path (clear, reproject, shade), then the 100k LOD edges. Both variants run the decimation passes, because their result is the overdraw estimate: points per occupied 2x2 cell = in-view sampled points in kept tiles divided by survivors. The density switch is made by the host from that number read back from a previous frame (the benchmark camera is static; a real renderer would use the previous frame counters, one or two frames late). Variants: `ccpts` (decimated points), `ccdens` (density path); the policy "density when points per cell > T" picks one per cell.

| Variant | Meets (GPU) | Meets (wall p99) | GPU p50 median (ms) | Worst GPU p99 / wall p99 (ms) |
|---|---|---|---|---|
| ccpts: cull + decimation + points | 150 of 150 | 150 of 150 | 0.157 | 1.98 / 2.29 |
| ccdens: cull + decimation + density path | 150 of 150 | 149 of 150 | 0.224 | 2.33 / 3.34 |
| 10.9 for reference: decimation alone | 148 of 150 | 144 of 150 | 0.167 | 3.03 / 15.47 |

- All 150 cells meet the GPU target with `ccpts` alone, including the cells that missed before: ring 2D whole at 0.5M, 1M and 2M points (GPU p50 0.24 ms at 1M, against 9.9 ms with tile cull alone and 0.74 ms with decimation alone), clustered 2D whole and hub and spoke 2D whole at 2M points. Decimation over the kept tiles is cheaper than over all M points because culled tiles are never touched.
- The density switch is not needed to meet the target on this GPU: policy T = 1, 2, 4, 8, 16 and 32 all give 150 of 150 GPU, and the same single wall miss (one density cell, wall p99 3.34 ms), so `ccpts` alone is the better result. The density path is still the right fallback for what points cannot show (a count image where many points share a 2x2 cell), but at these caps it is a quality choice, not a performance one.
- The estimator (claim plus select) costs 0.03 to 0.3 ms in the whole views; the whole combined frame is 0.04 to 1.0 ms p50.

**Hi-Z with realistic occluders (3D, 75 cells).** Points are drawn as splats sized from the density of the drawn points (about 1.2 times the square root of the screen area per drawn point, 1 to 48 px), so they cover the screen and can hide tiles. `ctilesz` is tile cull with those splats and no depth; `cthizsz` adds depth write and the Hi-Z test.

| Variant | Meets (GPU) | GPU p50 median (ms) | Worst GPU p99 (ms) | Tiles frustum-visible / drawn |
|---|---|---|---|---|
| ctilesz | 67 of 75 | 0.415 | 20.7 | 81 455 / 81 455 |
| cthizsz | 70 of 75 | 0.331 | 6.5 | 81 455 / 80 581 |

Hi-Z culled 1.1 percent of the tiles that passed the frustum test. Its cell-level win (33 cells more than 10 percent faster, 23 more than 10 percent slower) comes mostly from the depth test rejecting same-pixel writes early, as in 10.10, not from skipped tiles. Splat occluders do not hide much of a graph, whose nodes are sparse; Hi-Z is not worth carrying into the renderer until there are occluders that are large and dense (cluster impostors or filled regions). It is left out of the reusable API below.

**Background load (wall p99).** N = 1B, M = 1M, 30 cells, run with `BAZAAR_BENCH_BOOST=1`, alone and with a second process (`bgload`: a 60 Hz loop of about 8 ms of GPU draw work, plus two busy CPU threads, normal priority).

| Run | Variant | Meets (GPU) | Meets (wall) | Wall p99 median / max (ms) | GPU p99 median / max (ms) |
|---|---|---|---|---|---|
| alone | ccpts | 30 of 30 | 30 of 30 | 0.83 / 1.89 | 0.59 / 1.68 |
| alone | ccdens | 30 of 30 | 30 of 30 | 1.00 / 1.92 | 0.82 / 1.63 |
| with load | ccpts | 30 of 30 | 9 of 30 | 8.57 / 10.30 | 0.60 / 1.82 |
| with load | ccdens | 30 of 30 | 2 of 30 | 8.81 / 10.77 | 0.70 / 1.72 |

The GPU time of the frame is unchanged under load, but the wall p99 rises to 8 to 11 ms, about one frame of the other process GPU work: the queue is shared and the benchmark frame waits behind the other process draws. The raised priority of the benchmark process and thread does not help against that. The CPU and GPU load were not run separately, so how much of the rise is the CPU threads is not known. The 300 to 400 fps target cannot be guaranteed while another application keeps the GPU busy for 8 ms in one submission; the render thread would need the other work split into shorter submissions, or its own queue at a higher GPU priority (not tried).

**Reusable API.** `Bazaar.Constellation.Vulkan.TileCullPass` (`src/Bazaar.Constellation.Vulkan/TileCullPass.cs`, shader `Shaders/tilecull.comp.glsl`) is the part the renderer lifts; selection (`ViewSelector`, `TileDrawPlanner`, `ImportanceRanker`) stays with the renderer thread.
- `new TileCullPass(deviceApi, memoryProperties, maxTiles)`: creates a host-visible tile buffer, a device-local command buffer and the compute pipeline. The device needs Vulkan 1.2 `drawIndirectCount` and core `multiDrawIndirect` and `drawIndirectFirstInstance`.
- `UploadTiles(ReadOnlySpan<TileCullEntry>)`: one entry per tile, `(Min, Max, FirstInstance, InstanceCount)`: the box that bounds the tile instances and its slice of the instance list. Do not call while a frame that reads the buffer is in flight.
- `RecordCull(cb, viewProjection, verticesPerDraw)`: outside a rendering scope; resets the counters, dispatches one thread per tile (frustum test of the 8 box corners, clip depth range 0 to w, so reversed Z works) and records the barrier before the indirect draw. `viewProjection` is the System.Numerics matrix the renderer already pushes.
- `RecordDraw(cb)`: inside a rendering scope with the renderer pipeline bound: one `vkCmdDrawIndirectCount`. The vertex shader reads instance `gl_InstanceIndex`, which includes `FirstInstance`.
- `TileCullPass.IsVisible(min, max, viewProjection)`: CPU reference of the shader test. Six unit tests in `TileCullPassTests`, and a GPU parity check, bench mode `tilecull-check` (200 random camera and box sets, 3 000 tiles: draw count and instance total match the CPU reference in all).
- Not in it: the Hi-Z test (bench shader `tile_cull.comp`), decimation (bench shaders `claim_tiles.comp`, `select_tiles.comp`) and the density switch. Decimation is the piece that made all 150 cells meet; it can be lifted next as a second pass over the commands `RecordCull` writes (one workgroup per command), see the bench.

**Limits.** One run per cell. Tiles are a uniform grid over a stride sample, not the quadtree or octree; real selection from #908 will change tile sizes and counts, and the cull and decimation costs with them. The density switch is decided on the host from a previous frame counters. The background load is one kind of game-like load (60 Hz, 8 ms GPU, two spinning threads); other loads can give other wall times.

### 10.12 Wall-time p99 under a busy GPU: what helps and what the driver allows (2026-11-20)
Bench mode `topology-contention`, same machine and method as 10.11 (Intel Arc B580, driver 0.406.842, Windows, 1280x720, `BAZAAR_BENCH_BOOST=1`). The frame is the points variant of the combined frame of 10.11 (tile cull, decimation, points, 100k LOD edges), N = 1B, M = 1M, 30 cells (5 topologies, 2D and 3D, 3 viewpoints), 30 warm-up and 400 measured frames, one run per cell. The load is a second process (`bgload`, normal priority): "GPU" is a 60 Hz loop of about 8 ms of GPU draw work (19M points in one submission), "CPU" is busy threads. "Meets" means wall p99 <= 3.3 ms. Raw CSVs: `results/contention/<load>-<variant>.csv`; the table gives, per run, cells meeting, the median over cells of wall p99, and the worst cell.

**Which load hurts.**

| Load | Cells meeting | Wall p99 median / worst (ms) |
|---|---|---|
| none | 30 of 30 | 0.78 / 1.90 |
| CPU only, 2 busy threads | 30 of 30 | 0.86 / 1.91 |
| CPU only, 4 busy threads | 30 of 30 | 0.69 / 1.72 |
| CPU only, 8 busy threads | 30 of 30 | 0.80 / 1.64 |
| GPU only (8 ms, 60 Hz) | 12 of 30 | 7.23 / 10.27 |
| GPU 8 ms + 2 CPU threads (the 10.11 load) | 10 of 30 | 7.33 / 10.05 |

The CPU threads do nothing to the frame, with the benchmark at High priority (8 threads on the machine included). The whole wall p99 rise comes from the other process GPU work: a frame that arrives while the other 8 ms submission is running waits for it to finish (7 to 10 ms, about one other frame), a few percent of the time, which is exactly what p99 sees. GPU timestamps of the frame itself stay at 0.6 ms.

**Mitigations, GPU 8 ms load alone and with 2 CPU threads** (cells meeting / median wall p99 in ms; the baseline is the first row):

| Variant | GPU only | GPU + 2 CPU |
|---|---|---|
| baseline (1 submission, 1 frame in flight) | 12 / 7.23 | 10 / 7.33 |
| global queue priority HIGH (VK_KHR_global_priority) | 12 / 7.12 | 8 / 8.54 |
| global queue priority REALTIME | 10 / 7.67 | 7 / 7.54 |
| frame split in 2 submissions | 10 / 8.55 | 9 / 7.75 |
| frame split in 4 submissions | 5 / 8.33 | 5 / 7.41 |
| 2 frames in flight | 17 / 1.74 | 17 / 2.22 |
| 3 frames in flight | 17 / 2.08 | 16 / 3.29 |
| HIGH priority and 3 frames in flight | not run | 16 / 3.26 |

- **Global priority.** The driver exposes VK_KHR_global_priority (core in Vulkan 1.4, plus the EXT variant and the query feature) and the device was created with HIGH and with REALTIME without error, so both were granted. Neither changed the wall p99 (7.1 and 7.7 ms against 7.2 ms alone; the other process is at the default priority). So on this driver and under WDDM the request is accepted but does not make our work run ahead of, or preempt, the other process 8 ms packet. I cannot tell from the API whether the value is used at all; no effect was measured.
- **Dedicated or async compute queue: not possible on this device.** The B580 reports one queue family (graphics, compute, transfer, sparse) with one queue, and the integrated GPU the same. There is no second queue to put the cull and decimation on, so async compute was not tested. Cull and decimation cost 0.04 to 0.3 ms here in any case.
- **Smaller submissions do not help and slightly hurt.** 2 and 4 submissions per frame leave the p99 at 7 to 8.5 ms and drop the cells meeting from 12 to 10 and 5 (the p50 rises from 0.28 to 0.30 and 0.37 ms because every submission has fixed cost). The wait behind the other process is for the time slice that is already running; shorter command buffers on our side do not shorten it.
- **Frames in flight help the cadence, not the wait.** With 2 or 3 frames in flight the interval between successive frame submissions has a median wall p99 of 1.7 to 3.3 ms and 16 to 17 cells meet, because the CPU keeps submitting while one frame waits. The latency of the frame that was behind the other 8 ms work is still 8 to 10 ms (worst-cell p99 is 10 to 11 ms in every run, and the longest single interval 13 to 16 ms), so what the viewer sees is a frame that is late by one other-process frame a few percent of the time, with the other frames earlier. The 17 of 30 cells are those whose own frames are short enough for the queue to catch up. This is the only mitigation that moved the number, and it should be read as cadence, not as frame latency.

**What the OS allows.** Between two processes on WDDM the GPU is time-sliced by the scheduler; a Vulkan application cannot raise its process scheduling class from the API, and the Vulkan global priority did nothing measurable here. Things not tried: D3DKMT scheduling priority class (needs elevated rights), hardware-accelerated GPU scheduling settings, the other process using shorter submissions (the game side, not ours), and presenting from a swapchain with a fixed cadence that hides a late frame behind the previous one. For the renderer the practical answer is to keep 2 or 3 frames in flight and to measure cadence and latency separately; a hard 3.3 ms wall p99 cannot be held while another application keeps the GPU busy for 8 ms in one go.

**Limits.** One run per cell and per variant. One load shape (60 Hz, 8 ms, one submission); heavier or lighter games give other numbers, and the share of frames hit by the other process rises with its duty cycle (about 8 of 16.7 ms here). Frames in flight are measured as the interval between submissions, not as time to completion. Tiles are still the fixed grid of 10.10 (#908 is not merged).

## 11. Frame-rate target: 300+ fps, 400+ preferred, at billions of nodes, 2D and 3D (PROPOSED, owner request 2026-11-19)

**Source.** Owner message, 2026-11-19 07:59 UTC, relayed by the coordinator: the Intel Arc B580 (12 GB, a midrange card) should do 300+ fps, preferably 400+, "no matter what", tested with a couple of billion nodes with varied connections, in 2D and 3D.

**What this changes in section 6.** The 60 fps rows stay as the floor for phones. On the PC tier the target becomes:

| Item | PC tier target |
|---|---|
| Frame time, p95, GPU | 400 fps: ≤ 2.5 ms; floor 300 fps: ≤ 3.3 ms |
| Frame time, p99 | ≤ 3.3 ms (no hitch below 300 fps) |
| Instances drawn per frame | hard cap, default 2 000 000 (the B580 draws 2M in about 1 ms, section 10), operator-tightenable |
| Edges drawn per frame | hard cap, default 1 000 000 line segments, bundled or sampled beyond that |
| Graph size | no term: frame cost depends on the caps and the view, never on node count |

**Method.** Out-of-core and bounded per frame; none of this touches data the view cannot show.
1. **Selection.** Each frame the client picks a bounded set of tiles by walking the tile tree from the root, splitting the largest on-screen tile first, stopping at the instance cap, the tile cap, or the pixel threshold (below 96 px a tile is drawn from its sample). The CPU reference is `ViewSelector` in `Bazaar.Constellation.Rendering.Core`; the GPU compute pass must produce the same set and is tested against it.
2. **Tree.** 2D uses the quadtree, 3D the octree from section 3 (tile format v1 already carries `dimensions`). 3D uses a perspective camera and frustum culling; far tiles collapse to density splats and cluster impostors.
3. **Far and mid.** A density field (compute splat, section 5) and per-tile sampled nodes. Edges are drawn from per-tile bundled summaries (cluster-to-cluster ribbons), not per edge, until a tile is near.
4. **Near.** All nodes and their real edges for the few visible leaf tiles, under the caps.
5. **GPU-driven draw.** Compute culling writes indirect arguments; one indirect-count draw per layer. Tile data resident in a fixed GPU pool with LRU, streamed under the section 6 bandwidth limit. A frame that would exceed a cap drops detail (coarser tiles), never frames.
6. **Not yet measured on the B580:** swapchain present cost, compute culling, density splat, indirect count draws, mesh shaders, 3D. These decide whether 400 fps holds or only 300; they are the first benchmark milestone.

**Benchmark (extends section 7).** A synthetic generator (`ImplicitTileTree` for tile structure, so nothing is stored) at 100 million, 1 billion and 2 billion nodes. Topologies: uniform, clustered (Louvain-like blobs), power-law hubs, grid, chain/ring. Modes: 2D orthographic and 3D perspective, each with a fixed pan/zoom/orbit script. Report GPU p50, p95 and p99 per frame, instances and tiles per frame, and tile bytes streamed. Pass: p99 ≤ 3.3 ms and p95 ≤ 2.5 ms at every size. The owner's PC runs it (benchmark thread).

**Honest limit.** "No matter what" is met by bounding work per frame, so detail drops under extreme views rather than fps. Whether the B580 reaches 400 rather than 300 is not known until item 6 is measured.
