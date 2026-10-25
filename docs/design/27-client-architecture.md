# Client Architecture (Bazaar.Client.Core)

Status: DRAFT for owner review (2026-10-25) · Owner questions: 14
Depends on: 01-gc-data-model.md, 02-wire-protocol-and-sync.md, 09-work-life-simulation.md, 20-engine-integration-api.md, 31-local-coworker-npu.md, 32-remote-access-auth-and-privacy.md · Used by: 19-constellation-and-git-solar-system.md, 28-windows-client.md, 29-android-client.md, 30-linux-client-and-packaging.md, 34-testing-strategy.md

## Purpose
The shared Avalonia 12 UI library and the client logic under it: connection and stores, theme, keyboard and command model, layout and panes, and the GC message view. Thin heads host it and supply platform services.

## Decided inputs
- One Avalonia codebase: `Bazaar.Client.Core` plus thin heads; WinUI 3 is the fallback (Bazaar.Windows.md §2).
- Clients render; the server does all compute; no web client (CLAUDE.md; Bazaar.Server.md §1).
- Fully themeable: one shareable file drives Avalonia styles and Monaco theme JSON. Format OPEN (Bazaar.Windows.md §4).
- Nothing focused by default; leader `Space`; mnemonics (`w` then `c` focuses chat); no Ctrl chords; click-to-focus; fuzzy palette; stenography-compatible (Bazaar.Windows.md §4).
- GC message anatomy, including presence, cost chip and profile cards (Bazaar.Windows.md §4; 09). Action chips generated locally on the NPU, never server-summarised (Bazaar.Android.md §3).
- Windows start dashboard: unread and mentions, awaiting you, agents and Kanban, build health and usage (Bazaar.Windows.md §1).
- Detachable windows; a Windows default handler needs a user step (Bazaar.Windows.md §1, CHANGED).
- Monaco in a WebView2 island on Windows (Bazaar.Windows.md §4). Android is a Development Console (Bazaar.Android.md §1–2).
- GC entries are append-only; edits keep history with an "edited" marker (01).
- Every UI change gets an `Avalonia.Headless` test (CLAUDE.md). No paid Avalonia tier in the public repo until confirmed (avalonia_clients.md Q4).

## Design

### Projects [PROPOSED]
| Project | Holds | References |
|---|---|---|
| `Bazaar.Client.Core` | Views, view models, stores, connection client, theme loader, key router, command registry, layout renderer, pane registry | `Bazaar.Contracts`, Avalonia 12.1.4 |
| `Bazaar.Client.Windows` / `.Linux` / `.Android` | Entry point and platform services: secret store, terminal host, editor host, fold posture, default-handler registration, notifications | `Bazaar.Client.Core` |
| `Bazaar.Client.Core.Tests` | Headless UI tests and pure logic tests | `Bazaar.Client.Core` |

Core never references a head or `Bazaar.Server`. Platform services are interfaces defined in Core and implemented in heads.

### Layers and state [PROPOSED]
- **Stores** (one per stream: GC, catch-up, presence, ui) hold immutable snapshots, written only by the connection layer.
- **View models** derive from stores, do no I/O, and call services for actions.
- **Window-local state** (focus, scroll, splitter preview, palette query) stays in the client. Preferences go through `prefs.set` on commit, never per pointer move.
- **MVVM helper:** CommunityToolkit.Mvvm (Q6). Compiled bindings for trim safety, unverified on Android.

### Connection and sync
- One `IBazaarConnection` per process; all windows share it (02 Q4).
- It owns the hello/welcome handshake, subscribe with per-GC cursors, in-order application by `n`, de-duplication by `id`, `lagged` and `resync` handling, and reconnect with jittered backoff (1 s to 60 s).
- Connection state shows as one chip: connecting, live, offline (with cache age), or update required (close 4426).
- Tests drive the connection through a fake transport. No socket in view-model tests.

### Offline cold start [PROPOSED]
1. Read the cached `UiProfile` (with revision and age) and paint the shell at once.
2. Connect, then reconcile on `ui.changed`.
3. With no cache, show the built-in fallback: Connecting, Settings and Pairing only (02 Q5).
4. Cache writes are atomic (temp file, then rename). A corrupt cache is discarded silently.
5. The cache never holds GC content. This is a test, not a convention.

### Server-driven layout and panes [PROPOSED]
- `LayoutDescriptor` is a tree. Containers: `split`, `stack`, `tabs`, `dock`. Leaves: `pane { kind, id }`.
- Unknown container: children stack vertically. Unknown leaf: a placeholder card showing the kind.
- Pane kinds map to view factories in a registry. v1 loads first-party factories only; engine panes arrive as `PaneDescriptor` (20) and fall back to generic tree, log, form or table views.
- Fold or resize changes send `ui.get` with the new posture. The client never picks a layout itself.

