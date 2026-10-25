# Client Architecture (Bazaar.Client.Core)

Status: RECONCILED with DECISIONS-LOG (2026-10-25), cross-doc pass done · Owner questions: 9
Depends on: 01-gc-data-model.md, 02-wire-protocol-and-sync.md, 09-work-life-simulation.md, 20-engine-integration-api.md, 23-observatory-and-usage.md, 31-local-coworker-npu.md, 32-remote-access-auth-and-privacy.md · Used by: 19-constellation-and-git-solar-system.md, 28-windows-client.md, 29-android-client.md, 30-linux-client-and-packaging.md, 34-testing-strategy.md

## Purpose
The shared Avalonia UI library and the client logic under it: connection and stores, theme, keyboard and command model, layout and panes, and the GC message view. Thin heads host it and supply platform services.

## Decided inputs
- One Avalonia codebase: `Bazaar.Client.Core` plus thin heads. WinUI 3 and Jetpack Compose are retired (CLAUDE.md; this supersedes the WinUI 3 fallback in Bazaar.Windows.md §2).
- Avalonia is decided but **conditional on the Monaco hosting spike (S12)**, which gates the UI framework as a whole. If it fails, the project may move to another open-source UI platform rather than ship a compromised client (DECISIONS-LOG round 5, C26, owner 2026-10-25).
- **OSI-only public build**: no paid Avalonia components, no Accelerate or Pro controls. Charts and trees are drawn plainly (DECISIONS-LOG round 4, C23, owner 2026-10-25).
- No web client. Native clients may embed web components such as Monaco (CLAUDE.md; Bazaar.Server.md §1). Android never uses Monaco (UI framework contingency note: Monaco is unsupported on mobile).
- Clients render; the server does all compute, except the ratified client-side exceptions: on-device NPU inference, the human's local terminal, the Roblox Studio bridge relay, and syncing the viewed branch to a local folder. Layout stays server-side (DECISIONS-LOG round 4, C17).
- The local terminal is a built-in replacement for Windows Terminal running on the human's machine. Agent terminals are streamed by the server and are not hosted here (DECISIONS-LOG round 4, C31; owned by 28).
- Layout: the server sends variants for every posture; the client switches locally with no round trip, and caches the last profile for offline cold start (DECISIONS-LOG round 5, C27).
- Secrets: the Identity Service holds provider and GitHub App keys. Clients hold device credentials only; the client never holds a GitHub credential (DECISIONS-LOG rounds 2 and 6, C05, C25).
- Blobs and search use the authenticated HTTP surface in 02, not this connection (DECISIONS-LOG round 6, C15).
- Nothing focused by default; leader `Space`; mnemonics (`w` then `c` focuses chat); no Ctrl chords for app commands; click-to-focus; fuzzy palette; stenography-compatible (Bazaar.Windows.md §4).
- GC message anatomy, including presence, cost chip and profile cards (Bazaar.Windows.md §4; 09). Presence uses the eight labels plus overlays (DECISIONS-LOG round 11, C43).
- GC entries are append-only; edits keep every version with an "edited" marker (CLAUDE.md; DECISIONS-LOG round 1, C09).
- Windows start dashboard: unread and mentions, awaiting you, agents and Kanban, build health and usage (Bazaar.Windows.md §1).
- Detachable windows. A Windows default handler needs a user step (Bazaar.Windows.md §1, CHANGED).
- Android is a Development Console, not a mobile IDE (Bazaar.Android.md §1–2).
- Every UI change gets an `Avalonia.Headless` test (CLAUDE.md).

## Design

### Projects [PROPOSED]
| Project | Holds | References |
|---|---|---|
| `Bazaar.Client.Core` | Views, view models, stores, connection client, theme loader, key router, command registry, layout renderer, pane registry | `Bazaar.Contracts`, Avalonia 12.1.4 |
| `Bazaar.Client.Windows` / `.Linux` / `.Android` | Entry point and platform services: device secret store, terminal host, editor host, fold posture, default-handler registration, notifications | `Bazaar.Client.Core` |
| `Bazaar.Client.Core.Tests` | Headless UI tests and pure logic tests | `Bazaar.Client.Core` |

Core never references a head or `Bazaar.Server`. Platform services are interfaces defined in Core and implemented in heads. Project names are approved (CLAUDE.md; DECISIONS-LOG round 10, C37).

