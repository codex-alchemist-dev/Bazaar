# Constellation renderer extraction: what moves, what stays

Status: owner direction recorded (2026-11-19/20); the plan below is PROPOSED · Supersedes the "not triggered" state of: 49-gap-decisions-operations.md section 10 (for Constellation.Renderer only; Kodo is unchanged) · Extends: constellation-render-core.md (boundary), 41b-client-module-boundaries.md (shared renderer row), 47-licensing-and-trademarks.md (section 7.1), 46-kodo-health-module.md (section 18.7) · Enforced today by: `tests/Bazaar.Architecture.Tests/ConstellationRenderCoreBoundaryTests.cs`

## 1. Owner direction and its effect on older docs

**[DECIDED] (owner, 2026-11-19/20).**
1. Only the renderer (Constellation.Renderer) is extracted into its own shared repository.
2. The Constellation module stays in Bazaar: the knowledge-graph views, the Vault, the tile sources and the Bazaar scene adapter.
3. The shared repository has no dependency on Bazaar and none on any other shared repository, beyond the contracts it declares itself.
4. The core uses only the .NET base class library (BCL). It has a Vulkan backend and a Skia fallback.
5. Licence AGPL-3.0, contributions under CLA plus DCO (doc 47, section 7.1, row "Extracted repos"). No Bazaar names in package IDs.

