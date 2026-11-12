# Client GUI customization, module contract and DSL question

Status: PROPOSED, HELD FOR OWNER DECISIONS (2026-11-12, revised after owner correction) · Owner decisions needed: see section 10 (count refreshed 2026-11-12; command-bar AI matching answered: suggest only) · This PR stays a draft until the owner answers. No default is applied to the decisions in section 10; work that depends on them waits.
Depends on: 02-wire-protocol-and-sync.md (`ui.get`, `ui.changed`, `prefs.set`), 03-server-host-and-providers.md (settings registry), 15-security-taint-and-approvals.md (approval tiers, `audit_log`), 20-engine-integration-api.md (`PaneDescriptor`), 27-client-architecture.md (layout, theme, keyboard model), 28-windows-client.md, 29-android-client.md, 38-ryusei-fuda.md, 19-constellation-and-git-solar-system.md · Used by: 30-linux-client-and-packaging.md, 34-testing-strategy.md, 40-core-feature-modules-audit.md

## 1. Purpose

Bazaar's clients have to be fully customizable per Bazaar company: theme, layout, which panes sit where, what the start screen shows, which commands the palette offers, and per-company configuration. The owner asked three things: how the GUI works, how it stays customizable without losing safety or performance, and whether a DSL is warranted.

The short answer:

