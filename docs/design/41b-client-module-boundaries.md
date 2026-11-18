# Client module boundaries: where the GUI code belongs

Status: PROPOSED (2026-11-13). Draft for the owner's card on moving code out of `Bazaar.Client.Core`. Hold the move until the owner answers. Nothing here changes code.
Rule (owner, 2026-11-13): `Bazaar.Client.Core` is an adapter and holds no functionality. Every function is a module.
Depends on: 41-client-gui-customization-and-dsl.md (section 8 module contract, decision 10 names), 41a-rubedo-dsl-design.md (DECIDED).

## 1. What is in Client.Core today

| Code now in `Bazaar.Client.Core` | Source | What it is |
|---|---|---|
| `Bazaar.Client.Layout` (moved from `Layout/` in Client.Core; namespace `Bazaar.Client.Layout`): `LayoutDocument`, `LayoutValidator`, `LayoutResolver`, `LayoutScopeMerger`, `LayoutTree`, `LayoutSelection` | #512, #520 | Layout documents, rules and placement (pure) |
| `Tour/TourDefinition`, `TourSession`, `ITourStateStore` | #512 | First-run tour framework (pure) |
| `Modules/ModuleManifest`, `ModuleManifestValidator`, `ClientModuleBuilder`, `IClientModule` | #521 | Module contract: the manifest and its checks (pure) |

Code that was already in Client.Core before this work (theme, commands, chat, panes, Kanban, Constellation, Pairing, Vault and others) is out of scope here and needs its own map.

## 2. Proposed split

| Target | Holds | Why |
|---|---|---|
| `Bazaar.Client.Layout` (module) | Layout documents, validator, resolver, scope merger, tree, selection | Layout is a feature with its own document kind and tests. It is a module, not core. |
| `Bazaar.Michi` (module) | Tour definition, session, progress store interface | First-run tour is a feature (doc 41 onboarding). The tour's store is a module capability, not core. |
| `Bazaar.Rubedo` (module) | Rubedo engine (pure library) | Owner decision (2026-11-13). Shared by the server and every head. |
| `Bazaar.Contracts` (existing shared project) | `ModuleManifest`, `ModuleManifestValidator`, `ModuleIsolation` | The manifest is a shared contract. Every host and every module references it, so it cannot sit in one module. Contracts is already the shared project for client and server messages. |
| `Bazaar.Client.Core` (adapter) | Host that loads modules, the manifest check at load, the pane-registry adapter, the Avalonia shell | Holds no features. `ClientModuleBuilder` and `IClientModule` are host-side plumbing and stay here. |

## 2a. Server and client halves (owner, 2026-11-16)

Each feature with a client surface has a separate server module and client module (BAZAAR-COMPONENTS §2.1). The pairing table is the reference for where each half lives. The renderer row is a standalone shared module, not a feature half. It is internal today and will become an externally imported dependency later.

| Feature | Server module | Client module | Notes |
|---|---|---|---|
| Constellation | `Bazaar.Server.Constellation` | `Bazaar.Client.Constellation` with `Bazaar.Client.Constellation.Abstractions` | Client panes and views. The abstractions hold the tile-source contract. |
| Constellation renderer (shared renderer) | none | `Bazaar.Client.Constellation.Renderer` (name as the owner gave it; standalone and shared, see note) | Not part of Constellation, despite the name, which is the owner's and may be renamed. A general Vulkan renderer with a Skia fallback, used by Constellation and by other modules such as RyuseiFuda, and later other CDAX projects. Future Dep: it will become an externally imported dependency, with a Bazaar-free core and an adapter in its own repo. Other modules depend on it only through its contract. |
| GC | `Bazaar.Server.GC` | `Bazaar.Client.Gc` | GC pane. Depends on `Bazaar.Client.Chat.Abstractions` for the chat contract. |
| Chat | none (chat rides the GC wire) | `Bazaar.Client.Chat` with `Bazaar.Client.Chat.Abstractions` | Chat view, timeline and connection. The GC pane binds it only through the abstractions. |
| Souls | `Bazaar.Server.Souls` | `Bazaar.Client.Souls` | Soul panes and binding views. |
| Dashboard | none | `Bazaar.Client.Dashboard` | Start-screen dashboard pane. Reads Constellation tiles only through `Bazaar.Client.Constellation.Abstractions`. |
| Git | `Bazaar.Server.Git` | `Bazaar.Client.GitGui` (with `.Github`, `.Gitlab`) | Per doc 41 §4.14. |
| Kanban (RyuseiFuda) | `Bazaar.Server.RyuseiFuda` | `Bazaar.Client.RyuseiFuda` | Dependency board views. |
| Vault | `Bazaar.Server.Vault` | `Bazaar.Client.Vault` | Vault search pane. |

Server and client modules share contract types only through `Bazaar.Contracts` and each module's `.Abstractions` project. The client halves in this table (Constellation, GC, Chat, Souls, Dashboard, Vault) exist as separate projects, built on `Bazaar.Client.Abstractions` (wire, server and pane contracts) and `Bazaar.Client.Panes.Abstractions` (`IPaneModule`, `PaneRegistry`, `IPaneViewFactory`). Server halves and the Git and RyuseiFuda rows follow the same pattern as each feature splits. A client feature module never references `Bazaar.Client.Core`; an architecture test enforces this. Names follow the owner's pattern (`Bazaar.Server.<Name>` and `Bazaar.Client.<Name>`). Doc 40 still lists the pre-split names (`Bazaar.GC`, `Bazaar.Constellation`, `Bazaar.RyuseiFuda`, `Bazaar.Vault`); when each feature splits, its server half takes the `Server` prefix. Doc 40 is not edited here, and its names need updating in the split PRs.

## 3. Dependencies after the move

- `Bazaar.Client.Layout` → `Bazaar.Contracts`, `Bazaar.Rubedo` (for `visibleWhen`, when wired).
- `Bazaar.Michi` → `Bazaar.Contracts`.
- `Bazaar.Rubedo` → nothing (pure library, no Avalonia, no I/O).
- `Bazaar.Client.Core` → `Bazaar.Contracts`, and loads modules by manifest. It does not reference any feature module directly; heads wire modules in.

## 4. Open questions for the owner

1. Manifest location: decided by the coordinator (2026-11-13). The manifest types go in `Bazaar.Contracts`, and the builder moves with them.
2. Module names (owner, 2026-11-13): the layout module is `Bazaar.Client.Layout`, and the first-run tour module is Michi (道), so `Bazaar.Michi`.

Loading mode (resolved 2026-11-13, owner correction): the server builds each company's client with its first-party modules compiled in (per-company builds, docs 37 and 41). Layout and tour ship compiled in. Out-of-process loading (decision 2) applies only to sandboxed external modules, under the module trust tiers. It is not a question for first-party modules.

## 5. Move plan (after the owner answers)

1. Create the target projects, each with its own tests. Move files with `git mv` so history follows.
2. Move the manifest types to Bazaar.Contracts, keeping the public names so callers do not change.
3. Update references and run the full Client.Core and new module test suites.
4. One PR per module, so each move is reviewable and small.