### Layers and state [PROPOSED]
- **Stores** (one per stream: GC, catch-up, presence, ui) hold immutable snapshots, written only by the connection layer.
- **View models** derive from stores, do no I/O, and call services for actions.
- **Window-local state** (focus, scroll, splitter preview, palette query) stays in the client. Preferences go through `prefs.set` on commit, never per pointer move.
- **MVVM helper:** CommunityToolkit.Mvvm (Q3). Compiled bindings for trim safety, unverified on Android.

### Connection and sync
- One `IBazaarConnection` per process; all windows share it (02 Q4).
- It owns the hello/welcome handshake, subscribe with per-GC cursors, in-order application by `seq`, de-duplication by `id`, `lagged` and `resync` handling, and reconnect with jittered backoff (1 s to 60 s).
- Connection state shows as one chip: connecting, live, offline (with cache age), or update required (close 4426).
- Tests drive the connection through a fake transport. No socket in view-model tests.

### Offline cold start and built-in shell [PROPOSED]
This doc owns the cached offline profile and the minimal built-in shell (DECISIONS-LOG round 5, C27).
1. Read the cached `UiProfile` (with revision and age) and paint the shell at once.
2. Connect, then reconcile on `ui.changed`.
3. With no cache, show the built-in fallback: Connecting, Settings and Pairing only (02 Q5).
4. Cache writes are atomic (temp file, then rename). A corrupt cache is discarded silently.
5. The cache holds the profile and derived counters (unread, mention counts). It never holds message bodies. This is a test, not a convention.

### Server-driven layout and panes [PROPOSED]
- `LayoutDescriptor` is a tree. Containers: `split`, `stack`, `tabs`, `dock`. Leaves: `pane { kind, id }`.
- The server computes a layout for each posture it supports (folded, unfolded, half-open) and sends the variants in `UiProfile` (02).
- The client selects the variant for the current posture locally. Fold and resize need no round trip and send nothing on the wire. The client never invents a layout.
- Unknown container: children stack vertically. Unknown leaf: a placeholder card showing the kind.
- Pane kinds map to view factories in a registry. v1 loads first-party factories only (Q3); engine panes arrive as `PaneDescriptor` (20) and fall back to generic tree, log, form or table views.

### Theme file [OPEN; direction PROPOSED]
- One JSON file: `schemaVersion`, `name`, `base` (light or dark), `tokens` (colours, fonts, spacing, radii, role badge colours, syntax colours, reduced-motion flag).
- Pipeline: parse, then validate (required tokens; 4.5:1 contrast for text tokens; no URLs, scripts or remote fonts), then generate an Avalonia `ResourceDictionary` and a Monaco `defineTheme` payload from the same tokens.
- Users import and export the file. The server stores revisions (02).

### Keyboard and command model [PROPOSED]
- **Modes:** Normal (default, nothing focused; keys are commands), Insert (entered by focusing a text input or `i`; `Esc` leaves), Palette (opened by the `client.keys.palette` setting, default `Space Space`).
- **Clipboard:** inside Insert mode, OS clipboard and undo keys work in text inputs (`client.clipboard_in_text_inputs`). The no-Ctrl-chord rule covers app commands only.
- **Router:** one preview-key handler at the window root. The state machine is pure and tested without Avalonia.
- **Command registry:** id, title, mnemonic sequence, keywords, enabled predicate, source (local or server). Server commands arrive in `UiProfile`. Local mnemonic conflicts fail at load.
- **Palette:** subsequence fuzzy match, recency boost, no network.
- **Hints:** a which-key list appears after the leader, after `client.hints.delay_ms`. The hint timeout never triggers execution.
- **IME:** composition events bypass Normal mode.

### GC message pipeline [PROPOSED]
1. Log rows from 01 (`gc_event`, `gc_message`, `gc_message_version`) become an `EntryViewModel` by kind. Each edit is a separate version; the view shows the latest with an "edited" marker and opens the history.
2. Consecutive entries from one author within `client.gc.group_window_min` (default 5 minutes) are grouped.
3. Date separators use the viewer's local timezone. Agent schedules (09) are shown in the agent's own zone on the profile card.
4. Row: avatar, name, role badge, model badge, cost chip (display only, never a gate; 23), presence dot, timestamp, "edited" marker with history.
5. Action chips: raw action records come from the server. Each chip is labelled deterministically with no model, for example "Read 15 files", and collapsed by default. Count labels follow `client.action_chips.show_counts`. An optional prose summary may be generated on the local NPU (31), cached per device, marked as derived, and shown only when `client.action_chips.npu_summary` is on. Neither is ever written back to the GC.

