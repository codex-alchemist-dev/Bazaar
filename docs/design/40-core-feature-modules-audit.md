# 40. Core feature modules: GC, Constellation, RyuseiFuda

Status: AUDIT (2026-11-09). Written from the code at this branch's base. Follows `MODULARITY-AUDIT.md` (2026-10-30) and `BAZAAR-COMPONENTS.md`. The owner's question: are GC, Constellation and RyuseiFuda just modules of Bazaar, and does the shape `Bazaar.Core` plus feature modules that slot into `Bazaar.Server` and each `Bazaar.Client.*` head exist?

## 1. Short answer

- **The kernel exists; the module boundaries do not yet.** `src/Bazaar.Server/Components/` has `IBazaarComponent`, `ComponentManifest`, `ComponentGraph` (validation and ordering) and `BazaarComponentRegistration` (enable/disable). `HostComponents.All` lists 16 components. GC, Constellation, Vault, Kanban and RyuseiFuda's schedule are registered through it.
- **Server side, the features are folders, not modules.** GC, Constellation and Vault are folders inside the one `Bazaar.Server` assembly. Only Kanban and RyuseiFuda sit in their own project, `Bazaar.Server.Components.Kanban`, and that project also holds RyuseiFuda's layer-2 code. Nothing stops a feature from using another feature's internals, because the assemblies are shared.
- **Client side, there is no module at all.** The panes for GC, Constellation, Kanban and the dashboard are registered by hand in `Bazaar.Client.Core/Panes/FirstPartyPanes.cs`, which has a `TODO(component)` for the missing client kernel. RyuseiFuda's client view model sits in `Bazaar.Client.Core/Ryusei/`.
- **So the answer to "Bazaar.Core plus Bazaar.GC etc. that slot in" is: yes, that is the target, and the kernel half is already built. The feature half is not.**

## 2. Current state

