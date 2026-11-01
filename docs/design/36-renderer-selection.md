# Renderer selection: Kanban, Vault canvas, Constellation and the whole UI

Status: PROPOSED (2026-11-01). Owner question, not yet decided. Once the owner accepts, record it in `DECISIONS-LOG.md` and update doc 19 and `Bazaar.Constellation.md` §1, which still mark Skia as DECIDED.

## Question

Should Kanban and the Vault canvas render on Vulkan for speed, or is Avalonia enough? Should the whole UI move to Vulkan on every platform?

## Recommendation

1. **Avalonia for all ordinary UI, including Kanban and the Vault canvas.** Cards, columns, sticky notes, images and text boxes are retained controls. Avalonia virtualises them, so drawing cost follows what is on screen. Vulkan would add a second rendering stack for no visible gain at these sizes.
2. **Vulkan only for the Constellation graph view** (and the Git Solar System, which reuses its renderer). Dense point and edge drawing is the one workload where a GPU path pays off. Use Vulkan with GPU instancing as the primary path on capable desktops, and keep Skia through Avalonia as the fallback behind `IConstellationRenderer` (#173). This matches the owner's 2026-11-01 direction.
3. **No full-Vulkan UI.** A Vulkan UI would replace Avalonia's layout, text, input, accessibility and theming, which the theme file and key router depend on. Monaco is a separate hosted component (WebView2 or WebKitGTK), so it does not decide this either way.

## Why Avalonia is enough for Kanban and the Vault canvas

- Kanban is a dependency board (doc 12): columns of cards plus dependency lines. A busy company has hundreds of open cards, not millions. Virtualised lists and a few hundred edges are well inside Avalonia's budget.
- The Vault canvas is a freeform board of Vault documents, images and links (Obsidian, Kanban and Milanote-style). Its items are interactive controls with text, so Avalonia is the right tool. Its wiki-link graph view is not a separate canvas: it is the Constellation (doc 18, Constellation s1 and s3), so it takes the Constellation rules below.
- Only Vault graph views with large node counts need the GPU path.

## When to switch a surface to Vulkan (thresholds)

These are starting points to measure against, not settled values. Spike S14 (doc 35) measures them on the owner's Windows PC, and the Android numbers are still owed.

| Surface | Stay on Avalonia until | Switch to Vulkan when |
|---|---|---|
| Kanban board | Always, at realistic sizes | Sustained over 16 ms frames with 5,000+ visible cards or edges after virtualisation |
| Vault canvas (boards, images, notes) | Under 2,000 visible items with interactive controls | Over 5,000 visible items and frames miss 60 fps after virtualisation |
| Constellation graph (points and edges) | Skia misses 60 fps at 10,000 visible nodes or edges (the S14 budget, doc 19) | Skia misses that budget, or more than 50,000 visible points are needed on the PC after cluster level-of-detail |
| Whole UI | Never on current evidence | Only if Avalonia itself misses budget on ordinary screens, which no measurement shows so far |

Above the Constellation's `constellation.node_cap` (default 10,000), the server already sends clusters only (doc 19), so the GPU never receives billions of raw points. The server aggregates; the client draws what is in view. Phone limits are lower and need their own measurement.

## Platforms

- **Windows PC:** Avalonia on Skia for everything. Vulkan for the Constellation only, once S14 shows a need.
- **Linux:** same split. Vulkan on Linux is an open question because the Linux GPU drivers vary, so the Skia fallback must stay complete.
- **Android:** Avalonia on Skia. The Constellation is a read-only drawer first (doc 29), so Vulkan waits for a measured need.

## Costs of Vulkan that the decision must weigh

- Two drawing paths to test (Avalonia.Headless golden images plus a GPU capture path that needs a real driver).
- Driver and device variance; the CI container has no GPU, so GPU behaviour is verified only on the owner's PC.
- The current #173 branch has the renderer contract and a Vulkan shell, with no swapchain yet. Keep that scope: the contract and fallback ship first, and the Vulkan path is gated on the S14 result.

## Open items

- Owner: confirm the split (Avalonia for the UI, Vulkan for the Constellation only) and the thresholds above.
- S14 on the owner's PC: Skia baseline at 5k and 10k nodes, then Vulkan at 10k, 100k and 1M instanced points.
- Android device numbers.
- Update doc 19 and `Bazaar.Constellation.md` §1 once accepted (both currently say Skia DECIDED).
