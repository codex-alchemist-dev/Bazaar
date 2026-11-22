# Rubedo tooling: analysis, language server, docs generator, playground

Status: PROPOSED (2026-11-20). Written under the owner's standing rule to build the full DSL in v1 with no temporary shortcuts (41a, DECIDED) and the coordinator's instruction to design the tooling as its own modules, docs first. Nothing here changes the language (41a) or the view layer (41c). Every item below is a tool around the one engine; none adds a second evaluator.
Depends on: 41a-rubedo-dsl-design.md (grammar, schema, function table, limits), 41c-rubedo-view-declarations.md (view documents), 41b-client-module-boundaries.md (module sides), 40-core-feature-modules-audit.md (module list).

## 1. Goals and non-goals

Goals: people and agents who write Rubedo sources and view documents get completions, hover help, live diagnostics, a canonical format, generated reference docs and a place to try expressions, all driven by the same schema and function table the engine uses, so the tools can never drift from the language.

Non-goals: no new language feature, no second parser, no execution of anything but `RubedoEngine`, no network access, no write access to a document without the caller asking for a text edit.

## 2. Modules

| Module | Side | Holds | References |
|---|---|---|---|
| `Bazaar.Rubedo` (existing) | shared, pure | Engine, `RubedoFormatter` (section 3). | nothing |
| `Bazaar.Rubedo.Analysis` | shared, pure | Transport-neutral analysis: diagnostics with ranges, completions, hover, signature help, document symbols for view documents, format edits. | `Bazaar.Rubedo` |
| `Bazaar.Rubedo.LanguageServer` | server-side tool host | LSP 3.17 over stdio and a loopback socket; maps LSP requests to `Bazaar.Rubedo.Analysis`. Runs as a separate tool process, never inside Bazaar.Server. | `Bazaar.Rubedo.Analysis` |
| `Bazaar.Rubedo.Docs` | shared, pure | Reference generator: Markdown pages from `RubedoSchema`, the function table, the widget and action sets of 41c and the diagnostic code list. | `Bazaar.Rubedo`, `Bazaar.Rubedo.Analysis` |
| `Bazaar.Client.Rubedo.Playground` | client | A pane for trying an expression or a view document against a chosen context, with live diagnostics and result. Loads as an ordinary client module; no code in Client.Core. | `Bazaar.Rubedo.Analysis` |

No module references `Bazaar.Server.csproj`. The language server and docs generator are developer and operator tools and ship as separate executables; the playground ships as a client module a company may include or leave out.

## 3. Formatter

`RubedoFormatter.Format(source, schema?)` returns the canonical source or the compile diagnostics, and never throws. Properties, each covered by tests: idempotent; semantics-preserving against the golden corpus (41a section 8); output within the source limit or a diagnostic; parentheses the grammar requires (a relation under `==` or `!=`) are kept.

## 4. Analysis

- **Diagnostics:** the engine's diagnostics with start and end offsets and a stable code (41a section 7), mapped to line and column by the caller.
- **Completions:** identifiers from the schema (with type), function names with signatures from the closed table, keywords `true` and `false`. Context-aware: inside a call, argument position decides which types rank first. View documents also complete widget names, prop names, action ids and theme tokens (41c section 8).
- **Hover:** identifier type and meaning, function signature and description, the inferred type of the expression under the cursor.
- **Signature help:** active parameter inside a call.
- **Limits:** every request is bounded by the language limits (41a section 6) plus a 64 KiB document cap; over-limit input returns diagnostics, never an exception.
- **Determinism:** same document and schema give the same result on every machine. No clock, no randomness.

## 5. Language server

- Scope: documents of two languages, `rubedo` (a bare expression) and `rubedo-view` (a JSON view document, with expressions inside `{ "expr": "..." }` props).
- Capabilities: diagnostics (push), completion, hover, signature help, formatting, document symbols. No code actions that change meaning in v1.
- Transport: stdio by default; a loopback-only socket with a random port printed on start. No remote bind.
- Security: read-only over the text the client sends; it never reads other files, never opens the network, and never executes view actions.
- Schema: the language server uses `RubedoSchema.Standard` (41a section 3) unless started with `--schema <file>`, a JSON object mapping identifier names to `bool`, `number` or `text`; an invalid file stops startup. Hosts with their own identifiers (a module's view data) pass their schema this way. PROPOSED.
- View symbols: one symbol per widget node under `root` and `children`, named by the `widget` value (kind Object). PROPOSED.
- Packaging: a self-contained tool built from the same source as the engine, versioned with it (`dslVersion`). A server whose `dslVersion` is unknown to the editor still works; the response names the version.

## 6. Docs generator

- One command writes a reference folder: identifiers, functions, operators and precedence, limits, widgets, actions, diagnostic codes, with a worked example for each function taken from the golden corpus.
- Output is deterministic Markdown with stable anchors, so a doc diff in a PR shows real language changes only.
- A CI check regenerates the reference and fails when the committed copy differs. The function table is the single source of truth.
- IMPLEMENTED: `Bazaar.Rubedo.Docs` (`RubedoReferenceGenerator.Generate()`), the `bazaar-rubedo-docs` tool (`--out <dir>`, `--check <dir>`, exit 1 on drift) and the committed copy in `docs/reference/rubedo/`. `Bazaar.Rubedo.Docs.Tests` is the drift check, and it also scans the engine source for diagnostic codes missing from the catalog.

## 7. Playground

- Edit a source or a view document; pick or edit a context (the nine identifiers of 41a section 3 as typed controls); see the result or the diagnostics as you type.
- Runs in-process on the client with the shared engine; no server call.
- Accessible and keyboard-first like every pane: labels, focus order, text scale, localisation keys (41c section 7). An `Avalonia.Headless` test per control.

## 8. Order of work

1. `Bazaar.Rubedo`: `RubedoFormatter` and tests.
2. `Bazaar.Rubedo.Analysis` with tests (diagnostic ranges, completions, hover, signature help).
3. `Bazaar.Rubedo.LanguageServer` over the analysis, with protocol-level tests that talk LSP over in-memory streams.
4. `Bazaar.Rubedo.Docs` with the CI drift check.
5. `Bazaar.Client.Rubedo.Playground` with headless tests.
Each step lands as its own PR with this doc section named in the body.

## 9. Open questions

1. (Contract gap found while building) `ModuleManifest` and `ClientModuleHost` have no optional or off-by-default flag; the playground carries `EnabledByDefault = false` as a constant and is in no head's module list yet. A real flag belongs in the module-loading design (doc 41 section 8). Whether the playground should be offered in company builds by default or only through the module list (proposed: module list, off by default).
2. Whether the language server is also exposed to agents through a tool endpoint (proposed: no; agents call the analysis library through their own sandbox tooling).
3. Editor packages (VS Code extension, Monaco client). Out of scope here; the Monaco hosting spike decides.
