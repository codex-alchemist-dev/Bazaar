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
| `icon` | no | Icon from a closed icon set |
| `button` | no | Calls a named action |
| `input` | no | Text input bound to a value (write goes through an action) |
| `list` | template | Keyed list over a data source, one item template |
| `tabs` | yes | Named tabs |
| `badge` | no | Count or status chip |
| `divider` | no | Separator |
| `placeholder` | no | Failure card (doc 41 section 6, rule 7) |

**Text props.** `text` and `badge` take a `text` prop: a literal, or an expression of any type that renders as text (a Number or Text binding is shown as written). A bare identifier reads that field of the scope; any other expression is evaluated by Rubedo. `badge` also takes a `variant` from the theme's closed variants. `button` and `icon` take `label` (the accessible name); `icon` also takes `name` from the closed icon set. `visibleWhen` and `enabledWhen` are Rubedo bool expressions (or literal bools) on any node: the first shows or hides it, the second enables or disables it, both evaluated against the node's scope, so a list item's template sees its own fields. A value that is not a bool fails that node closed.

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
