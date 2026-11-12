# Client GUI customization, layout and DSL question

Status: PROPOSED (2026-11-12) · Owner questions: 8 (section 10) · Recommendations are defaults that the build may follow now; anything marked OPEN is a choice the owner has not made
Depends on: 02-wire-protocol-and-sync.md (`ui.get`, `ui.changed`, `prefs.set`), 03-server-host-and-providers.md (settings registry), 15-security-taint-and-approvals.md (approval tiers, `audit_log`), 20-engine-integration-api.md (`PaneDescriptor`), 27-client-architecture.md (layout, theme, keyboard model), 28-windows-client.md, 29-android-client.md, 38-ryusei-fuda.md, 19-constellation-and-git-solar-system.md · Used by: 30-linux-client-and-packaging.md, 34-testing-strategy.md, 40-core-feature-modules-audit.md

## 1. Purpose

Bazaar's clients have to be fully customizable per Bazaar company: theme, layout, which panes sit where, what the start screen shows, which commands the palette offers, and per-company configuration. The owner asked three things: how the GUI works, how it stays customizable without losing safety or performance, and whether a DSL is warranted. This doc answers those in one place, using the existing design set rather than a new stack.

The short answer:

- **Customization is data, not code.** A small set of versioned, schema-checked JSON documents (theme, layout, start screen, command bindings, settings) describes the GUI. No document can carry code, markup, URLs or scripts.
- **No DSL in v1.** A custom language adds a parser, a second place where behaviour lives, a security surface and a support burden, and it buys nothing the typed documents do not already give. One narrow expression sub-language for visibility and predicates is allowed later, if a real need shows up (section 4, option D).
- **Panes stay compiled-in first-party code** in v1, each one an `IPaneModule` in its `Bazaar.Client.<F>` module. Layout documents place panes; they never define them.
- **Config lives on the server**, revisioned, scoped (install, company, person, device), and synced through `ui.get` and `ui.changed`. Agents cannot edit it.

## 2. Decided inputs (not re-opened here)

- One Avalonia codebase, `Bazaar.Client.Core` plus thin heads. WinUI 3 and Jetpack Compose are retired (CLAUDE.md).
- Avalonia stays **conditional on the Monaco hosting gate (S12)**. Android never uses Monaco (27, DECISIONS-LOG round 5).
- The server does all compute. Layout stays server-side: the server sends a variant per posture and the client selects one locally (27, C17, C27).
- Theme: one file drives Avalonia and Monaco; validation rejects URLs, scripts and remote fonts (27, `src/Bazaar.Client.Core/Theme/`).
- Panes register through `IPaneModule` and are composed by `PaneRegistry.Compose` (`src/Bazaar.Client.Core/Panes/`). Unknown kinds render a placeholder and are reported once.
- Keyboard: mnemonics only, no Ctrl chords for app commands, nothing focused by default (27, Windows doc 28 §4).
- Configurability rule: a value left open becomes an operator setting with a safe default, audited on change. Floors and security invariants are not settings and settings may only tighten them (DECISIONS-LOG round 13).
- Third-party module loading is deferred; v1 modules are first-party and compiled in (MODULARITY-AUDIT §5, doc 40).
- Constellation's renderer is kept generic behind a boundary, with a Bazaar adapter, so it can be extracted later (owner, 2026-11-12). Layout documents refer to the Constellation pane by kind only, never to renderer internals.
- Client builds target .NET 11, use JSON through `IWireCodec` with source-generated `System.Text.Json`, and follow the forward-facing rule (CLAUDE.md, DECISIONS-LOG).

## 3. What "customizable" covers

| Surface | Example | Document | Default owner |
|---|---|---|---|
| Theme tokens | colours, fonts, spacing, radii, role and presence colours, reduced-motion flag | `theme` (exists in code: `ThemeModel`, `ThemeValidator`) | company, person may override |
| Layout | split, stack, tabs and dock containers with pane leaves, per posture | `layout` | company default, person may override |
| Start screen | section order, widgets, counters | `client.dashboard.order` (already a setting, 27) plus widget list | company |
| Command bindings | mnemonic for a command, hidden commands, palette keywords | `commands` | person (within company rules) |
| Pane configuration | filters, sort order, page size for a pane instance | `pane` options inside the layout leaf | person |
| Preferences | hints delay, group window, clipboard rule | settings registry (27 table) | per the registry's "who may change" column |

The pane set itself is **not** customizable at runtime. A company can hide, place or configure a pane; it cannot add a pane kind that the head does not ship.

## 4. Options and recommendation

### 4.1 Customization model

