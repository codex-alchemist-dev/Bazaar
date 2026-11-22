# Rubedo view declarations: how the client GUI is declared

Status: ACCEPTED (owner, 2026-11-15; answers recorded in section 9). Originally PROPOSED (2026-11-13). The owner's direction (2026-11-13): the client GUI is written declaratively in Rubedo. Rubedo is the foundation (doc 41a, DECIDED). This doc defines the view layer on top of the expression language. Questions in section 9 go to the owner by card before the code is built.
Depends on: 41a-rubedo-dsl-design.md (expressions, typed context, limits), 41-client-gui-customization-and-dsl.md (sections 4.8, 6, 7, 8), 41b-client-module-boundaries.md (modules, compiled-in first-party), 15-security-taint-and-approvals.md (approval tiers).

## 1. Purpose

A pane's appearance and behaviour are a Rubedo view document, not C#. A module ships its views as data. The head renders the view with its own adapter (Avalonia on desktop, Android views on the phone, terminal on Calcinato). Views carry no code, so the same document runs on every head and on the server for validation.

Non-goals: no scripting in views (expressions only, doc 41a); no widget that runs host code; no view can add a capability (doc 41 section 6, rule 4).

## 2. Document shape

```
view document (schema version 1)
  dslVersion: 1
  name: "bazaar.gc.feed"        dotted, unique per module
  data: { <name>: <source> }    typed data sources the view reads (section 4)
  root: <node>
  actions: { <id>: <action> }   named actions the nodes refer to (section 6)
```

A node is `{ "widget": "<kind>", "props": { ... }, "children": [ ... ] }`. Props are typed values or Rubedo expressions in the form `{ "expr": "<source>" }`.

## 3. Widgets (closed set, v1 proposal)

| Widget | Children | Role |
|---|---|---|
| `stack` | yes | Vertical stack |
| `row` | yes | Horizontal row, wraps by flag |
| `grid` | yes | Two-dimensional grid, column count prop |
| `scroll` | one | Scrolling region |
| `text` | no | Text with a style token; localisation key or literal |
| `icon` | no | Icon from a closed icon set, chosen by a required `name` prop (a name outside the set fails the node; the validator reports `view.missing_icon_name` when it is absent) |
| `button` | no | Calls a named action |
| `input` | no | Text input bound to a value (write goes through an action) |
| `list` | template | Keyed list over a data source, one item template |
| `tabs` | yes | Named tabs |
| `badge` | no | Count or status chip. `variant` is from a closed set the theme colours: `neutral`, `owner`, `agent`, `system`, and the health states `healthy`, `degraded`, `failing`, `recovering`, `frozen`, `unobserved` |
| `divider` | no | Separator |
| `placeholder` | no | Failure card (doc 41 section 6, rule 7) |

**Text props.** `text` and `badge` take a `text` prop: a literal, or an expression of any type that renders as text (a Number or Text binding is shown as written). A bare identifier reads that field of the scope; any other expression is evaluated by Rubedo. `badge` also takes a `variant` from the theme's closed variants. `button` and `icon` take `label` (the accessible name); `icon` also takes `name` from the closed icon set. `visibleWhen` and `enabledWhen` are Rubedo bool expressions (or literal bools) on any node: the first shows or hides it, the second enables or disables it, both evaluated against the node's scope, so a list item's template sees its own fields. A value that is not a bool fails that node closed.

**Conditions.** Any node may carry `visibleWhen` and `enabledWhen`, each a Bool expression over the node's scope. A hidden node stays in the tree so a later data change can show it. A condition that does not evaluate to a Bool fails that node closed.

**Conditions.** Any node may carry `visibleWhen` and `enabledWhen`, each a Bool expression over the node's scope. A hidden node stays in the tree so a later data change can show it. A condition that does not evaluate to a Bool fails that node closed.

**Conditions.** Any node may carry `visibleWhen` and `enabledWhen`, each a Bool expression over the node's scope. A hidden node stays in the tree so a later data change can show it. A condition that does not evaluate to a Bool fails that node closed.