- **Modularity is total.** The base GUI and every pane are modules on `Bazaar.Core`, at the same level as third-party modules. There is no privileged first-party path. One module contract serves all of them (owner correction, 2026-11-12).
- **Customization is data, not code.** Versioned, schema-checked JSON documents (theme, layout, start screen, command bindings, settings) describe the GUI. No document can carry code, markup, URLs or scripts.
- **A full DSL, carried inside the typed JSON (owner, 2026-11-12).** Documents stay validated JSON with one codec and one validator. Layout logic and rules are written in a DSL whose source sits inside those documents, so the DSL never runs outside the document checks. The DSL is sandboxed: no I/O, no wire calls except declared read-only queries, and fixed step and time limits (section 4.1, option B').
- **Trust comes from the contract, not from the author.** A module declares what it needs (capabilities), and the host enforces that declaration the same way for every module.
- **Config lives on the server**, revisioned, scoped (install, company, person, device), and synced through `ui.get` and `ui.changed`. Agents cannot edit it.

## 2. Decided inputs (not re-opened here)

- One Avalonia codebase, `Bazaar.Client.Core` plus thin heads. WinUI 3 and Jetpack Compose are retired (CLAUDE.md).
- Avalonia stays **conditional on the Monaco hosting gate (S12)**. Android never uses Monaco (27, DECISIONS-LOG round 5).
- The server does all compute. Layout stays server-side: the server sends a variant per posture and the client selects one locally (27, C17, C27).
- Theme: one file drives Avalonia and Monaco; validation rejects URLs, scripts and remote fonts (27, `src/Bazaar.Client.Core/Theme/`).
- Panes register through `IPaneModule` and are composed by `PaneRegistry.Compose` (`src/Bazaar.Client.Core/Panes/`). Unknown kinds render a placeholder and are reported once.
- Keyboard: mnemonics only, no Ctrl chords for app commands, nothing focused by default (27, Windows doc 28 §4).
- Configurability rule: a value left open becomes an operator setting with a safe default, audited on change. Floors and security invariants are not settings, and settings may only tighten them (DECISIONS-LOG round 13).
- Modularity is total (owner, 2026-11-12). The base GUI and every pane are modules on `Bazaar.Core`, the same level as third-party modules. Clients keep no privileged built-in path.
- Constellation's renderer stays generic behind a boundary, with a Bazaar adapter, so it can be extracted later (owner, 2026-11-12). Layout documents refer to the Constellation pane by kind only, never to renderer internals.
- Client builds target .NET 11, use JSON through `IWireCodec` with source-generated `System.Text.Json`, and follow the forward-facing rule (CLAUDE.md, DECISIONS-LOG).

## 3. What "customizable" covers

| Surface | Example | Document | Who may override |
|---|---|---|---|
| Theme tokens | colours, fonts, spacing, radii, role and presence colours, reduced-motion flag | `theme` (exists in code: `ThemeModel`, `ThemeValidator`) | company, person may override |
| Layout | split, stack, tabs and dock containers with pane leaves, per posture | `layout` | company default, person may override |
| Start screen | section order, widgets, counters | `client.dashboard.order` (already a setting, 27) plus widget list | company |
| Command bindings | mnemonic for a command, hidden commands, palette keywords | `commands` | person (within company rules) |
| Pane configuration | filters, sort order, page size for a pane instance | `pane` options inside the layout leaf | person |
| Preferences | hints delay, group window, clipboard rule | settings registry (27 table) | per the registry's "who may change" column |

The set of pane kinds is **not** customizable at runtime. A company can hide, place or configure a pane. It cannot add a pane kind that no composed module provides.

## 4. Options and recommendations

### 4.1 Customization model

| Option | What it is | Pros | Cons |
|---|---|---|---|
| **A. Typed JSON documents with schema versions** (recommended) | Each surface in section 3 is a JSON document, validated against a published JSON Schema 2020-12 and a C# model with source-generated `System.Text.Json`. | Same codec as the wire (`IWireCodec`). Validation, diff, export and import are plain. Testable with golden files. No new language to learn or secure. | Users cannot write logic. Anything beyond the schema needs a new schema version. |
| B. A custom DSL (new grammar, parser, interpreter) | A purpose-built language for layouts and rules. | Compact for people who want to script their UI. | Parser and interpreter in every head, a second source of truth, error messages to design, and an attack surface. The design set shows no need. |
| C. Embedded general-purpose scripting (Lua, JavaScript, C# scripting) | Run user scripts inside the client. | Maximum flexibility. | Sandboxing is hard, and scripts could call the wire. Conflicts with the no-code-in-config rule, with trimming and ReadyToRun, and with Android. Rejected. |
| D. Constrained expression sub-language for predicates only (later, optional) | A CEL-style, non-Turing-complete expression for visibility ("show when unread > 0"), evaluated over a fixed set of variables. | Covers the one place typed data is awkward. Easy to bound: no loops, no I/O, cost limit. | Needs a library choice (not made here). Not needed for v1. |

| B'. Full DSL carried inside the typed JSON (owner choice, 2026-11-12) | Documents keep their typed JSON shape. Any field that holds logic (layout rules, visibility, derived values) holds DSL source, parsed and type-checked at save and on load. The DSL is a small, total language: no loops without bounds, no I/O, fixed step and time budgets, and a closed set of variables and functions that the module manifests declare. | One codec and one validator for everything. Rules can be checked at save, so bad logic is rejected before it reaches a device. Users can express layouts that the typed fields alone cannot. | A parser, type checker and evaluator in every head, which must give the same results. A new grammar to version. A larger security surface, handled by the sandbox limits above. |

**Recommendation (owner's choice, 2026-11-12):** A carries the structure, and B' carries the logic inside it. Option C is still rejected. Option D is absorbed into B': a visibility predicate is a DSL expression, not a separate language.

**Owner decisions behind this (2026-11-12):** typed JSON as the document format; a full DSL for layouts and rules; the DSL lives inside the JSON documents. The DSL's name and its grammar are not decided. Naming is the owner's call, so the grammar is a work package to design, not a fixed spec.

### 4.2 Module contract and trust

Every module, first-party or not, implements the same contract (section 8, `IClientModule` and `ModuleManifest`). The manifest declares the module's pane kinds, commands, settings keys, layout fragments and **capabilities**, meaning what it may call or read: wire requests by name, local storage, device services and notifications. The host enforces the manifest the same way for every module. Trust is a property of the manifest and of how the host runs the module, not of who wrote it.

| Option | Meaning | Trade-off |
|---|---|---|
| **M1. One contract, trust from the manifest** (recommended) | The base GUI, GC, Constellation, RyuseiFuda and any third-party module are all modules with the same manifest and the same host checks. | Consistent with the owner's modularity rule. Needs a capability model built once, for everyone. |
| M2. Privileged first-party modules | Built-in modules skip some checks. | Simpler start. Contradicts the owner's rule and creates a second path to keep safe. Not recommended. |

**Loading mode** (how a module that is not compiled into the shipped build runs). This is the open choice, and the owner decides it:

| Option | Meaning | Trade-off |
|---|---|---|
| L1. Compiled in only | Every module ships in the head build. No runtime loading. | Simplest and trim-friendly. No third-party modules without a rebuild. |
| L2. In-process assemblies via `AssemblyLoadContext` | Modules loaded at runtime, inside the client process. | Full speed and rich UI. Conflicts with trimming and ReadyToRun. Isolation rests on the manifest, not on a process boundary, so a bad module can crash the client. |
| L3. Out-of-process modules over a protocol (recommended for non-shipped modules) | A module runs in its own process and talks to the client through a versioned contract, the way engine adapters do in 20. Its UI is a descriptor (a layout kind with a model), rendered by generic views, or by a native view the head supplies for a whitelisted kind. | Isolation by process and by manifest. Keeps the shipped set trim- and R2R-friendly. More work per module, and a richer custom view needs more design. |

**Recommendation:** M1, with L1 for the shipped set and L3 for modules that are not shipped with the build. L2 stays out until a real need and the trimming question are settled. Engines already follow L3 through `PaneDescriptor` (20).

### 4.3 Where configuration lives

| Option | Verdict |
|---|---|
| **S. Server-side revisioned documents in Postgres, with JSON export and import** (recommended) | Single source of truth, audited, synced to every device, survives a device loss, and backed up with the rest of the state (doc 33). |
| V. Vault (Markdown in git) | Rejected. The Vault is knowledge that agents read and write (CLAUDE.md, doc 18). Putting GUI config there would let agents change the owner's UI, which conflicts with the approval rules. |
| L. Local files on each device | Allowed only for device-only presentation (window geometry, cache). Never the company's definition. |

### 4.4 Scopes and who can edit

Resolution order, later wins: **install defaults (shipped) → company → person → device**. Device scope holds only window geometry and cache. Each layer may override only what the layer above allows.

| Scope | Who may change | Approval | Notes |
|---|---|---|---|
| Install (shipped defaults) | Build only | none | Read-only at runtime. |
| Company | The owner, and operators with the owner's role | Tier 1 (applied at once, audited) | One company per install in v1 (doc 19). Keyed so multi-company is a later key, not a rewrite. Model is for the owner to confirm (section 10). |
| Person | That person | none | Can override presentation and command bindings. Cannot override company rules marked locked. |
| Device | That device | none | Local only. |
| Agents | Nobody | n/a | No tool, endpoint or ability to change any GUI document. A suggested change arrives as a decision card for a human. |
| Imported file | The owner | Tier 2 (held until approved) | An import is untrusted input (section 6). |

### 4.5 Sync

- `ui.get { deviceClass, viewport, posture }` returns a `UiProfile` with the variants for each posture and a revision (doc 02).
- `ui.changed { revision }` tells connected clients a document changed. The client refetches only the changed document.
- Writes use optimistic concurrency: a change carries the revision it was made against. A stale write is refused with the current revision, and the client shows a merge prompt for layout changes. No silent last-writer-wins for company documents.
- Offline cold start uses the last cached profile (27). The cache holds documents and counters only, never message bodies.
- Personal documents sync the same way, so a person's layout follows them across devices.

### 4.6 Head constraints

| Concern | Windows | Linux | Android |
|---|---|---|---|
| Theme switch | Swap an Avalonia `ResourceDictionary` at runtime; no restart. Monaco theme regenerated from the same tokens (gated by S12). | Same as Windows. | Same as Windows. No Monaco. |
| Layout build | Build the control tree in code from the layout document through the module registry. Do not build XAML from user data at runtime. | Same. | Same. Posture variant chosen locally from fold state (29). |
| Trimming and ReadyToRun | Registry and generators are plain code, so they are trim- and R2R-friendly. Avoid reflection-based XAML loading on paths the preloader uses (doc 40). | Same. | Verify on device before claiming trim safety (27 conflict 3, unverified). |
| Fonts | Font families are names only. No remote font fetch. Fonts ship with the head or come from the OS. | Same. | Same. |
| Accessibility | Theme contrast validation is mandatory (4.5:1 on text pairs, as 27). | Same. | Same. TalkBack unverified. |

### 4.7 Performance

- Documents are small. Targets: under 64 KB per document and under 256 KB per profile. Both are operator-settable limits with those defaults. Validation runs once on load and on change, not per frame.
- A layout change rebuilds one subtree, not the window. Module view models survive a layout move, so a GC view keeps its scroll position.
- Theme changes update resources, so bound controls update without rebuilding the tree.
- Target: the cached shell with layout and theme paints inside the 1 s budget already in 27. This is a target to measure, not a claim.

## 4.8 Devices, displays and posture (added 2026-11-12)

Docs 27 and 29 cover posture (folded, unfolded, half-open) and send one layout variant per posture. They do not model a foldable's two screens as separate displays, nor device classes beyond `deviceClass` in `ui.get`. This section fills that gap for doc 41.

- **Display identity is a separate axis from posture.** A foldable's cover screen and inner screen are different displays with their own size and DPI. Posture describes the hinge state; display describes which screen the window is on. Layout variants are keyed by `(deviceClass, display, posture)`.
- **Cover screen (folded) is the main chat interface.** It is the primary way to use Bazaar on the go: a GC list the person can click into and chat, with the top navigation at the top. Its layout is a document like any other, with its own variant. Until a company sets a cover layout, it falls back to a default folded layout (GC list plus chat, per doc 29).
- **Inner screen (unfolded) is for heavy work and monitoring.** Its chat is only a notification ticker, because the cover screen is the chat. The inner screen holds split and swipeable panes with a lot of information at once: Constellation, RyuseiFuda (Workday, Orrery, Roadmap; the Task Board is part of Constellation and RyuseiFuda), Bazaar's own Git view, code viewer and Diffs. **Owner decision (2026-11-12):** a swipeable pager of Diffs, Task Board and Background, with the chat as a notification ticker. The three-column sketch is not used.
- **Device classes.** `phone`, `foldable`, `tablet`, `desktop`. A foldable is a phone-class device with two displays. The `deviceClass` value comes from the head and selects the default variant set.
- **Window moves.** When a window moves between displays (fold or unfold), the client selects the variant for the new display with no round trip. Pane view models survive the move.
- **Baked defaults.** Each company's build carries default variants for each device class and display, so a foldable works before any server connection.
- **Scope.** The variant key is a document field. It does not add a scope, so the scope table in 4.4 is unchanged.

**Cover screen (owner, 2026-11-12): a company-defined cover layout.** Each company defines it for glanceable triage. Until it is set, the cover screen falls back to the folded variant.

## 4.9 Desktop layout (added 2026-11-12)

Docs 27 and 28 cover desktop only as windows, the start dashboard and mnemonic navigation. This section sets the desktop variant for doc 41.

- **Default desktop layout (owner, 2026-11-12): GC rail, chat, pane tabs.** A Discord-style GC rail on the left. The active chat, PR or code view in the centre. Panes (Constellation, RyuseiFuda, Git, code viewer, Diffs) as tabs or dockable panels, which the person can rearrange.
- **Variant key.** Desktop variants are keyed by window size class, with breakpoints still to be set (open, below). The same layout document drives the desktop, the inner screen and the cover screen, each through its own variant.
- **Multiple monitors.** Open. Default proposal: one main window, with detached panes opened as separate windows (doc 28 process model).
- **Pane keyboard model.** Commands and pane focus use mnemonics, with no Ctrl chords (27, 28 §4). Ctrl+1-9 view switching from earlier notes is superseded.
- **Density.** Open. Default proposal: one density setting, comfortable by default, set per person.

Open for the owner: size-class breakpoints, the multi-monitor rule and the density default.

## 4.10 Standalone editor and terminal (added 2026-11-12)

- **Desktop width switch (owner, 2026-11-12): 1200 px.** Below 1200 px the desktop shows the compact layout, one pane at a time. At 1200 px and above it shows the wide layout (GC rail, chat, pane tabs). Multi-monitor and density are still open.

- **Process model (owner, 2026-11-12): same process, own windows.** The standalone editor and terminal are lightweight windows inside the same client process, as doc 28 describes. They share one connection and one Monaco bundle.
- **OS default handler.** The editor and terminal register as OS handlers, as decided in Bazaar.Windows §1. The person makes the default change in OS settings. Nothing is claimed silently.
- **Gates.** Making the terminal the default console host waits on spike S10. A standalone Monaco editor waits on the free-route Monaco spike (doc 27, S12).
- **Linux.** Handler registration uses `.desktop` and `xdg-mime` (doc 30).
- **Names (owner, 2026-11-12).** The terminal is **Calcinatio**, with CLI **CaLcInato** (owner confirmed, 2026-11-12; clearance check needed before public use). The editor is **Athanor** (owner chose it, 2026-11-12; clearance check needed before public use). The editor and terminal share one naming family (owner, 2026-11-12).

## 4.11 Terminal autocomplete (added 2026-11-12)

- **AI command autocomplete runs on the local NPU, off by default (owner, 2026-11-12).** It is a client-side compute exception (C17, with the NPU inference exception). A setting turns it on. Typed commands stay on the device.
- **Shell completion** is the baseline. AI suggestions sit on top of it and never replace it.
- **Gate.** The NPU path depends on doc 31 (local inference) and its backend availability.
- **Command bar AI matching suggests only, never runs (owner, 2026-11-12).** When a typed phrase is not a known command or target, AI matching may propose commands. It never executes them. The user confirms every proposal. Syntax-first commands (section 4.12) run directly.

## 4.12 Navigation grammar (added 2026-11-12, provisional)

- **Syntax first.** Single-letter verbs for speed: `m` move or navigate, `n` next, `i` `j` `k` `l` for directional navigation, plus other vim-style keys. These run with no AI involved.
- **Compound syntax (owner example, 2026-11-12).** `ngc1` + Enter means navigate, GC list, first (top) chat. `nrfdrm` + Enter means navigate to RyuseiFuda Roadmap. The verb letter comes first, then a short code for the target. Codes are per-module and configurable per company. The overlay (section 4.12, Enter rule) shows the full expansion before it runs.
- **Named targets** typed then Enter: `topgc` (most recent activity GC), `ryuseifuda` or `kanban` (RyuseiFuda board), `constellation`, `unread`, `settings`. Aliases are configurable per company.
- **Vim is the reference when in doubt (owner, 2026-11-12).** When a new verb, key or code is undecided, follow Vim's convention: a single letter for a common motion, a verb then a target (as in `d` then a motion), and counts as prefixes. The command bar is Vim-style mnemonics, not a separate language.
- **AI fallback.** Unknown phrases go to AI matching, which suggests only (section 4.11). Latency and locality rules follow doc 31.
- **Enter is always syntax (owner, 2026-11-12).** Enter runs the typed syntax or named target. Syntax input shows an overlay before it runs, stating exactly what it will do and where (for example "Move to: RyuseiFuda Workday"). The overlay sits beside the typed text.
- **AI matching is a separate key (owner, 2026-11-12, proposed).** As the user types, the overlay shows the syntax result immediately. AI loads slowly beside it and shows what the AI thinks the user wants, as a suggestion only (section 4.11). The AI key is Shift+Enter (owner decided, 2026-11-12). Ctrl+Enter was rejected because it conflicts with the no-Ctrl-chord rule. Android needs a non-chord route too (open).
- **No Ctrl chords.** Mnemonics and the command bar are the only entry points. Open: exact verb set and whether abbreviations are per-module.

## 5. Modules and existing panes

The base GUI and each feature are client modules that a head composes. This is the design in MODULARITY-AUDIT §4 and doc 40, restated for the single contract in 4.2.

A client module, `Bazaar.Client.<F>`, exposes one registration call that gives:

1. its pane kinds (view factory per kind),
2. its command contributions (id, title, mnemonic, keywords),
3. its settings keys (typed, with defaults, through the registry),
4. its default layout fragment (optional),
5. its manifest of capabilities (section 8).

A module references `Bazaar.Client.Core` only, never a head and never another module's internals. Heads compose the list. A layout can place only kinds that the composed list provides; anything else renders the placeholder card.

The base GUI (the shell, palette, theme, layout renderer and start screen) is also a module on `Bazaar.Core`, registered like the others. No pane or shell piece is wired into Core by name.

| Module | Project (planned) | Current code | Notes |
|---|---|---|---|
| Base shell (layout renderer, palette, theme, settings UI) | `Bazaar.Client.Core` as the base module (planned split) | Core today | Same contract as every other module. |
| GC | `Bazaar.Client.Gc` (planned) | `GcPaneModule` in Core | Core is the shared GC view today (doc 27 pipeline). |
| Constellation | `Bazaar.Client.Constellation` (planned) | `ConstellationPaneModule` | Renderer behind `IConstellationRenderer`; layout refers to the kind only. |
| RyuseiFuda (Workday, Orrery, Roadmap) | `Bazaar.Client.RyuseiFuda` (planned) | `WorkdayPaneModule`, `RyuseiSchedulePaneModule`, `RoadmapPaneModule` | Doc 38 names: Workday, Orrery, Roadmap. Workday board reads are owner-only (doc 12). |
| Start dashboard | `Bazaar.Client.Dashboard` (planned) | `DashboardPaneModule` | Widgets come from the `client.dashboard.order` setting. |
| Vault search, Soul profile | Their own modules (planned) | `VaultSearchPaneModule` and `SoulProfileView` | Soul profile is read-only in the client. |
| Engine panes | Out-of-process modules (L3, doc 20) | `EnginePaneView` | Kinds are namespaced by the engine, e.g. `godot.scene-tree`. |
| Terminal | Head-owned | Windows head | Local terminal only (C17 exception). Hosted by the head, so it is not a module in the sense of this doc. |

**Module packaging (owner, 2026-11-12): built into a company client.** Each company's extra modules are baked into that company's client build. This is a per-company build, not one generic client that loads modules from the server. Doc 37 must be updated to match; the module packaging and signing consequences are open for that doc.

**Consequence (owner, 2026-11-12): modules are compiled into each company's client.** Because each company builds its own client, company modules are compiled in, not loaded at runtime. That keeps startup static: the client does not load modules or build the GUI dynamically at each start, and preloading (doc 40) is simpler because the GUI is fixed at build time. The layout, theme and commands remain server documents, so per-company look still changes without a rebuild. Out-of-process loading (L3) remains only for engine panes (doc 20), not for company modules.

**Company defaults are baked into the build (owner, 2026-11-12).** Each company's default layout and theme ship inside its client build, as compiled defaults. The server still holds overrides at the person and device scopes, and can change them without a rebuild. A company-scope change on the server overrides the baked default for connected clients; the baked default is the offline and first-run fallback. Layout and theme therefore work before any server connection.

Names for new module projects are the owner's call (section 10). The planned names follow the `Bazaar.Client.<F>` pattern from MODULARITY-AUDIT. None is decided here.

## 6. Security and sandboxing of custom UI

Customization must not become a way to run code, leak data or escalate permissions. These rules apply to every module and every document, whoever wrote them.

1. **Data only.** Every document is JSON checked against its schema. Unknown top-level fields are rejected in an import and preserved (and ignored) when the document came from a newer server.
2. **No markup or code in documents, beyond the DSL.** No HTML, XAML, host scripts, templates or URLs. DSL source is the only permitted logic, and it is checked by the DSL sandbox: no I/O, no reflection, a closed function set, bounded steps and time, and a bounded output size. The validator extends the theme rule (`ThemeValidator`: no URL, markup, script or remote font in any string, including keys) to every document.
3. **Capabilities from the manifest.** A module can call, read or write only what its manifest declares. The host checks each call. Wire calls are still authorized server-side. A command bound in a document runs through the same approval tiers (doc 15) as the button it mirrors.
4. **No layout grants access.** A layout places panes. It cannot add a capability to a module.
5. **Taint.** An imported or shared document is external input. It is parsed as untrusted, cannot change permissions, and cannot be applied until the owner approves it (Tier 2). Its import is audited.
6. **Limits.** Maximum document size, pane count per layout, nesting depth (default 16) and command count. Operators can tighten these, not loosen them below the shipped floor.
7. **Failure mode.** A bad document is rejected with a path and code. The client keeps the last good profile and shows a notice. It never crashes on bad input. A failing module is isolated to its pane, which renders a failure card.
8. **No agent writes.** Agents hold no tool that changes these documents.
9. **Loading mode sets the isolation strength.** Under L1 the shipped set is trusted code and the manifest is checked in tests. Under L3 the process boundary adds isolation. Under L2 isolation is weakest, which is one reason it is not recommended.

## 7. Versioning and forward compatibility

- Each document has `schemaVersion` (integer, starting at 1). Within a version, changes are additive only: new optional fields, new kinds, new containers.
- The module manifest has its own `contractVersion`. Modules built against an older contract keep working when the change is additive. Breaking changes bump the version, and the host keeps a shim for the previous version for one release.
- Breaking changes to documents bump `schemaVersion`. The server keeps a migration function per step, tested with golden files in both directions where possible.
- The client declares the highest versions it understands in `hello`. The server returns documents in the highest version both sides know, and migrates down for old clients only when the migration is lossless. Otherwise the client gets a "client update required" notice, the same as the 4426 close rule in 27.
- Unknown containers stack their children vertically. Unknown pane kinds render the placeholder card. Unknown command keys are kept and ignored.
- Published JSON Schema files are versioned in the repo and drive the golden tests. The C# models are the source of truth for the codec, and CI fails if they drift from the published schemas.
- Forward-facing: source-generated `System.Text.Json` only, no reflection-based serializers, no deprecated APIs.

## 8. Data and interfaces

```csharp
// Shared shape of every customization document. Each document is stored as JSON with a revision.
public sealed record UiDocument<T>(string Kind, int SchemaVersion, long Revision, string ScopeKey, T Body);

// Layout tree (extends the LayoutDescriptor in 27). Pane leaves carry a kind and an optional config object.
public sealed record LayoutNode(string Container, IReadOnlyList<LayoutNode>? Children, string? PaneKind,
    string? PaneId, JsonElement? PaneConfig, string? VisibleWhen /* reserved, unused in v1 */);

// Manifest every client module declares. The host enforces it identically for every module.
public sealed record ModuleManifest(string Id, string Version, int ContractVersion,
    IReadOnlyList<string> PaneKinds, IReadOnlyList<string> Commands, IReadOnlyList<string> SettingKeys,
    IReadOnlyList<string> Capabilities /* e.g. wire:gc.history, storage:local, device:notify */,
    ModuleIsolation Isolation /* InProcessCompiled, OutOfProcess: depends on loading mode */);

public enum ModuleIsolation { InProcessCompiled, OutOfProcess }   // names PROPOSED; open question 2

// Module contract for Bazaar.Client.<F>. Heads call Register once with the context they supply.
public interface IClientModule
{
    ModuleManifest Manifest { get; }
    void Register(ClientModuleBuilder builder);
}

public interface IUiDocumentStore            // server side, backed by Postgres (doc 04)
{
    Task<UiDocument<JsonElement>?> GetAsync(string kind, string scopeKey, CancellationToken ct);
    Task<UiDocument<JsonElement>> PutAsync(string kind, string scopeKey, long expectedRevision,
        JsonElement body, CancellationToken ct);  // throws on revision mismatch
}

public interface IUiDocumentValidator       // pure; no Avalonia, no I/O; used by server and clients
{
    UiValidationResult Validate(string kind, JsonElement body);
}
```

Table (planned, migration owned by doc 04): `ui_document (kind, scope_key, revision, schema_version, body jsonb, updated_by, updated_at)` with a unique key on `(kind, scope_key)` and a history table for audit. Names are PROPOSED and not decided.

## 9. Behaviour and edge cases

- A revision mismatch on save returns the current document. The client shows the difference and does not overwrite.
- A document from a newer server with unknown fields is kept as-is in the cache and round-trips without loss.
- Two clients editing the same person's layout: the second save gets a revision conflict, as above.
- A pane whose module is disabled by its setting renders a "not available" card, with the layout unchanged so re-enabling restores it.
- A command mnemonic clash inside one scope fails validation at save. Across scopes, the more specific scope wins and a notice is logged once.
- A module that violates its manifest is stopped and its panes show a failure card. Other modules keep running.
- Keyboard: a layout change never moves focus. Focus stays where it was or falls to the nearest pane.
- Offline: the last cached profile renders. Edits made offline are not queued in v1. The client shows them as read-only until it reconnects.

## 10. Decisions for the owner

These are real design decisions. The owner asked to be asked, so none is settled by default here. Each has a recommendation. Work that depends on an open answer waits; the rest proceeds (section 12).

1. **Module model. DECIDED (owner, 2026-11-12): one contract, trust from the manifest.** Base GUI, GC, RyuseiFuda and third-party modules all use it.
   - Option 1: one contract, trust from the manifest (chosen). Consistent with total modularity.
   - Option 2: first-party modules are privileged and skip some checks. Simpler start, but a second path to keep safe.
2. **Loading mode for modules not shipped with the build. DECIDED (owner, 2026-11-12): out-of-process over a protocol (L3).**
   - Option 1: compiled in only (L1). Simplest and trim-friendly, but no third-party modules without a rebuild.
   - Option 2: out-of-process over a protocol (L3, chosen). Isolation by process and manifest; keeps the shipped set trim-friendly.
   - Option 3: in-process runtime loading (L2). Richest, but conflicts with trimming and ReadyToRun, and a bad module can crash the client.
3. **Customization model. DECIDED (owner, 2026-11-12): typed, versioned JSON documents.** Covers theme, layout, commands and start screen with no new language.
   - Option 1: typed JSON (chosen).
   - Option 2: a custom DSL. Only if you want users to script logic.
   - Option 3: embedded scripting. Not recommended: sandboxing is hard and it conflicts with trimming and Android.
4. **DSL scope. DECIDED (owner, 2026-11-12): a full DSL for layouts and rules, carried inside the typed JSON documents (section 4.1, option B').** Its grammar and runtime limits are work to design. Owner chose the category "short exotic name" (2026-11-12); the owner chose the name Rubedo (2026-11-12). Clearance and meaning check still to do before public use.
   - Option 1: none in v1 (not chosen).
   - Option 2: predicates only, a CEL-style subset for visibility rules, later.
   - Option 3: a full DSL. Not recommended.
5. **Company scope model. DECIDED (owner, 2026-11-12): one company per install, keyed for later.**
   - Option 1: one company per install, keyed (chosen).
   - Option 2: multi-company now, with its own tenancy and settings model. Larger scope.
6. **Who may override company locks? DECIDED (owner, 2026-11-12): the owner only.**
   - Option 1: the owner only (chosen).
   - Option 2: any operator with the owner's role.
7. **Importing layouts and themes from files. DECIDED (owner, 2026-11-12): allowed, with Tier 2 approval and validation.**
   - Option 1: allowed, with Tier 2 approval and validation (chosen).
   - Option 2: not allowed in v1. Settings only, created in the app.
9. **Cover screen on foldables. DECIDED (owner, 2026-11-12): the cover screen is the folded main chat, a company-defined layout, with a default GC list plus chat.**
10. **Names.** Document kinds (`theme`, `layout`, `commands`, `start`), the module namespace pattern, and the product-facing term for a layout.
   - Layout term: DECIDED (owner, 2026-11-12): "Layout". Options were "Layout" (recommended), "Workspace" or "Arrangement".
   - Module term: DECIDED (owner, 2026-11-12): "Client module". Options were "Client module" (recommended) or "Pane module".
   - Namespace: `Bazaar.Client.<F>` for every module, base shell included, as MODULARITY-AUDIT planned. Confirm or name the base shell differently.

## 11. Testing

- **Pure tests (no Avalonia):** schema validation, golden files for each document kind and version, migration round trips, revision conflicts, layout tree parsing with unknown kinds and containers, command mnemonic clash detection, limit enforcement, rejection of markup and URLs in every string position (including keys), manifest checks (a call outside the declared capabilities is refused).
- **Server tests:** `IUiDocumentStore` against Postgres (Testcontainers): put, get, revision mismatch, audit row written, scope resolution order.
- **Headless (Avalonia.Headless, `[AvaloniaFact]`):** a layout tree builds the expected control tree; a missing pane renders the placeholder; a failing module shows a failure card while others keep working; theme switch updates bound controls without rebuild; a moved pane keeps its view model; posture variant selected locally with no socket.
- **Compatibility:** a newer document with unknown fields round-trips; an old client receives a lossless downgrade or the update notice.
- **Device checklist (doc 34):** trimming and ReadyToRun on Android, theme switch on phone, TalkBack.

## 12. Work packages

Each package needs its own tests (CLAUDE.md). Haiku sub-agents take the independent ones in parallel, one worktree each. "Waits" means blocked on an answer in section 10.

1. Document models and JSON Schemas for `theme`, `layout`, `commands`, `start`, with `schemaVersion` 1. Pure. Not blocked.
2. `IUiDocumentValidator` covering the rules in section 6, excluding the module manifest rules. Depends on 1. Not blocked.
3. Theme pipeline: move the existing `ThemeModel` and validator behind the document model. Depends on 1. Not blocked.
4. Layout tree builder through the module registry (extends `PaneRegistry`), with placeholder fallback and posture selection. Depends on 1. Not blocked.
5. `ModuleManifest`, `IClientModule` and `ClientModuleBuilder`. Waits on section 10 questions 1 and 2. Decided: one contract and out-of-process loading for non-shipped modules.
5a. DSL design: grammar, type system, closed function set, sandbox limits, and a golden-test suite shared by every head so the evaluators agree. Waits on the DSL name and grammar review (owner). A design doc comes first.
6. Migrate GC, Dashboard, Vault and RyuseiFuda panes to modules one at a time, starting after 5. This is MODULARITY-AUDIT step 7. Waits on 5.
7. Server store `ui_document` with migration, revision checks, audit. Depends on doc 04 migration runner and doc 03 settings registry. Waits on section 10 question 5 for scope keys only.
8. `ui.get` and `ui.changed` for documents, with posture variants. Depends on 7 and doc 02 WP.
9. Client cache for documents and counters. Depends on 8.
10. Import and export with Tier 2 approval. Depends on 7, doc 15, and section 10 question 7.
11. Headless and golden tests for all of the above. Depends on 4 and, for module tests, 5.

Constellation and Workday renderer work is out of scope here, except that the Constellation module must reference the renderer through its abstraction only.

## 13. Conflicts

1. **Config in the Vault would be the natural place for "editable" files, but agents can read and write the Vault.** Resolved here: config lives in Postgres (section 4.3). The Vault can hold an exported copy only if agents cannot write to it.
2. **Runtime module loading (doc 20, `AssemblyLoadContext`) vs the trim and ReadyToRun direction (owner, 2026-11-11).** Open. Section 10 question 2 decides it. Until then the shipped set is compiled in and non-shipped modules are not loaded.
3. **Layout computed on the server (C17) vs client-side variant selection (C27).** Both hold: the server sends variants for every posture, and the client selects one locally with no round trip.
4. **Per-person layouts vs single company layout.** Both exist as scopes. The company layout is the default. A person can override only what the company allows.
5. **"First-party" wording in older docs (27 Q3, 20 Q7, MODULARITY-AUDIT §5).** Superseded by the owner's modularity rule (2026-11-12): there is no privileged first-party path. Those docs should be updated when section 10 question 1 is answered.