### Virtualised GC view [PROPOSED]
- Rows keyed by `(gc, seq)`. History pages backward with `gc.history`. Holes from 02 show as a "load gap" row.
- Prepending keeps the scroll anchor.
- Long action output is truncated with paging.
- Budget [PROPOSED acceptance target, not a setting]: 10,000 entries scroll at 60 fps on the Windows reference machine, and the cached shell paints within 1 s. Both are targets to be measured, not assumed.

### Settings [PROPOSED]
Client preferences are registry entries owned by doc 03 (DECISIONS-LOG round 13). The client reads defaults from the registry and does not hard-code them. The setup wizard and advanced settings (docs 27, 32) show these keys, and every change is audited (doc 15 `audit_log`).

| Key | Type | Default | Scope (install/project/agent) | Who may change | Change needs approval? |
|---|---|---|---|---|---|
| `client.clipboard_in_text_inputs` | bool | true | install | owner | no |
| `client.dashboard.order` | list of section ids | awaiting_you, unread_mentions, agents_kanban, build_health_usage | install | owner | no |
| `client.action_chips.show_counts` | bool | true | install | owner | no |
| `client.action_chips.npu_summary` | bool | false [PROPOSED] | install | owner | no (device NPU opt-in) |
| `client.gc.group_window_min` | int (minutes) | 5 | install | owner | no |
| `client.hints.delay_ms` | int | 500 | install | owner | no |
| `client.keys.palette` | key label | `Space Space` | install | owner | no (validated at load: mnemonic only, no Ctrl chords) |

### Platform heads
| Concern | Windows | Linux | Android |
|---|---|---|---|
| Role | Build products; full shell | Same; lower priority | Development console |
| Editor (Monaco) | WebView2 behind `IEditorHost`, gated by spike S12 | Not in first cut (Q9) | Not included (Monaco is unsupported on mobile) |
| Local terminal | ConPTY library behind `ITerminalHost` (28) | PTY library | Not included |
| Windows and handlers | Multi-window; default handler needs user step | `.desktop` plus `xdg-mime` | n/a |
| Layout input | Window size class picks a server variant | Same | Posture from WindowLayoutInfo picks a server variant locally |
| Device secrets | DPAPI | Secret Service, or 0600 file (Q5) | Android Keystore |
| Accessibility | UIA (unverified) | AT-SPI2 (Avalonia 12.0 claim) | TalkBack (unverified) |

Android folded view is chat triage; unfolded is panes (Bazaar.Android.md §3).

### Accessibility [PROPOSED]
Mode (NORMAL, INSERT) is shown as text and announced on change. Badges are text, not colour alone. Theme validation enforces contrast. Reduced-motion token. Target: WCAG 2.2 AA for text contrast in every head. No accessibility claim before Narrator and TalkBack checks.

### Localisation [PROPOSED]
Resource-file strings; mnemonics are per-locale data; dates through the culture; RTL through `FlowDirection`; CJK font fallback in the theme.

## Data and interfaces
```csharp
interface IBazaarConnection { ConnectionState State { get; }
  Task SubscribeAsync(IReadOnlyList<StreamCursor> streams, CancellationToken ct); }
// IDeviceSecretStore: device credentials only (renamed from ISecretStore, C05)
interface IPlatformServices { IDeviceSecretStore Secrets { get; } ITerminalHost? Terminal { get; }
  IEditorHost? Editor { get; } IFoldPosture? Posture { get; } }
record LayoutDescriptor(string Container, IReadOnlyList<LayoutDescriptor> Children, string? PaneKind, string? PaneId);
record Command(string Id, string Title, string Mnemonic, IReadOnlyList<string> Keywords, CommandSource Source);
```
Cache file: `ui-profile.json` holding `{ revision, savedAt, profile }`, plus derived counters. No entry bodies.