**Tab items (structural, not a widget).** A `tabs` node takes only `tab` children, and a `tab` is valid only directly under `tabs`. A tab has a required non-empty `title` prop (a localisation key or literal) and exactly one child, the page. The page is an ordinary node. `tab` is not in the closed set, so the widget set stays at 13. Validator codes: `view.tabs_empty` (no tabs), `view.tab_child` (a child of `tabs` that is not a `tab`), `view.tab_title`, `view.tab_page` (not exactly one page), `view.tab_placement` (a `tab` anywhere else).

Unknown widgets in a newer document render as `placeholder`, never as a crash (doc 41 section 7).

## 4. Data

- A view reads only the data sources it declares. Each source is named in the module manifest with its capability (for example `wire:gc.history`). The host checks the capability at load.
- Each source has a typed shape (number, string, bool, or a list of records with declared fields). A binding is a Rubedo expression over those names, for example `data.unread > 0`.
- Bindings re-evaluate when a source changes. A source's change notice is the only trigger. There is no polling inside views.
- A view never fetches data itself. The head or module supplies it, so views stay pure.

## 5. Theme and layout

- Views reference theme tokens by name (`color.text.primary`, `space.md`). They carry no raw colours, fonts or sizes. This keeps one theme file driving every head (doc 41 section 5 and the theme rules).
- A layout node names a pane kind. The pane's view is looked up by that kind. A pane kind with no view falls back to the module's default view, or to `placeholder`.
- Layout's `visibleWhen` is a Rubedo expression (doc 41a section 10), the same as any other expression.

