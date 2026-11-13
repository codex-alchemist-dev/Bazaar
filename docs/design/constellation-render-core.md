# Constellation render core: boundary and extraction steps

Status: PROPOSED (owner asked for preparation inside the repo, not extraction) · Extends: 19-constellation-and-git-solar-system.md (renderer and client boundary) · Related: constellation-scale-and-lod.md (tiles and level of detail) · Enforced by: `tests/Bazaar.Architecture.Tests/ConstellationRenderCoreBoundaryTests.cs`

## 1. Purpose

The Constellation renderer is a bulk-node rendering core: it takes node positions, edges and per-node style data, keeps a bounded working set chosen by the camera, and hands draw lists to a backend. That part is generic. The owner wants it to become a reusable library later, extracted as a standalone package. Today it is only prepared inside the repo: its generic code sits in one namespace with a strict boundary, and nothing is published or split into its own project.

## 2. The boundary

The renderer project is `src/Bazaar.Constellation.Rendering`. It has two halves.

**Generic core** (namespace `Bazaar.Constellation.Rendering.Core`, folder `src/Bazaar.Constellation.Rendering/Core/`). It holds the camera, frame sequencing and interpolation, level-of-detail choice, render frames and their value types, and the spatial grid. More types are being added in parallel (node style table, node instance buffer, render layers, primitive kinds, and a pick index interface). Core code:
- uses only the .NET base class library, and no other Bazaar namespace or assembly;
- does not name any adapter type, not even in comments or `cref` tags.

**Constellation adapter** (namespace `Bazaar.Constellation.Rendering`, the parent folder). It holds `ConstellationScene`, which carries Constellation cluster ids, and `IConstellationRenderer`, the thin boundary between the view and a drawing backend. The adapter is the only part that knows about:
- the mapping from server tile frames in `src/Bazaar.Client.Core/Constellation/`;
- Solar, orbit and comet semantics for the Git Solar System;
- component ids, configuration, and the rendering settings the client sets;
- DI registration, which lives in `src/Bazaar.Constellation.Vulkan/` (`BazaarConstellationVulkanServiceCollectionExtensions`). That project is the Vulkan backend and is Bazaar-specific.

The rule is one-directional. The adapter may use the core; the core may never use the adapter.

## 3. Rules enforced now

`ConstellationRenderCoreBoundaryTests` runs these rules in the architecture test project. Each rule is a pure function in `ArchitectureRules`, with seeded-violation tests that prove it fires.

- **Source rule.** Every `.cs` file under `Core/` may use only `Bazaar.Constellation.Rendering.Core` (and its children) as a Bazaar namespace. Its namespace declarations must be in the core namespace. No line may name `IConstellationRenderer`, `ConstellationScene` or `VulkanSwapchain`, or the Rendering namespace outside the core. The rule covers comments and `cref` tags. The real-tree test also asserts that `Core/` exists and holds source, so an empty folder cannot pass.
- **Project rule.** The Rendering project file declares no `ProjectReference`, so the renderer depends on no other project in the repo.
- **Assembly rule.** The compiled Rendering assembly references no Bazaar assembly except itself and the core namespace. This catches dependencies that a project file might hide.

These rules check the code as it is. They do not check design intent, so a reviewer still reads new core types against section 2.

## 4. Known coupling to fix before extraction

These are not violations of the rules above. They are things that will block a clean extraction.

1. **Ordering keys are named for Bazaar.** `RenderFrame` and `FrameSequencer` use `GraphVersion` and `LayoutSeq`. They are generic sequence numbers and should get neutral names.
2. **One spatial grid (done, #473).** The client's hit-testing uses `src/Bazaar.Constellation.Rendering/Core/SpatialGrid.cs`. The duplicate in `Bazaar.Client.Core` was removed. The grid is 2D only; 3D frames need a dimension-aware rebuild before the server sends them.
3. **No GPU consumer for the instance buffer yet.** The Vulkan backend does not draw nodes yet, so the node instance buffer has no consumer. Its layout is not proven on a device, and it should not be frozen as a public contract until it is.
4. **Bazaar service registration in the Vulkan project.** `Bazaar.Constellation.Vulkan` still uses a Bazaar service registration, so the backend cannot be reused on its own.

## 5. Future extraction steps

1. Move `Core/` into its own project with no Bazaar references and its own tests. The architecture rules then become that project's own check.
2. Rename the ordering keys to neutral names (section 4, item 1).
3. Consolidate the two spatial grids into the core (section 4, item 2). Done in #473.
4. Move the Vulkan backend's generic parts behind the core interfaces, and leave Bazaar-specific code in the adapter (section 4, items 3 and 4).
5. Publish the package when the owner decides on the name and the licence. The repo rule sets Apache-2.0 for `Bazaar.Sdk` and `Bazaar.Contracts` only. The licence for this library is not decided, so ask the owner first and do not choose it here.

## 6. Forward-facing rule

Target `net11.0` and current APIs. Do not use obsolete or deprecated APIs, and do not add code paths for an older runtime. Package versions follow the newest stable release. The extracted core must build on the same target without changes.