| Area | Server | Client | Manifest deps (declared) | Real deps (found) |
|---|---|---|---|---|
| GC (`gc`) | Folder `Bazaar.Server/Gc/` in core assembly | GC pane in `Client.Core` (`GcPaneViewModel`) | none | 13 files outside `Gc/` import it (Agents, Calendar, Org, Http, Souls, Kanban, Ryusei). Kanban and Ryusei use it. |
| Constellation (`constellation`) | Folder `Bazaar.Server/Constellation/` in core | `Client.Core/Constellation/` | none | Its Vault graph source reads `Vault.Data` types. Used by `Http/HttpSurfaceEndpoints.cs`. |
| Kanban (`kanban`) | Own project `Bazaar.Server.Components.Kanban` | Board pane in `Client.Core` | none | Uses GC (optional: `IGcLogWriter`, `IGcExistence`). Works without GC. |
| RyuseiFuda schedule (`ryusei`, formerly `ryusei-schedule`) | Project `Bazaar.RyuseiFuda` (split from the Kanban project in #384), namespace `Bazaar.RyuseiFuda.Orrery` (was `Bazaar.Server.Kanban.Ryusei`) | `Client.Core/Ryusei/RyuseiMicroPaneViewModel` | `kanban` (plus `gc` and `audit` after this PR) | Hard: `GcNotifyListener`, `ITransactionalAuditLog`. Optional: `IGcLogWriter`, `GcCreateService`. |

## 3. Gaps

1. **Undeclared hard dependencies.** RyuseiFuda resolves the GC listener and the audit log but declared only `kanban`. This PR declares `gc` and `audit` and adds a test (`RyuseiScheduleDependencyTests`). The kernel now refuses to start RyuseiFuda without them.
2. **The manifest has no optional dependencies.** Kanban and RyuseiFuda use GC only when it is present. Declaring GC as `DependsOn` would make Kanban fail when GC is disabled, which is wrong. Declaring nothing hides the link. The model needs an `OptionalDependsOn` (or a `soft` flag). That is a schema choice, so it needs an owner answer before code changes.
3. **Constellation's dependency on Vault is a type reference, not a declared dependency.** It only runs when `Constellation:VaultId` is set, so it is optional too. Same fix as item 2.
4. **GC is effectively core, but it is not declared as core.** Thirteen files outside `Gc/` depend on it. No consumer declares `gc`. Calendar, Org, Agents and Http are not components yet, so the kernel cannot see their edge.
5. **Single assembly for the server features.** GC, Constellation and Vault are folders in `Bazaar.Server`, so no project boundary is enforced. The architecture tests check only Contracts and Client.Core layering, not component boundaries. Rules 1 and 2 of `BAZAAR-COMPONENTS.md` section 6 are not yet tests.
6. **RyuseiFuda is inside the Kanban project.** Layer 1 (Kanban) and layer 2 (RyuseiFuda) share one assembly. The `kanban` dependency is therefore not a project boundary.
7. **Migration namespace mismatch (DECIDED 2026-11-09: rename the id to `ryusei`; applied as code, see PR #360 follow-up).** The `ryusei-schedule` component maps to namespace `ryusei_schedule`, but its SQL folder is `ryusei`. The runner still applies it (after the component namespaces, by name), so nothing breaks today, but the ordering and ownership are not what the manifest says. `DECISIONS-LOG.md` already records this as an owner choice: rename the component id or the folder. Renaming the folder would change applied history, so the component id is the safer side. Owner to choose.
8. **GC, Constellation and Vault tables sit in `common/`**, which is frozen (see `BAZAAR-COMPONENTS.md` section 5). Their components own no migration namespace yet. This is correct under the freeze, and moving it would re-run applied files, so it stays until a wave decides otherwise.
9. **Client panes are hard-wired.** `FirstPartyPanes.CreateRegistry` names each pane. Heads cannot add or drop a feature pane without editing Client.Core.
10. **Version format.** Manifests use SemVer (`0.1.0`). The owner's rule (2026-11-08) is `vAA.BB.CC-commit` for internal modules, with SemVer under the hood. The manifest keeps SemVer, and the display derives the other form. No code change is needed, but the rule belongs in `BAZAAR-COMPONENTS.md`.

## 4. Target layout

The owner's picture is right. The target, for each feature `<F>`:

```
Bazaar.Core                       kernel: IBazaarComponent, manifests, graph, migrations, DI, wire base
Server (per feature <F>):
  Bazaar.<F>.Abstractions         contracts other modules may reference (no implementation)
  Bazaar.<F>                      implementation; registers via one AddBazaar<F>() / IBazaarComponent
  migrations                      Data/Sql/<f>/NNNN_*.sql, its own namespace
Client (per feature <F>):
  Bazaar.Client.<F>               pane views and view models, referencing Bazaar.Client.Core only
Heads (Windows, Linux, Android, Terminal) compose the client modules they ship.
```

Rules that already exist and stay: a module references other modules only through their abstractions; `common/` is frozen; `Bazaar.Contracts` keeps the wire types, namespaced per feature; client modules never reference a head.

Applied to the three features:

- **GC:** `Bazaar.Gc.Abstractions` (log reader and writer contracts, access policy), `Bazaar.Gc` (the log). Its consumers (Kanban, RyuseiFuda, Calendar, Org, Agents) reference the abstractions only. Biggest payoff, so first.
- **Constellation:** `Bazaar.Constellation` with its Vault graph source going through a Vault abstractions project. Its client pane goes in `Bazaar.Client.Constellation`.
- **RyuseiFuda:** `Bazaar.RyuseiFuda` split out of the Kanban project. It depends on `Bazaar.Kanban.Abstractions` and `Bazaar.Gc.Abstractions`. Its client pane goes in `Bazaar.Client.RyuseiFuda`.

## 4a. Renames (planned, small PRs)

| Now | Target | Notes |
|---|---|---|
| `ryusei` component (layer 2 code in `Bazaar.Server.Components.Kanban`) | `Bazaar.RyuseiFuda` with `RyuseiFuda.Orrery` and `RyuseiFuda.Workday`/`Roadmap` parts | Module split done (#384). The scheduling layer's namespace is `Bazaar.RyuseiFuda.Orrery`; the `Ryusei*` type names, the `ryusei` id and the `Bazaar:Ryusei` settings section stay. |
| Layer 1 kanban (`kanban`) | Under `RyuseiFuda.Workday` or kept as `Bazaar.Kanban`, owner to confirm | Id and stored settings keys stay (doc 38 R4 defers renaming). |
| No connectivity code yet | `Bazaar.Conduit` with `Conduit.Cloudflared`, `Conduit.P2P` | New code only; nothing to rename. |

## 5. Plan, in order

Each step is a small PR with tests, per `CLAUDE.md`.

1. **Declare real hard deps** (this PR): RyuseiFuda declares `gc` and `audit`. Test added.
2. **Optional dependencies** in the manifest. Needs an owner answer on the field name and semantics (item 2).
3. **Architecture rule for component boundaries**, starting from a baseline that may only shrink (rule 2, `BAZAAR-COMPONENTS.md` section 6). The baseline lists today's violations.
4. **GC abstractions project**, and move its consumers onto it.
5. **Split RyuseiFuda** out of the Kanban project.
6. **Constellation and Vault** projects and abstractions.
7. **Client modules** for GC, Constellation and RyuseiFuda, with the pane registry taking modules instead of named panes. Heads compose them. Progress: the registry composes `IPaneModule`s (#373); a module also registers its view (`PaneRegistry.RegisterView`) and can read a head-supplied service from `PaneModuleContext.Get<T>()`, so Client.Core no longer needs a feature's view model type to render it. Next: move each feature's pane code into `Bazaar.Client.<F>`.

Not now: a runtime plugin loader, a module framework with its own container, or splitting every folder before step 3 exists.

## 6. Decisions

Recorded 2026-11-09 by the coordinator, relaying the owner's choices:

1. **Names:** `Bazaar.Core` (kernel), `Bazaar.GC`, `Bazaar.Constellation`, `Bazaar.RyuseiFuda`, `Bazaar.Conduit`. Sub-parts are `Module.Part`. Final owner picks (2026-11-09):
   - RyuseiFuda parts: `RyuseiFuda.Roadmap` (objective layer), `RyuseiFuda.Workday` (macro layer, formerly "Macro"), `RyuseiFuda.Orrery` (micro layer, formerly "Micro"). Spelling is `RyuseiFuda` throughout; the owner's variants were typos.
   - Connectivity is a named feature, `Bazaar.Conduit`, with parts such as `Conduit.Cloudflared` and `Conduit.P2P`.
   - Until these are renamed in code, the existing `ryusei` component id and `Ryusei*` identifiers stay. Renames go in small parallel PRs (section 7).
2. **Optional dependencies:** a `Soft` flag on each dependency (`SoftDependsOn`), additive, with hard as the default.
3. **RyuseiFuda id:** renamed to `ryusei`, which matches its SQL folder. The old enablement key is still read (`BazaarComponentRegistration.LegacyIds`). Migration history is unchanged because the namespace was already `ryusei`.

The earlier open questions are kept below for reference.

## 6a. Open questions (superseded where decided above)

1. **Project naming.** Options: (a) `Bazaar.Gc`, `Bazaar.Constellation`, `Bazaar.RyuseiFuda` (matches the namespaces today); (b) `Bazaar.GC` (matches the owner's spoken form); (c) `Bazaar.Server.Components.<Name>` (matches the existing Kanban project). The kernel name `Bazaar.Core` is also new. Owner picks.
2. **Optional dependencies** (item 2): field name `OptionalDependsOn`, or a `Soft` flag on each dependency. Owner or doc owner picks.
3. **RyuseiFuda migration name** (item 7): rename the component id to `ryusei` (no stored data uses the id, so this is the cheaper side), or the folder to `ryusei_schedule`. Recommend the id.
4. **Scope of wave 2.** Whether Calendar, Org and Agents become components before GC is split, since they are the GC consumers.
