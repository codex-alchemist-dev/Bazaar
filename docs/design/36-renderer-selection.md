# Renderer selection: the Constellation, Kanban and the chrome

Status: PROPOSED (2026-11-01). Split accepted under the owner's delegated authority. Doc 19 and `Bazaar.Constellation.md` §1 match this doc. Record in `DECISIONS-LOG.md` on the next round.

## Decision

- **The Constellation is the primary knowledge surface and renders on Vulkan across clients.** It is the Obsidian and Milanote replacement: notes, canvas, the Vault's links, agent memory and related items. Owner, 2026-11-01: it is the whole knowledge workspace, not a side graph view. It draws through the renderer behind `IConstellationRenderer` (#173), with Skia through Avalonia as the fallback where Vulkan is unavailable.
- **Avalonia stays for chrome, Kanban and text-heavy editing panes.** Kanban is a separate screen (doc 12) and stays on Avalonia unless a measurement says otherwise.
- **Monaco stays separate.** It is an embedded web component (WebView2, WebKitGTK on Linux), not drawn by either renderer. Android never uses Monaco.
- **No full-Vulkan UI.** Menus, dialogs, settings, forms and the theme stay on Avalonia.

## Why Vulkan for the Constellation

- Its contents are dense and it zooms and pans continuously: documents, canvas items, links, memory notes and graph edges. Doc 19 sets the budget at 60 fps for 5,000 to 10,000 visible nodes on the PC, with cluster level-of-detail above `constellation.node_cap` (default 10,000). The GPU path is for cases Skia cannot hold.
- The server does layout and aggregation (doc 19). The client draws the frame it is sent.

## Costs of Vulkan to manage (flagged as the owner asked)

- **Text editing.** Vulkan has no text layout, input method or caret. Note bodies are edited in an Avalonia text pane or a Monaco pane. Only rendered labels, previews and canvas text go through Vulkan, and they need their own glyph atlas and shaping path.
- **Accessibility.** Screen readers and keyboard focus use the Avalonia accessibility tree. A Vulkan-drawn canvas has none, so the Constellation needs a parallel accessible list of its visible items (`AccessibleNodeList` in `Bazaar.Client.Core` already covers part of this). Every item reachable by mouse must be reachable by keyboard and through that list.
- **Input and hit-testing.** Pointer, touch, IME and keyboard routing go through Avalonia and are passed to the Vulkan control. Hit-testing uses the spatial grid in doc 19.
- **Theming.** The one theme file drives Avalonia and Monaco (CLAUDE.md). The Vulkan renderer reads the same tokens, so colours and fonts come from that file.
- **Drivers and devices.** GPU behaviour varies by driver, and CI has no GPU. The Skia fallback must stay complete and tested. The GPU path is verified on the owner's PC.
- **Two drawing paths.** Golden-image tests run on Skia in Avalonia.Headless. The Vulkan path runs on a software Vulkan device in CI where one exists, and on the PC for real frame numbers.

## Thresholds to revisit the split

Starting points only. S14 (doc 35) measures them on the owner's PC. Android numbers are still owed.

| Surface | Current choice | Revisit when |
|---|---|---|
| Constellation (canvas, graph, memory) | Vulkan, Skia fallback | Vulkan misses the frame budget on the PC or Android at the agreed sizes, or the fallback fails its tests |
| Kanban board | Avalonia | Sustained over 16 ms frames with 5,000+ visible cards or edges after virtualisation |
| Text editing panes | Avalonia (or Monaco) | Not moved to Vulkan: text input and accessibility stay on Avalonia |
| Whole UI | Avalonia for chrome | Only if Avalonia misses the budget on ordinary screens, which no measurement shows so far |

Above `constellation.node_cap`, the server sends clusters only (doc 19), so the GPU never receives billions of raw points. Phone limits are lower and need their own measurement.

## Platforms

- **Windows PC:** Avalonia for chrome, Kanban and text panes. Vulkan for the Constellation (primary), Skia fallback.
- **Linux:** same split. GPU drivers vary, so the Skia fallback must stay complete.
- **Android:** Avalonia for chrome, Kanban and text panes. The Constellation uses Vulkan (primary) with Skia fallback, but its first release is a read-only drawer (doc 29), and the Android numbers are still owed.

## Open items

- Owner: confirm the revised split.
- S14 on the owner's PC: Skia baseline and Vulkan numbers at 5k, 10k and 100k visible items.
- Android device numbers.
- Accessibility plan for the Vulkan canvas: the parallel accessible list must cover every canvas item.