| Option | What it is | Pros | Cons |
|---|---|---|---|
| **A. Typed JSON documents with schema versions** (recommended) | Each surface above is a JSON document, validated against a published JSON Schema 2020-12 and a C# model with source-generated `System.Text.Json`. | Same codec as the wire (`IWireCodec`). Validation, diff, export and import are plain. Trivially testable with golden files. No new language to learn or secure. | Users cannot write logic. Anything beyond the schema needs a new schema version. |
| B. A custom DSL (new grammar, parser, interpreter) | A purpose-built language for layouts and rules. | Compact for humans who want to script their UI. | Parser and interpreter to maintain in every head, a second source of truth, error messages to design, and an attack surface. No need is shown in the design set. |
| C. Embedded general-purpose scripting (Lua, JavaScript, C# scripting) | Run user scripts inside the client. | Maximum flexibility. | Sandboxing is hard; scripts can call the wire. Conflicts with the no-code-in-config rule, with trimming and ReadyToRun on clients, and with Android. Rejected. |
| D. Constrained expression sub-language for predicates only (later, optional) | A CEL-style, non-Turing-complete expression for visibility ("show when unread > 0"), evaluated over a fixed set of variables. | Covers the one place typed data gets awkward. Easy to bound: no loops, no I/O, cost limit. | Needs a library choice (not made here). Not needed for v1. |

**Recommendation:** A now. Keep D as a reserved schema field (`visibleWhen`, unused in v1). Reject B and C. Revisit D only when a second real use case appears in the design set.

**Answer to "is a DSL warranted":** no, not for v1. The customization the owner described (theme, per-company layout, panes, per-company config) is fully expressed by typed documents. A DSL would be justified only by user scripting of logic, which is a product decision, not a UI one.

### 4.2 Plugin-contributed panes

| Option | Meaning | Status |
|---|---|---|
| P1. First-party compiled panes only (recommended for v1) | Each `Bazaar.Client.<F>` ships its pane kinds. Heads compose the list. | Default in 27 Q3 and MODULARITY-AUDIT. |
| P2. In-process pane plugins via `AssemblyLoadContext` | Third-party DLLs loaded at runtime. | Rejected for v1: conflicts with trimming and ReadyToRun goals, adds signing and isolation work, and no v1 feature needs it (27 conflict 3). |
| P3. Out-of-process engine panes described by `PaneDescriptor` | Engines (Godot, Roblox) send a namespaced descriptor and snapshots; the client renders them with a generic tree, log, form or table view. | Already in 20. Compatible with this design: the layout references the kind, and the view is generic. |

**Recommendation:** P1 for v1, P3 for engines (already designed). Third-party UI code is out of scope until doc 40's extension contract exists.

### 4.3 Where configuration lives

| Option | Verdict |
|---|---|
| **S. Server-side revisioned documents in Postgres, with JSON export and import** (recommended) | Single source of truth, audited, synced to every device, survives a device loss, and backed up with the rest of the state (doc 33). |
| V. Vault (Markdown in git) | Rejected. The Vault is knowledge that agents read and write (CLAUDE.md, doc 18). Putting GUI config there would let agents change the owner's UI, which conflicts with the approval rules. |
| L. Local files on each device | Allowed only for device-only presentation (window geometry, cache). Never the company's definition. |

### 4.4 Scopes and who can edit

Resolution order, later wins: **install defaults (shipped) → company → person → device**. Device scope holds only window geometry and cache. Each layer can only override what the layer above allows.

| Scope | Who may change | Approval | Notes |
|---|---|---|---|
| Install (shipped defaults) | Build only | none | Read-only at runtime. |
| Company | The owner, and operators with the owner's role | Tier 1 (applied at once, audited) | The "company" is one per install in v1 (the single-company model in doc 19). Keyed so multi-company is a later key, not a rewrite. |
| Person | That person | none | Can override presentation and command bindings. Cannot override company rules that are marked locked (for example, a company's approval banner). |
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
| Layout build | Build the control tree in code from the layout document through the pane registry. Do not build XAML from user data at runtime. | Same. | Same. Posture variant chosen locally from fold state (29). |
| Trimming and ReadyToRun | Registry and generators are plain code, so they are trim- and R2R-friendly. Avoid reflection-based XAML loading on paths the preloader uses (doc 40). | Same. | Verify on device before claiming trim safety (27 conflict 3, unverified). |
| Fonts | Font families are names only. No remote font fetch. Fonts ship with the head or come from the OS. | Same. | Same. |
| Accessibility | Theme contrast validation is mandatory (4.5:1 on text pairs, as 27). | Same. | Same. TalkBack unverified. |

### 4.7 Performance

- Documents are small (target under 64 KB per document, under 256 KB per profile, both as operator-settable limits with those defaults). Validation runs once on load and on change, not per frame.
- A layout change rebuilds one subtree, not the window. Pane view models survive a layout move, so a GC view keeps its scroll position.
- Theme changes update resources, so bound controls update without rebuilding the tree.
- Target: the cached shell with layout and theme paints inside the 1 s budget already in 27. This is a target to measure, not a claim.

## 5. Modules and existing panes

Each feature becomes a client module that a head composes. This is the design in MODULARITY-AUDIT §4 and doc 40, restated for customization.

A client module, `Bazaar.Client.<F>`, exposes one registration call that gives:

1. its pane kinds (`IPaneModule` with its view factory),
2. its command contributions (id, title, mnemonic, keywords),
3. its settings keys (typed, with defaults, through the registry),
4. its default layout fragment (optional).

It references `Bazaar.Client.Core` only, never a head and never another feature's internals. Heads compose the list. A layout can place only kinds that the composed list provides; anything else renders the placeholder card.

| Existing pane | Module | Current state | Notes |
|---|---|---|---|
| GC | `Bazaar.Client.Gc` (planned) | `GcPaneModule` in Core | Core is the shared GC view today (doc 27 pipeline). |
| Constellation | `Bazaar.Client.Constellation` (planned) | `ConstellationPaneModule` | Renderer behind `IConstellationRenderer`; layout refers to the kind only. |
| RyuseiFuda (Workday, Orrery, Roadmap) | `Bazaar.Client.RyuseiFuda` (planned) | `WorkdayPaneModule`, `RyuseiSchedulePaneModule`, `RoadmapPaneModule` | Doc 38 names: Workday, Orrery, Roadmap. Workday board reads are owner-only (doc 12). |
| Start dashboard | `Bazaar.Client.Dashboard` (planned) | `DashboardPaneModule` | Widgets come from the `client.dashboard.order` setting. |
| Vault search, Soul profile | Their own modules (planned) | `VaultSearchPaneModule` and `SoulProfileView` | Soul profile is read-only in the client. |
| Engine panes | Generic (doc 20) | `EnginePaneView` | Kinds are namespaced by the engine, e.g. `godot.scene-tree`. |
| Terminal | Head-owned | Windows head | Local terminal only (C17 exception). |

Names for new module projects are the owner's call. The planned names above follow the owner's naming rule (`Bazaar.Client.<F>`, MODULARITY-AUDIT). No new name is decided here.

## 6. Security and sandboxing of custom UI

Customization must not become a way to run code, leak data or escalate permissions.

1. **Data only.** Every document is JSON checked against its schema. Unknown top-level fields are rejected in an import and preserved (and ignored) when the document came from a newer server.
2. **No markup or code.** No HTML, XAML, scripts, expressions (v1), templates or URLs. The validator extends the existing theme rule (`ThemeValidator`: no URL, markup, script or remote font in any string, including keys) to every document.
3. **No new permissions.** A layout cannot grant access. A pane shows what its module's wire calls return, and those calls are already authorized server-side. A command bound in a document runs through the same approval tiers (doc 15) as the button it mirrors.
4. **Taint.** An imported or shared document is external input. It is parsed as untrusted and cannot change permissions, can't be applied until the owner approves it (Tier 2), and its import is audited.
5. **Limits.** Maximum document size, pane count per layout, nesting depth (default 16) and command count. Operators can tighten these, not loosen them below the shipped floor.
6. **Failure mode.** A bad document is rejected with a path and code. The client keeps the last good profile and shows a notice. It never crashes on bad input.
7. **No agent writes.** Agents hold no tool that changes these documents.

## 7. Versioning and forward compatibility

- Each document has `schemaVersion` (integer, starting at 1). Within a version, changes are additive only: new optional fields, new kinds, new containers.
- Breaking changes bump the version. The server keeps a migration function per version step, tested with golden files in both directions where possible.
- The client declares the highest version it understands in `hello`. The server returns documents in the highest version both sides know, and migrates down for old clients only when the migration is lossless. Otherwise the client gets a "client update required" notice, the same as the 4426 close rule in 27.
- Unknown containers stack their children vertically. Unknown pane kinds render the placeholder card. Unknown command keys are kept and ignored.
- Published JSON Schema files are versioned in the repo and drive the golden tests. The C# models are the source of truth for the codec, and CI fails if they drift from the published schemas.
- Forward-facing: use source-generated `System.Text.Json` only, no reflection-based serializers, and no deprecated APIs.

## 8. Data and interfaces

```csharp
// Shared shape of every customization document. Each document is stored as JSON with a revision.
public sealed record UiDocument<T>(string Kind, int SchemaVersion, long Revision, string ScopeKey, T Body);

// Layout tree (extends the LayoutDescriptor in 27). Pane leaves carry a kind and an optional config object.
public sealed record LayoutNode(string Container, IReadOnlyList<LayoutNode>? Children, string? PaneKind,
    string? PaneId, JsonElement? PaneConfig, string? VisibleWhen /* reserved, unused in v1 */);

// Module contract for Bazaar.Client.<F>. Heads call Register once with the context they supply.
public interface IClientModule
{
    string Id { get; }
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
- A pane that is in a layout but disabled by its module's setting renders a "not available" card, with the layout left unchanged so re-enabling restores it.
- A command mnemonic clash inside one scope fails validation at save. Across scopes, the more specific scope wins and a notice is logged once.
- Keyboard: a layout change never moves focus. Focus stays where it was or falls to the nearest pane.
- Offline: the last cached profile renders. Edits made offline are not queued in v1. The client shows them as read-only until it reconnects.

## 10. Open questions for the owner

Each has a default that the build will use now. The owner does not need to answer for work to start.

1. **DSL in v1?** Default no. Option A (typed JSON). Revisit only if a use case needs user logic. [Decision needed only if the owner wants scripting.]
2. **Expression sub-language later (option D)?** Default reserved field, unused. Library choice is open.
3. **Pane plugins from third parties?** Default first-party only in v1 (P1), engines via descriptors (P3). Matches 27 Q3 and 20 Q7.
4. **Company scope model.** Default one company per install, keyed for later multi-company. Naming of "company" in config UI is open.
5. **Who may override company locks?** Default owner only. Person overrides cannot lift a locked company rule.
6. **Import of layouts and themes from files.** Default allowed with Tier 2 approval and validation. Sharing across installs is out of scope.
7. **Limits.** Defaults in section 4.7 and 6.5 are operator settings with floors. Owner may set the floors.
8. **Names.** Document kinds (`theme`, `layout`, `commands`, `start`), the module namespace pattern and any product-facing term for a "layout" are the owner's call. Options for the layout term: "Layout", "Workspace", "Arrangement". Options for the module term: "Client module" (default), "Pane module". Recommendation: keep "Layout" and "Client module" unless the owner chooses otherwise.

## 11. Testing

- **Pure tests (no Avalonia):** schema validation, golden files for each document kind and version, migration round trips, revision conflicts, layout tree parsing with unknown kinds and containers, command mnemonic clash detection, limit enforcement, reject markup and URL in every string position (including keys).
- **Server tests:** `IUiDocumentStore` against Postgres (Testcontainers): put, get, revision mismatch, audit row written, scope resolution order.
- **Headless (Avalonia.Headless, `[AvaloniaFact]`):** a layout tree builds the expected control tree; a pane that is missing renders the placeholder; theme switch updates bound controls without rebuild; a moved pane keeps its view model; posture variant selected locally with no socket.
- **Compatibility:** a newer document with unknown fields round-trips; an old client receives a lossless downgrade or the update notice.
- **Device checklist (doc 34):** trimming and ReadyToRun on Android, theme switch on phone, TalkBack.

## 12. Work packages

Each package needs its own tests (CLAUDE.md). Haiku sub-agents take the independent ones in parallel, one worktree each.

1. Document models and JSON Schemas for `theme`, `layout`, `commands`, `start`, with `schemaVersion` 1. Pure. No dependencies.
2. `IUiDocumentValidator` covering all rules in section 6. Depends on 1.
3. Theme pipeline: move the existing `ThemeModel` and validator behind the document model. Depends on 1.
4. Layout tree builder through `PaneRegistry`, with placeholder fallback and posture selection. Depends on 1 and the pane registry (already merged).
5. `IClientModule` and `ClientModuleBuilder`; migrate GC, Dashboard, Vault and RyuseiFuda panes to modules one at a time. Depends on 4. This is MODULARITY-AUDIT step 7.
6. Server store `ui_document` with migration, revision checks, audit. Depends on doc 04 migration runner and doc 03 settings registry.
7. `ui.get` and `ui.changed` for documents, with posture variants. Depends on 6 and doc 02 WP.
8. Client cache for documents and counters. Depends on 7.
9. Import and export with Tier 2 approval. Depends on 6 and doc 15.
10. Headless tests and golden files for all of the above. Depends on 4 and 5.

Constellation and Workday renderer work is out of scope here, except that the Constellation module must reference the renderer through its abstraction only.

## 13. Conflicts

1. **Config in the Vault would be the natural place for "editable" files, but agents can read and write the Vault.** Resolved here: config lives in Postgres (section 4.3). The Vault can hold an exported copy only if it is read-only to agents.
2. **Pane plugins (doc 20, `AssemblyLoadContext`) vs the trim and ReadyToRun direction (owner, 2026-11-11).** Resolved for v1 by P1. The runtime loader stays out until doc 40 defines a contract.
3. **Layout computed on the server (C17) vs client-side variant selection (C27).** Both hold: the server sends variants for every posture, and the client selects one locally with no round trip.
4. **Per-person layouts vs single company layout.** Both exist as scopes. The company layout is the default. A person can override only what the company allows.