## Behaviour and edge cases
- Unknown widget, pane, container or command kind renders a placeholder and logs once. A newer `UiProfile` renders what it can.
- Mnemonic prefixes: the hint timeout never triggers execution.
- Detached windows share the connection and stores, with their own root view model and layout.
- Logs never include message bodies.
- The device secret store holds device credentials only. No provider or GitHub App key class exists in Core (C05, C25).

## Testing
- **Pure tests (no Avalonia):** key state machine, mnemonic conflicts, fuzzy scoring, theme validation and golden output, cache corruption, connection over a fake transport, posture variant selection (no socket), settings validation (`client.keys.palette` rejects Ctrl chords).
- **Headless (Avalonia.Headless.XUnit 12.1.4, `[AvaloniaFact]`):** palette opens on the configured palette key and runs a command; disabled commands are inert; unknown layout kinds; 10,000-entry list keeps anchor on prepend; offline shell; IME bypasses Normal mode.
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
7. Layout renderer, variant selection and pane registry. Depends on 4.
8. Start dashboard widgets from cached counters and live stores (presence and usage from 23). Depends on 4, 01 GetCatchUp.
9. GC entry view models and message row. Depends on 2, 3.
10. Action chips, summariser interface, deterministic fallback. Depends on 9, 31.
11. Virtualised GC list, paging, gap rows. Depends on 9.
12. Detached window manager. Depends on 7.
13. `IEditorHost` and Monaco payload wiring. Depends on 2 and the Monaco gate (S12, 35).
14. Head shells and platform services. Depends on 1–13.
15. Accessibility pass per head. Depends on 14.
16. Settings keys wired to doc 03 registry. Depends on 03 registry.

## Conflicts
1. **Monaco in an Avalonia WebView is unverified.** Bazaar.Windows.md §4 is decided, but no source shows Monaco in any Avalonia WebView. The official `NativeWebView` needs the paid Accelerate tier, which the OSI-only build excludes (C23). The only free route left is a direct WebView2 HWND host, also unverified. Spike S12 gates the whole Avalonia decision (C26). `IEditorHost` keeps the editor swappable.
2. **Stenography vs single-letter commands.** Steno output is typed text. In Normal mode, letters are commands, so steno must land in a focused field (Q1).
3. **Runtime pane plugins (20) vs trimming.** `AssemblyLoadContext` plugins conflict with the AOT and trim gains claimed for Android (vendor figures, unverified) (Q3).
4. **Cross-doc fixes (not this doc's to apply).** 28 must serve the dashboard from cached counters and live stores, not from a cached GC entry view (C27). 01 must name the read model for `gc_message` and `gc_message_version` (C09); this doc uses those names.

## Questions for the owner
1. **Stenography:** which steno software will you use? Default: steno output goes to the focused text field only. Letters are commands only when nothing is focused. Needs a test with your software.
2. **Theme file format:** JSON with a schema version. Default JSON; TOML or YAML are alternatives. The validator rejects URLs, scripts and remote fonts in every format.
3. **Pane plugins:** first-party compiled-in only in v1? Default yes (also answers 20 Q10).
4. **New dependency:** approve CommunityToolkit.Mvvm? Default yes, MIT.
5. **Device secret store:** DPAPI, Secret Service, Android Keystore, holding device credentials only? Default yes. Linux falls back to a 0600 file only with your say-so.
6. **Local cache:** profile and derived counters only (27 and 28 agree). Should an optional encrypted entry cache (SQLite, pending an AOT check on Android) be a separate decision? Default no.
7. **Screen-reader user:** is a screen-reader user a v1 requirement? The WCAG 2.2 AA contrast target is [PROPOSED] in Accessibility above.
8. **Posture variants:** folded, unfolded and half-open only, or also a width-class fallback for tablets and desktops? Default: the three postures plus a width-class fallback (02, 29).
9. **Linux editor:** Monaco on WebKitGTK after the Windows spike, or no editor in the first Linux cut? Default no editor in the first cut; a WebKitGTK spike after S12 passes.

MOVED TO SETTINGS: client.clipboard_in_text_inputs, client.dashboard.order, client.action_chips.show_counts, client.action_chips.npu_summary, client.gc.group_window_min, client.hints.delay_ms, client.keys.palette (DECISIONS-LOG round 13)

ANSWERED: Monaco hosting: spike first with free routes only, no paid tier; the spike gates the Avalonia decision, and the project may move off Avalonia if it fails (DECISIONS-LOG round 5, C26).
