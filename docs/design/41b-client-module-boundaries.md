# Client module boundaries: where the GUI code belongs

Status: PROPOSED (2026-11-13). Draft for the owner's card on moving code out of `Bazaar.Client.Core`. Hold the move until the owner answers. Nothing here changes code.
Rule (owner, 2026-11-13): `Bazaar.Client.Core` is an adapter and holds no functionality. Every function is a module.
Depends on: 41-client-gui-customization-and-dsl.md (section 8 module contract, decision 10 names), 41a-rubedo-dsl-design.md (DECIDED).

## 1. What is in Client.Core today

| Code now in `Bazaar.Client.Core` | Source | What it is |
|---|---|---|
| `Layout/LayoutDocument`, `LayoutValidator`, `LayoutResolver`, `LayoutScopeMerger`, `LayoutTree`, `LayoutSelection` | #512, #520 | Layout documents, rules and placement (pure) |
| `Tour/TourDefinition`, `TourSession`, `ITourStateStore` | #512 | First-run tour framework (pure) |
| `Modules/ModuleManifest`, `ModuleManifestValidator`, `ClientModuleBuilder`, `IClientModule` | #521 | Module contract: the manifest and its checks (pure) |

Code that was already in Client.Core before this work (theme, commands, chat, panes, Kanban, Constellation, Pairing, Vault and others) is out of scope here and needs its own map.

## 2. Proposed split

| Target | Holds | Why |
|---|---|---|
| `Bazaar.Client.Layout` (module) | Layout documents, validator, resolver, scope merger, tree, selection | Layout is a feature with its own document kind and tests. It is a module, not core. |
| `Bazaar.Client.Tour` (module) | Tour definition, session, progress store interface | First-run tour is a feature (doc 41 onboarding). The tour's store is a module capability, not core. |
| `Bazaar.Rubedo` (module) | Rubedo engine (pure library) | Owner decision (2026-11-13). Shared by the server and every head. |
| `Bazaar.Contracts` (existing shared project) | `ModuleManifest`, `ModuleManifestValidator`, `ModuleIsolation` | The manifest is a shared contract. Every host and every module references it, so it cannot sit in one module. Contracts is already the shared project for client and server messages. |
| `Bazaar.Client.Core` (adapter) | Host that loads modules, the manifest check at load, the pane-registry adapter, the Avalonia shell | Holds no features. `ClientModuleBuilder` and `IClientModule` are host-side plumbing and stay here. |

## 3. Dependencies after the move

- `Bazaar.Client.Layout` → `Bazaar.Contracts`, `Bazaar.Rubedo` (for `visibleWhen`, when wired).
- `Bazaar.Client.Tour` → `Bazaar.Contracts`.
- `Bazaar.Rubedo` → nothing (pure library, no Avalonia, no I/O).
- `Bazaar.Client.Core` → `Bazaar.Contracts`, and loads modules by manifest. It does not reference any feature module directly; heads wire modules in.

## 4. Open questions for the owner

1. Manifest location: `Bazaar.Contracts` (recommended, shared by host and modules) or a new `Bazaar.ModuleContract` library.
2. Whether `ClientModuleBuilder` stays in Client.Core (recommended: host plumbing) or moves to Contracts with the manifest.
3. Module names: `Bazaar.Client.Layout` and `Bazaar.Client.Tour` follow decision 10. Confirm or rename.
4. Whether the first-party layout module is loaded like any third-party module (out-of-process, per decision 2 for non-shipped modules) or compiled in (shipped set). Recommended: compiled in as a shipped module, with the same manifest checks.

## 5. Move plan (after the owner answers)

1. Create the target projects, each with its own tests. Move files with `git mv` so history follows.
2. Move the manifest types to Bazaar.Contracts, keeping the public names so callers do not change.
3. Update references and run the full Client.Core and new module test suites.
4. One PR per module, so each move is reviewable and small.