**[REJECTED / CHANGED].** Doc 49 section 10 said extraction waits for an explicit owner request. The request has now been made for the renderer, so that wait is over for this module only. Doc 49 also lists a Kodo-style split into core, probes, storage, API and UI pane packages; that split does not apply here (section 4 gives the renderer's own package list). Docs 41b and BAZAAR-COMPONENTS call the module `Bazaar.Client.Constellation.Renderer` and describe an "adapter in its own repo". Both are CHANGED: the renderer's repo and packages carry no Bazaar name, and the adapter lives in Bazaar, not in the shared repo. Those docs should be edited to match when this one is accepted (section 9, step 11).

**Note on scope.** The Vulkan backend and the Skia fallback are one shared repo, not three. A Skia fallback does not exist in the code today (section 3.4).

## 2. What exists on main today

Four projects hold renderer code. This section lists them as of origin/main on 2026-11-20.

| Project | Role | Bazaar references |
|---|---|---|
| `src/Bazaar.Constellation.Rendering` | Generic core in `Core/`, plus three Bazaar-specific files in the parent folder | None (the project file declares no `ProjectReference`; checked by the architecture test) |
| `src/Bazaar.Constellation.Vulkan` | Vulkan backend | Project reference to Rendering; package references to `Microsoft.Extensions.DependencyInjection` and `Vortice.Vulkan` |
| `src/Bazaar.Constellation.Abstractions` | Tile and graph source interfaces (server side feed) | Not part of the renderer. Stays. |
| `src/Bazaar.Client.Constellation` | The Avalonia pane, the only client of Rendering | Stays. |

**Names the brief lists that are not on main.** The owner's list mentions a tile tree, `ViewSelector`, `TileDrawPlanner`, `ImportanceRanker`, `HiZPyramid`, `OcclusionCuller`, `FrameBudgetController` and `NightSkyTheme`. A search of origin/main on 2026-11-20 finds none of these types. They are presumably on unmerged branches. They are listed in section 3.1 as "moves when merged", and the extraction must not start until they are merged or the owner accepts leaving them out (section 8, item 1).

## 3. Exact move list

### 3.1 Moves to the shared repository

Core (BCL only), from `src/Bazaar.Constellation.Rendering/Core/`, all 14 files:

| File | Types |
|---|---|
| `Camera2D.cs` | `Camera2D` |
| `FrameInterpolator.cs` | `FrameInterpolator` |
| `FrameSequencer.cs` | `FrameSequencer` |
| `IPickIndex.cs` | `IPickIndex` |
| `LevelOfDetail.cs` | `LodTier`, `LevelOfDetail` |
| `NodeInstanceBuffer.cs` | `NodeInstanceBuffer` |
| `NodeStyle.cs` | `NodeStyle` |
| `NodeStylePalette.cs` | `NodeStylePalette` |
| `NodeStyleTable.cs` | `NodeStyleTable` |
| `RenderFrame.cs` | `RenderFrame` |
| `RenderLayer.cs` | `RenderLayer` |
| `RenderPrimitiveKind.cs` | `RenderPrimitiveKind` |
| `RenderTypes.cs` | `RenderSurfaceKind`, `RenderSurface`, `RenderAttachResult`, `RendererCapabilities` |
| `SpatialGrid.cs` | `SpatialGrid` |

(The Core folder has no other files on main.)

Core types named in the brief that move when merged: tile tree, `ViewSelector`, `TileDrawPlanner`, `ImportanceRanker`, `HiZPyramid`, `OcclusionCuller`, `FrameBudgetController`, `NightSkyTheme`. Each goes into the same core project.

Vulkan backend, from `src/Bazaar.Constellation.Vulkan/` (all except one file, see 3.2):

- `VulkanConstellationRenderer.cs`, `VulkanSwapchain.cs`, `VulkanOffscreenRenderer.cs`, `VulkanProbe.cs`, `NodePipeline.cs`, `NodeStylePacking.cs`, `NodeInstanceVertexLayout.cs`
- `Shaders/node.vert.glsl`, `Shaders/node.frag.glsl` and the compiled `node.vert.spv`, `node.frag.spv` (embedded resources; the resource names start with `Bazaar.Constellation.Vulkan.Shaders.` and are renamed)
- the project file, minus the `Microsoft.Extensions.DependencyInjection` package reference

Tests and benches that move with them:

- `tests/Bazaar.Constellation.Rendering.Tests/`: `Camera2DTests`, `FrameInterpolatorTests`, `FrameSequencerTests`, `LevelOfDetailTests`, `NodeInstanceBufferTests`, `NodeStylePaletteTests`, `NodeStyleTableTests`, `NodeStyleTests`, `RenderFrameTests`, `RenderLayerTests`, `SpatialGridTests`, `SpatialGridPickTests`, `SpatialGridQueryTests`
- `tests/Bazaar.Constellation.Vulkan.Tests/`: `NativeTestWindow`, `NodeInstanceVertexLayoutTests`, `NodeStylePackingTests`, `VulkanConstellationRendererTests`, `VulkanOffscreenTests`, `VulkanProbeTests`, `VulkanSwapchainTests`, `VulkanWin32SwapchainTests`
- `tests/Bazaar.Constellation.Bench/` (`Program.cs`, `BenchNodeCounts.cs`)
- `tests/Bazaar.Constellation.Vulkan.Bench/` (`Program.cs`, `NodeBenchmark.cs`, `Shaders/nodes.*`)
- `tests/Bazaar.Constellation.Vulkan.Bench.Android/` (`MainActivity.cs`, which links `NodeBenchmark.cs` and the bench shaders)
- The generic half of `tests/Bazaar.Architecture.Tests/ConstellationRenderCoreBoundaryTests.cs` (section 7)

### 3.2 Stays in Bazaar

| File or project | Why it stays |
|---|---|
| `src/Bazaar.Constellation.Rendering/ConstellationScene.cs` | Carries Constellation cluster ids. Becomes the Bazaar scene type used by the pane (section 5). |
| `src/Bazaar.Constellation.Rendering/ConstellationNodeStyles.cs` | The fixed 8-colour palette keyed by Bazaar cluster ids. The shared repo ships no cluster palette. |
| `src/Bazaar.Constellation.Vulkan/BazaarConstellationVulkanServiceCollectionExtensions.cs` | Bazaar DI registration (`AddBazaarConstellationVulkan`). It carries the Bazaar name and the only `Microsoft.Extensions.DependencyInjection` use. |
| `tests/Bazaar.Constellation.Rendering.Tests/ConstellationSceneTests.cs`, `ConstellationNodeStylesTests.cs` | Test the two files above. |
| `tests/Bazaar.Constellation.Vulkan.Tests/BazaarConstellationVulkanServiceCollectionExtensionsTests.cs` | Tests the DI registration. |
| `src/Bazaar.Constellation.Abstractions/`, `src/Bazaar.Client.Constellation/`, `src/Bazaar.Client.Constellation.Abstractions/`, the server `Constellation/` tree | The Constellation module (views, tiles, layout, Git DAG, Vault links). |
| `Bazaar.slnx` entries, the DI call in host composition, `src/Bazaar.Client.Linux/Program.cs` and any other composition root | Updated to point at packages (section 9). |

After extraction, `src/Bazaar.Constellation.Rendering` is removed as a name. Its three Bazaar files move into a new Bazaar project, `src/Bazaar.Client.Constellation.Rendering` (OPEN, recommended; they could also fold into `Bazaar.Client.Constellation`, which is the only client of the scene type, but a separate project keeps the adapter testable on its own).

### 3.3 Needs a decision before the move

- `VulkanSwapchain.cs` and `VulkanConstellationRenderer.cs` use the adapter namespace today (`using Bazaar.Constellation.Rendering;`). The backend only needs `NodeCount` from the scene (it reads styles through `SetNodeStyles`), so the scene type is replaced by a neutral one (section 5). No other adapter use was found.
- `RenderFrame` and `FrameSequencer` still use the ordering keys `GraphVersion` and `LayoutSeq` (constellation-render-core.md section 4, item 1). Rename before the move (section 9, step 2).

### 3.4 Skia fallback

No Skia renderer exists in the repo today. The Avalonia pane draws through its own path. The shared repo gets a Skia fallback project, which is new work and not a move. It is written against the same core interface, and is gated behind the Vulkan backend working in the shared repo first. Recommended: ship the first shared release with Vulkan only and add Skia in the second (OPEN, section 8, item 3).

## 4. Shared repository layout and package IDs

**[PROPOSED].** No Bazaar names in repository, package, namespace or assembly names (owner direction). "Constellation" is not a Bazaar name; the repo name itself is OPEN (section 8, item 2). Recommended:

| Package ID | Contents | Dependencies |
|---|---|---|
| `ConstellationRenderer.Core` | The 14 core files plus the merged core types (`Rendering/Core` becomes the `ConstellationRenderer.Core` namespace), and the `IConstellationRenderer` contract and `RenderScene` type | .NET BCL only |
| `ConstellationRenderer.Vulkan` | The Vulkan backend and shaders | Core, `Vortice.Vulkan` |
| `ConstellationRenderer.Skia` | The Skia fallback (later) | Core, `SkiaSharp` |

No Microsoft.Extensions dependency anywhere. Registration into a DI container is the host's job (section 5.3). Repository folders follow the owner's names: `src/` for each package, with the Vulkan project at `src/Bazaar.Constellation.Vulkan` in the brief renamed to `src/ConstellationRenderer.Vulkan` (OPEN: the brief keeps the old path; since package IDs may carry no Bazaar name, the folder should not either).

The repository carries: AGPL-3.0 `LICENSE`, `NOTICE`, `CONTRIBUTING.md` (CLA plus DCO, copied from doc 47 section 7), its own CI, its own `global.json` pinned to the same .NET 11 SDK, central package management, an SBOM and signed releases (doc 49 section 10, step 4). Copyright line: "The Bazaar Authors" is a Bazaar name; the owner decides the holder name for the new repo (OPEN, section 8, item 4).

## 5. The contract seam

### 5.1 What crosses the boundary

The shared repo owns one interface, kept under its current name:

```
IConstellationRenderer : IDisposable
    RendererCapabilities Capabilities { get; }
    RenderAttachResult Attach(RenderSurface surface);
    void SetScene(RenderScene scene);
    bool SubmitFrame(RenderFrame frame, double nowSeconds);
    void Resize(int widthPixels, int heightPixels);
    void Draw(double nowSeconds);
```

Only `SetScene` changes from today (`ConstellationScene` becomes `RenderScene`). The backends also expose `SetNodeStyles(NodeStyleTable)`; it moves onto the interface so the adapter can set styles without knowing the backend type (OPEN: today it is only on the Vulkan class; recommended to add it to the interface).

### 5.2 Scene input types

| Type | Home | Shape |
|---|---|---|
| `RenderScene` | Shared repo | Node count, edge index pairs, validated exactly as `ConstellationScene` is today (even pair count, endpoints in range). No cluster ids. |
| `RenderFrame` | Shared repo | Positions per node with neutral sequence keys |
| `NodeStyleTable`, `NodeStyle`, `NodeStylePalette` | Shared repo | Per-node style by index; colour, radius, ring width |
| `ConstellationScene` | Bazaar | Same fields as today, with cluster ids. Can produce a `RenderScene`. |

### 5.3 The adapter that stays in Bazaar

A small Bazaar project (section 3.2) holds:
- `ConstellationScene` (cluster ids included) and a method that returns the matching `RenderScene`;
- `ConstellationNodeStyles`, which now builds the `NodeStyleTable` from a `ConstellationScene` and passes it to `SetNodeStyles`;
- `AddBazaarConstellationVulkan`, which registers the shared `VulkanConstellationRenderer` as `IConstellationRenderer`;
- the pane code in `Bazaar.Client.Constellation`, which maps server tile frames to `RenderFrame` (it already does; only the `using` lines and package reference change).

The direction is one-way: Bazaar depends on the shared packages, never the reverse. Bazaar pins an exact package version (doc 49 section 10, step 5).

## 6. Licence and contribution terms

**[DECIDED] (owner, 2026-11-19/20; doc 47 section 7.1).** AGPL-3.0, CLA plus DCO for every contribution. Code that moves already has "The Bazaar Authors" copyright. Moving it with the history keeps authorship; the CLA covers new contributions only. OPEN: a counsel check that the original contributors' sign-offs allow the move (recommended: ask counsel together with the first-public-release review, doc 47 section 12).

## 7. Architecture-test changes

**[PROPOSED].**

In the shared repo, the existing rules become the repo's own tests, with names changed:
- Source rule: core files use only the core namespace and no other namespace beyond the BCL. The deny list changes from `IConstellationRenderer`/`ConstellationScene` to "any `Bazaar.*` name".
- Project rule: the core project file declares no `ProjectReference`, and no package reference at all.
- Assembly rule: the compiled core assembly references only BCL assemblies.
- New rule: no package ID, namespace or assembly name contains `Bazaar`.
- New rule: the Vulkan project references only Core and `Vortice.Vulkan`.
- New rule: no `Microsoft.Extensions.*` reference in any renderer project.

In Bazaar, `ConstellationRenderCoreBoundaryTests` and its `ArchitectureRules` helpers for the core are deleted from the Bazaar test project once the shared repo has them. Replaced by:
- No file under `src/` or `tests/` in Bazaar defines the moved types (a leftover-copy check).
- The adapter project may reference the shared packages and may not be referenced by them (impossible, but test the package metadata for `Bazaar` anyway).
- Only host composition roots and `Bazaar.Client.Constellation` reference the shared renderer packages. This keeps modules such as RyuseiFuda on the contract path of doc 41b, and not on the Vulkan package.
- The existing seeded-violation test pattern is kept for each new rule.

## 8. Open questions

1. **Types not on main.** Tile tree, `ViewSelector`, `TileDrawPlanner`, `ImportanceRanker`, `HiZPyramid`, `OcclusionCuller`, `FrameBudgetController`, `NightSkyTheme`. OPEN. Recommended: merge them (or decide which to drop) before step 3, so the repo is created complete and the history does not split.
2. **Repository and package names.** OPEN. Recommended: repo `constellation-renderer`, packages as in section 4. A clearance search has not been done for "Constellation"; the owner decides before the first package is published (same caution as doc names in CLAUDE.md).
3. **Skia timing.** OPEN. Recommended: Vulkan first, Skia in the second release.
4. **Copyright holder and organisation for the new repo.** OPEN. Recommended: the owner's organisation, with the holder name chosen by the owner.
5. **Git history.** OPEN. Recommended: `git filter-repo` to carry the history of the moved paths only. The repo has chat-derived notes to purge before first public release (CLAUDE.md); the filtered paths contain none, and the result is checked for secrets and private material before anything is pushed.
6. **`SetNodeStyles` on the interface** (section 5.1). OPEN. Recommended: yes.
7. **Shader build.** The `.spv` files are committed next to the `.glsl`. OPEN whether the shared CI recompiles them. Recommended: keep committed files and add a check that the `.glsl` and `.spv` match.
8. **Versioning.** OPEN. Recommended: SemVer from `0.1.0`, independent of Bazaar's `v00.MM.PP` display, per doc 49 step 5.

## 9. Ordered steps

Each step is a small PR. Bazaar builds and tests pass after every step. Nothing is published before step 8.

1. **Record the request.** Add the 2026-11-19/20 owner direction to DECISIONS-LOG, with the licence and the repo name once chosen. (Doc 49 step 1.)
2. **Neutral names.** Rename `GraphVersion` and `LayoutSeq` in `RenderFrame` and `FrameSequencer`; update their tests.
3. **Merge or drop the missing core types** (section 8, item 1) into `Rendering/Core`. Make `Core` a single project in place: split `src/Bazaar.Constellation.Rendering` into a core project (the 14 files plus merged types) and the three Bazaar files. The architecture test now targets the core project.
4. **Introduce `RenderScene`.** Add it to the core. Change `IConstellationRenderer` to take it (and add `SetNodeStyles`). Make `ConstellationScene` produce one. Update the Vulkan backend and the pane.
5. **Take DI out of the Vulkan project.** Move `AddBazaarConstellationVulkan` to the Bazaar adapter project. Drop the `Microsoft.Extensions.DependencyInjection` package reference from the Vulkan project. Update `BazaarConstellationVulkanServiceCollectionExtensionsTests`.
6. **Rename inside the repo.** Rename the namespaces, assemblies and shader resource names of the moving projects to the neutral names (section 4). Update every consumer and the `.slnx`.
7. **Create the shared repo.** Use the filtered history (section 8, item 5). Add licence, CLA and DCO files, CI, SBOM and signing. Move the files and tests in section 3.1. CI builds and tests there with the Vulkan tests that need a device marked as the repo already marks them.
8. **Publish a first pre-release** under SemVer, after the owner clears the name (section 8, item 2) and counsel's note (section 6).
9. **Switch Bazaar to the packages.** Pin the version. Delete the moved projects, tests, benches and the generic architecture rules. Add the leftover-copy and package-metadata rules (section 7). Update `Bazaar.slnx`, central package versions and composition roots.
10. **Verify.** Both repositories build and pass; the Bazaar pane draws through the package on the owner's Windows machine; the Vulkan bench runs from the shared repo; the compatibility matrix shows the pinned version.
11. **Fix the docs.** Update docs 41b, 46 (section 18.7, drop Constellation.Renderer from the "stay first-party" sentence), 47 (row stays), 49 (section 10, split Kodo from the renderer), BAZAAR-COMPONENTS, DECISIONS-LOG line "Extraction", and constellation-render-core.md (mark sections 4 and 5 done or superseded).
12. **Skia fallback** in the shared repo (section 3.4), then bump the Bazaar pin.

## 10. Risks

1. **Unmerged core types** (section 8, item 1). Extracting before they merge splits their history and forces a second move. Mitigation: order in section 9, step 3.
2. **Pre-1.0 contract churn.** The GPU instance layout is not frozen (constellation-render-core.md section 4, item 3). A frozen public contract too early forces breaking releases. Mitigation: pre-1.0 versions with a changelog; Bazaar pins exact versions.
3. **Two repos, one change.** A feature that touches both the scene adapter and the renderer needs two PRs and a version bump. Mitigation: a Bazaar local-feed build (project reference override) for development only; releases always use pinned packages.
4. **Licence mismatch.** AGPL-3.0 on a library that other Bazaar modules and later projects consume pushes AGPL obligations on them. Bazaar is AGPL already, so Bazaar itself is fine; third parties are not. Mitigation: counsel review (doc 47) and the commercial option in the same doc.
5. **History carries private material.** Filtered history could still include chat-derived text in comments. Mitigation: a manual and automated scan before the first push (section 8, item 5).
6. **GPU tests need hardware.** The Vulkan tests (swapchain, offscreen, Win32) need a device or window. Shared CI may not have one. Mitigation: keep the existing skip behaviour, run the device tests in a labelled job on the owner's machine, and report skipped tests as skipped.
7. **Name clearance.** "Constellation" in a public package ID has had no search (section 8, item 2).
8. **Skia drift.** A fallback built later may not match the Vulkan output. Mitigation: one shared test suite for both backends that compares offscreen output on a small scene.
9. **Rename churn.** Namespace and assembly renames (step 6) touch many files and the client composition roots. Mitigation: mechanical rename in its own PR with no behaviour change.