### Theme file [OPEN; direction PROPOSED]
- One JSON file: `schemaVersion`, `name`, `base` (light or dark), `tokens` (colours, fonts, spacing, radii, role badge colours, syntax colours, reduced-motion flag).
- Pipeline: parse, then validate (required tokens; 4.5:1 contrast for text tokens; no URLs, scripts or remote fonts), then generate an Avalonia `ResourceDictionary` and a Monaco `defineTheme` payload from the same tokens.
- Users import and export the file. The server stores revisions (02).

### Keyboard and command model [PROPOSED]
- **Modes:** Normal (default, nothing focused; keys are commands), Insert (entered by focusing a text input or `i`; `Esc` leaves), Palette (opened by `Space Space`).
- **Router:** one preview-key handler at the window root. The state machine is pure and tested without Avalonia.
- **Command registry:** id, title, mnemonic sequence, keywords, enabled predicate, source (local or server). Server commands arrive in `UiProfile`. Local mnemonic conflicts fail at load.
- **Palette:** subsequence fuzzy match, recency boost, no network.
- **Hints:** a which-key list appears 500 ms after the leader.
- **IME:** composition events bypass Normal mode.

### GC message pipeline [PROPOSED]
1. Entry (01) becomes an `EntryViewModel` by kind: message, edit, system, structured, quarantine.
2. Consecutive entries from one author within a window are grouped.
3. Date separators use the viewer's local timezone. Agent schedules (09) are shown in the agent's own zone on the profile card.
4. Row: avatar, name, role badge, model badge, cost chip (display only, never a gate), presence dot, timestamp, "edited" marker with history.
5. Action chips: raw action records come from the server. The NPU summary is generated locally (31), cached per device and marked as derived. Fallback label: deterministic counts, for example "Read 15 files". Collapsed by default. Never written back to the GC.

### Virtualised GC view [PROPOSED]
- Rows keyed by `(gc, seq)`. History pages backward with `gc.history`. Holes from 02 show as a "load gap" row.
- Prepending keeps the scroll anchor.
- Long action output is truncated with paging.
- Budget (Q11): 10,000 entries scroll at 60 fps on the Windows reference machine. Measured, not assumed.

### Platform heads
| Concern | Windows | Linux | Android |
|---|---|---|---|
| Role | Build products; full shell | Same; lower priority | Development console |
| Editor (Monaco) | WebView2 behind `IEditorHost` (spike) | Not in first cut | Not included |
| Terminal | ConPTY library behind `ITerminalHost` | PTY library | Not included |
| Windows and handlers | Multi-window; default handler needs user step | `.desktop` plus `xdg-mime` | n/a |
| Layout input | Full descriptor | Same | Folded and unfolded posture from WindowLayoutInfo |
| Secrets | DPAPI | Secret Service, or 0600 file (Q7) | Android Keystore |
| Accessibility | UIA (unverified) | AT-SPI2 (Avalonia 12.0 claim) | TalkBack (unverified) |

Android folded view is chat triage; unfolded is panes (Bazaar.Android.md §3).

### Accessibility [PROPOSED]
Mode (NORMAL, INSERT) is shown as text and announced on change. Badges are text, not colour alone. Theme validation enforces contrast. Reduced-motion token. No accessibility claim before Narrator and TalkBack checks.

### Localisation [PROPOSED]
Resource-file strings; mnemonics are per-locale data; dates through the culture; RTL through `FlowDirection`; CJK font fallback in the theme.

## Data and interfaces
```csharp
interface IBazaarConnection { ConnectionState State { get; }
  Task SubscribeAsync(IReadOnlyList<StreamCursor> streams, CancellationToken ct); }
interface IPlatformServices { ISecretStore Secrets { get; } ITerminalHost? Terminal { get; }
  IEditorHost? Editor { get; } IFoldPosture? Posture { get; } }
record LayoutDescriptor(string Container, IReadOnlyList<LayoutDescriptor> Children, string? PaneKind, string? PaneId);
record Command(string Id, string Title, string Mnemonic, IReadOnlyList<string> Keywords, CommandSource Source);
```
Cache file: `ui-profile.json` holding `{ revision, savedAt, profile }`.

## Behaviour and edge cases
- Unknown widget, pane, container or command kind renders a placeholder and logs once. A newer `UiProfile` renders what it can.
- Mnemonic prefixes: the hint timeout never triggers execution.
- Detached windows share the connection and stores, with their own root view model and layout.
- Logs never include message bodies.

