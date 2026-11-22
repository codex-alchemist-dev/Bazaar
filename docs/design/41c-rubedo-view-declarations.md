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
| `badge` | no | Count or status chip |
| `divider` | no | Separator |
| `placeholder` | no | Failure card (doc 41 section 6, rule 7) |

**Dynamic values.** `text` and `badge` take a `value` prop: a literal, or an expression of any type that renders as text (a Number or Text binding is shown as written). `label` stays a localisation key or literal for translated strings, so a value is never looked up as a key. The validator checks every expression prop the same way, so a bad `value` expression reports like any other.

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

## 10. Work after the owner answers (accepted 2026-11-15, work started)

1. Widget and action schemas as C# records, with the validator (in the Rubedo module, pure).
2. Golden corpus for views, shared by heads.
3. Avalonia mapping of the widget set, with headless tests (Client.Core as adapter).
4. Port one pane (the GC feed) to a view document as the pilot.