### 5.1 Token catalog, hue, material and motion tokens (PROPOSED, 2026-11-22; from doc 93)
Doc 93 makes colours, materials, type, spacing, durations, springs and each micro-interaction an overridable token. View documents are how a company overrides them, so the view layer needs to know the token names without Rubedo depending on any theme code.
- **Catalog is injected.** Rubedo defines `IViewTokenCatalog` (`bool Contains(string name)`, `TokenKind? KindOf(string name)`, `IEnumerable<string> Names`) in `Bazaar.Rubedo.Views`. The host fills it from the theme and motion documents (`Bazaar.Client.Theme` today). Rubedo references no theme or Bazaar code, so it can move to the shared repo unchanged. `ViewValidator.Validate(document, options)` takes the catalog in its options.
- **Kinds.** Rubedo's `TokenKind` mirrors `Bazaar.Client.Theme.TokenKind` by name (`Colour`, `FontFamily`, `Length`, `Flag`, `Number`, `Duration`, `Choice`) and adds `Interaction` for `motion.interaction.<name>`, which names a preset and is not a value. Prop kinds map the same way (a colour prop takes `Colour`, a duration prop `Duration`, `motion` takes `Interaction`).
- **Adapter module.** The catalog adapter lives in its own small host-side module, `Bazaar.Client.Rubedo.Tokens`, which references `Bazaar.Rubedo`, `Bazaar.Client.Theme` and the motion module and implements `IViewTokenCatalog` from `ThemeTokens.Required` plus the loaded motion document. `Bazaar.Client.Theme` does not reference Rubedo. `motion.interaction.<name>` is a dynamic namespace: each document declares its names (with `liquid`, `subtle` and `reduced` variants) and the catalog lists the ones it has loaded.
- **Namespaces doc 93 adds:** `color.hue.0` to `color.hue.11` (the golden-ratio family), `material.mode`, `material.tint`, `material.opacity`, `motion.duration.*`, `motion.spring.*`, `motion.scale.desktop`, `motion.liquid.*`, and `motion.interaction.<name>` (a named interaction preset with `liquid`, `subtle` and `reduced` variants). The catalog, not Rubedo, owns the list; Rubedo only checks membership and kind.
- **Layout and gesture tokens (from the GUI thread, draft #1200).** Two more prefixes sit beside `color.*`, `material.*` and `motion.*`: `layout.*` and `gesture.*`. Seven tokens are listed so far, all kind `Number`: `layout.sidebar.width.expanded`, `layout.sidebar.width.collapsed`, `layout.strip.height`, `layout.sheet.width` and `layout.sheet.side.width` (0 to 1024 dip), `material.scrim.opacity` and `gesture.sheet.dismiss.fraction` (0 to 1). A view may name them in size and opacity props; ranges are enforced by the theme layer, not by Rubedo. Rubedo knows no token by name, so new ones need no Rubedo change.
- **Token props.** A node prop whose value is a token reference is `{ "token": "color.hue.3" }`; a bare string stays a literal. The validator checks the name is in the catalog and the kind fits the prop (colour for `color`, duration for `duration`). Unknown name: `view.unknown_token`. Wrong kind: `view.token_kind_mismatch`.
- **Interaction presets.** Any node may carry `motion`: `{ "token": "motion.interaction.send-button" }` to pick a preset, or the literal `"none"` to switch the node's motion off. The three level variants live in the motion document, not in the view, and the person's `client.motion_level` picks one at run time.
- **No literals.** A style prop that is a raw colour (`#rgb`, `#rrggbb`, `rgb(...)`), a raw duration (`"200ms"`) or a raw gesture threshold fails with `view.literal_style_value`. This is the validator half of doc 93's "scan views for literals" rule; the headless scan of built views stays.
- **Floors are not view concerns.** Text contrast, the reduced-motion substitution and the system back-gesture edge are enforced by the theme validator and the motion layer (doc 93), after the document loads. A view cannot lower them, because it can only name tokens.
- **Expressions.** The nine context identifiers of doc 41a section 3 are unchanged. Motion level and material mode are not Rubedo identifiers: what is shown must not depend on whether animation is on. (Open question 6 below.)
- **Tooling.** `RubedoAnalyzer.CompleteView` completes token names from the catalog, filtered by prop kind; the language server takes the catalog with `--tokens <file>` (a JSON list of `{ name, kind }`, like `--schema`); the docs generator lists the namespaces from the catalog it is given.

## 6. Actions

- Actions are a closed set: `navigate` (to a pane or GC), `invoke` (a named command from the module manifest), `openPane`, `setSetting` (a setting key the manifest declares).
- `invoke` runs through the same approval tier as the control it mirrors (doc 41 section 6, rule 3). A view cannot lower a tier.
- No action sends a raw wire call. Wire access stays in the module's declared capabilities.

### 6.1 Action arguments (PROPOSED, 2026-11-19)

Status: PROPOSED. Found while porting the RyuseiFuda schedule pane: a button inside a list row (pause or resume this trigger, open this chat) must say which row it acts on, and an action id alone cannot. This stays inside the closed set of section 6.

- A `button` may carry an optional `args` prop: a map of argument names to values. Each value is a literal or a Rubedo expression, resolved in the button's scope, so inside a list template the row's fields are available (`{ "id": { "expr": "id" } }`).
- Arguments are named, flat and scalar (text, number, bool). The validator rejects a nested value, a name that is not an identifier, more than 8 arguments, and an expression that does not compile against the item schema.
- Only `invoke`, `navigate`, `openPane` and `setSetting` accept arguments, and the action's target still comes from the document's `actions` entry, never from `args`. A view cannot change which command runs, only what it runs on, so no action becomes a raw wire call.
- The head receives the arguments with the target. `IViewActions` gains an overload that takes the argument map; the tier check of section 6 runs on the command before the arguments are read, and the module's command declares the argument names and types it accepts. An undeclared or mistyped argument fails the action closed and is reported through the error callback.
- Argument values are evaluated when the button is clicked, from the data the view was built with, not from a later update.

Open for the owner (cards only if the owner wants them): whether `args` should also be allowed on `input` change actions (the value is already passed there).

## 7. Accessibility and i18n (from day one, owner decision)

- Every `button`, `icon` and `input` needs a label: a localisation key or a literal. Validation fails without one.
- Reading order is the child order. Focus order follows it unless a `focusOrder` prop overrides it.
- Text uses localisation keys. Literals are allowed only for names that are not translated (product names, codes).
- Scaling: `text` sizes come from tokens, so the head's text scale (`textScale`, doc 41a section 3) applies everywhere.

## 8. Validation and tests

- The validator runs on the server and on every head. It checks: closed widget set, known action ids, declared data sources only, theme token names exist, labels present, Rubedo expressions compile against the view's typed context, and the node limits from doc 41 section 6.
- Golden corpus: each view has golden documents and expected validation results, shared by every head, the same way as the expression corpus (doc 41a section 8).
- Headless rendering tests per head (Avalonia.Headless on desktop, per CLAUDE.md) check that each widget maps to a control.

## 9. Questions for the owner (cards)

1. **Widget set.** Accept the v1 list in section 3, or change it. Adding a widget later is a version change.
   - **Answer (owner, 2026-11-15): ACCEPTED.** The 13 widgets in section 3, as listed.
2. **Data model.** Pull by binding over named sources (proposed), or push-only streams? Pull keeps views pure.
   - **Answer (owner, 2026-11-15): ACCEPTED.** Pull by binding over named sources.
3. **Actions.** Accept the four closed actions in section 6 (proposed), or add more.
   - **Answer (owner, 2026-11-15): ACCEPTED.** The four closed actions: navigate, invoke, openPane, setSetting.
4. **Lists.** Keyed list with one item template (proposed), or virtualised list with a separate item view?
   - **Answer (owner, 2026-11-15): ACCEPTED.** A keyed list with one item template.
5. **Authoring.** Views are shipped by modules and company builds only (proposed). Should people import view documents, under Tier 2 approval, as layouts are planned to?
   - **Answer (owner, 2026-11-15): ACCEPTED.** Views ship from modules and company builds only. There are no runtime imports.
6. **Motion level in expressions (PROPOSED, 2026-11-22).** Should `visibleWhen` and `enabledWhen` be able to read the person's motion level or material mode? Recommended: no. Visibility and enablement must not change with an animation setting; the nine identifiers stay as they are (section 5.1).

## 10. Work after the owner answers (accepted 2026-11-15, work started)

1. Widget and action schemas as C# records, with the validator (in the Rubedo module, pure).
2. Golden corpus for views, shared by heads.
3. Avalonia mapping of the widget set, with headless tests (Client.Core as adapter).
4. Port one pane (the GC feed) to a view document as the pilot.

## 11. Hosting views in a head (PROPOSED, 2026-11-20)

Status: PROPOSED. Needs the owner's accept or change before code. It answers one gap: sections 1 to 10 define the document and the mapper, but nothing says how a module's pane gets a view document and a view context. The RyuseiFuda panes need this to swap their hand-built panes for view documents.

**11.1 Who supplies what.**
- The head supplies the `ViewContext` (theme, the four actions, the localiser, the action-error sink) once at start-up, as a service in the pane module context. A module never builds its own context and never reaches the head's action implementations directly.
- The module supplies its view documents as embedded resources named `Views/<name>.view.json` (the document `name` field, dotted, must match the file name) and one data object per view that implements a small data interface (section 11.2).
- The shared host in `Bazaar.Client.Views` joins the two. It holds no feature code.

**11.2 Data.** A view reads only the sources it declares (section 4). The module hands the host an object that exposes each declared source as a typed value plus one change notice per source. The host re-evaluates bindings only on a notice. The module never pushes controls.

**11.3 Load.** At pane creation the host reads the embedded document, validates it with the Rubedo validator, checks every declared data source against the module manifest, then maps it. Any failure shows the section 1 placeholder for the whole pane with the validator's message and writes one audit entry. A document is never partly rendered when its root fails; a bad child node degrades to a placeholder for that node only (existing mapper rule).

**11.4 Layering.** Feature modules (`Bazaar.Client.<Name>`) may reference `Bazaar.Client.Views` and `Bazaar.Client.Theme` and nothing else new; `Bazaar.Client.Views` references no feature module. Architecture tests enforce both edges. Heads reference the modules and `Bazaar.Client.Views` and register the context service.

**11.5 Pane kinds.** A view-backed pane registers under the same pane kind as the hand-built one it replaces, so saved layouts keep working. A pane kind with no loadable view falls back as in section 5.

**11.6 Heads.** Desktop and Android use the Avalonia host. The terminal head (Calcinato) needs its own adapter against the same document and data interfaces; it is out of scope here.

**11.7 Open for the owner.**
1. Whether third-party modules may ship views later (this section only covers first-party, compiled-in modules, as doc 41b requires today).
2. Hot reload of view documents during development (default: off).