## Testing
- **Pure tests (no Avalonia):** key state machine, mnemonic conflicts, fuzzy scoring, theme validation and golden output, cache corruption, connection over a fake transport.
- **Headless (Avalonia.Headless.XUnit 12.1.4, `[AvaloniaFact]`):** palette opens on `Space Space` and runs a command; disabled commands are inert; unknown layout kinds; 10,000-entry list keeps anchor on prepend; offline shell; IME bypasses Normal mode.
- **Frames:** `UseHeadlessDrawing = false`, `UseSkia()`, `CaptureRenderedFrame`, a bundled test font at fixed DPI, baselines within a tolerance. Visual-tree assertions first, pixels second.
- **Build container:** the capture path is documented; no run here is verified. Skia native libraries and fonts may be missing, so a capture smoke test comes first.
- **Not covered headless:** OS IME, folds, real DPI, UIA and TalkBack, PTY, default handlers. These go to a device checklist (34).

## Work packages
1. Core and tests skeleton: Avalonia 12.1.4 via CPM, headless project, frame-capture smoke test. Depends on 02 WP1.
2. Theme model, validator, Avalonia and Monaco generators, golden tests.
3. Connection client with fake transport. Depends on 02 WP8.
4. Stores, cached profile, offline shell. Depends on 3.
5. Key router and mode state machine (pure).
6. Command registry, fuzzy matcher, palette view. Depends on 5.
7. Layout renderer and pane registry. Depends on 4.
8. Start dashboard widgets. Depends on 4, 01 GetCatchUp.
9. GC entry view models and message row. Depends on 2, 3.
10. Action chips, summariser interface, fallback. Depends on 9, 31.
11. Virtualised GC list, paging, gap rows. Depends on 9.
12. Detached window manager. Depends on 7.
13. `IEditorHost` and Monaco payload wiring. Depends on 2.
14. Head shells and platform services. Depends on 1–13.
15. Accessibility pass per head. Depends on 14.

## Conflicts
1. **Monaco in WebView2 is unverified.** Bazaar.Windows.md §4 is decided, but no source shows Monaco in any Avalonia WebView. The official `NativeWebView` needs the paid Accelerate tier, not XPF as the doc says; community packages are Windows-only or unverified. The `IEditorHost` abstraction keeps this swappable.
2. **Stenography vs single-letter commands.** Steno output is typed text. In Normal mode, letters are commands, so steno must land in a focused field.
3. **No Ctrl chords vs OS clipboard.** Users expect copy, paste and undo in text fields. The rule should scope to app commands.
4. **Runtime pane plugins (20) vs trimming.** `AssemblyLoadContext` plugins conflict with the AOT and trim gains claimed for Android (vendor figures, unverified).
5. **Naming.** Bazaar.Windows.md §3 uses `Bazaar.UI.Core`; CLAUDE.md owner-approved `Bazaar.Client.Core`. This doc follows CLAUDE.md.
6. **Paid controls.** Pro-tier charts and Tree DataGrid would serve Observatory and Kanban, but per-seat fees conflict with the public-repo rule.
7. **"Equivalent to Playwright"** is overstated (avalonia_clients.md Q3). This doc says "equivalent for in-app rendering and logic".

## Questions for the owner
1. **Stenography:** which steno software will you use? Default: steno output goes to the focused text field only. Letters are commands only when nothing is focused. Needs a test with your software.
2. **Clipboard in Insert mode:** keep Ctrl+C, V, X, Z and A inside text inputs? Default yes; the no-Ctrl rule covers app commands only.
3. **Theme file format:** JSON with a schema version. Default JSON; TOML or YAML are alternatives. Remote fonts disallowed by default.
4. **Monaco hosting:** approve a WebView2 spike with no paid tier? Default yes; if the spike fails, Accelerate is the fallback and needs your approval.
5. **Pane plugins:** first-party compiled-in only in v1? Default yes (also answers 20 Q10).
6. **New dependency:** approve CommunityToolkit.Mvvm? Default yes, MIT.
7. **Local secret store:** DPAPI, Secret Service, Android Keystore? Default yes. Linux falls back to a 0600 file only with your say-so.
8. **Local cache:** an embedded store for entry cache and cursors? Default SQLite, pending an AOT check on Android.
9. **Dashboard order:** awaiting you, unread and mentions, agents and Kanban, build health and usage? Default yes.
10. **No NPU:** show deterministic counts for action chips? Default yes.
11. **Performance budget:** 10,000 entries at 60 fps; cached shell painted within 1 s on Bazaar.Windows. Default yes; both are targets, not proven.
12. **Palette and leader:** `Space Space` opens the palette; hints at 500 ms. Confirm, or choose another key.
13. **Accessibility target:** WCAG 2.2 AA for text contrast in every head? Default yes. Is a screen-reader user a v1 requirement?
14. **Message grouping:** same author within 5 minutes. Default 5.
