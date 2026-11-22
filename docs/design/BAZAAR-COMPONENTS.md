# Bazaar Components

Status: PROPOSED (owner direction 2026-10-30: "make EVERYTHING modular. make EVERYTHING a Bazaar Component rather than hard coded"). Builds on `docs/design/MODULARITY-AUDIT.md`, which records the gap. This document is the model. Doc 03 (composition root), doc 05 (gateway), doc 20 (engine adapters) and doc 27 (client panes) are amended to this model when it is DECIDED.

## 1. Scope

Every capability the server or a client offers is a **Bazaar Component**, including base-app functions: GC log, Kanban, Vault, agents, gateway, settings, memory, Constellation, Interrupts, Calendar, WorkLife, Council, Skimmer, Org, Souls, the FreeLLMAPI provider and sidecar, Postgres management, the Identity Service, and client panes. Nothing is hard-coded into the host except the **kernel**: the component runtime, configuration binding, the database connection, the migration runner, the wire transport and the status listener. The kernel is small and is itself the first component (see section 9).

Terms used below:

- **Component**: a unit with a manifest, an entry point that registers its services, its own data (migrations), and a contract surface other components may use.
- **Contract**: the public types another component may reference. A contract lives in a component's `*.Abstractions` project (or in `Bazaar.Contracts` for wire types). Contracts never reference implementation code.
- **Kernel**: the host runtime. It loads components, orders them, runs their registration and migrations, and enforces the rules in section 6.

## 2. The manifest

Every component declares one manifest. In code (first-party) it is a static `ComponentManifest` on the component type. For a third-party component (section 8) the same fields come from a file, `bazaar-component.json`.

| Field | Meaning |
|---|---|
| `id` | Lowercase kebab-case, unique per install, stable across versions (for example `kanban`, `freellmapi`, `postgres-managed`). |
| `version` | SemVer of the component. Its contracts follow SemVer. |
| `kind` | `kernel`, `feature`, `provider`, `adapter`, `client-pane`. |
| `dependsOn` | Component ids it needs. Each entry may carry a version range. |
| `provides` | Contract names it implements, for example `provider:search`, `gateway:adapter:openai_chat`. |
| `requires` | Host capabilities it needs, for example `database`, `secrets`, `service-host`. Kernel-provided only. |
| `migrations` | Its migration namespace (section 5). |
| `settings` | Settings keys it reads, with safe defaults (doc 03 configurability rule). |
| `trust` | `first-party` (compiled in) or `third-party` (section 8). Default for first-party components. |
| `process` | `in-process` (default) or `out-of-process` (section 7). |
| `enabledByDefault` | PROPOSED. `true` (default) or `false`. The state when the operator has set no `Bazaar:Components:<id>:Enabled` value (section 3). A `false` component is optional: shipped in the build, off until switched on. Not allowed on `kind: kernel` or on a component another default-on component depends on. |

### 2.1 Module sides (owner, 2026-11-16)

A feature that has a server part and a client part is **two modules**, never one module with both halves. The server half is `Bazaar.Server.<Name>` and the client half is `Bazaar.Client.<Name>`, for example `Bazaar.Server.Constellation` and `Bazaar.Client.Constellation`. Each half has its own manifest, version, `kind` and migrations, and its own `Abstractions` project where another module needs it. The halves talk only through public contracts in `Bazaar.Contracts` (wire messages) or in the module's `.Abstractions` project, never by referencing each other's internals.

- A client module may depend on server contracts (wire types). It never depends on a server module's implementation.
- A feature with no client surface has only the server module, and the reverse holds for a client-only feature.
- A client module may depend on a shared client-side module that is not part of any one feature. The Constellation renderer (`Bazaar.Client.Constellation.Renderer`, the Vulkan renderer with a Skia fallback) is the first case. It is a standalone, shared module that other modules depend on through its contract (for example RyuseiFuda, and later other CDAX projects). It is not a sub-part of Constellation. It is internal today and will later become an externally imported dependency, with a Bazaar-free core plus an adapter in its own repo. Its manifest identity does not change when that happens.

The manifest is data. The kernel reads it before it runs any component code, so a bad graph fails at startup with a clear message.

## 3. Entry point and lifecycle

A component implements one interface:

```csharp
public interface IBazaarComponent
{
    ComponentManifest Manifest { get; }
    void AddServices(IServiceCollection services, IConfiguration configuration);
}
```

Lifecycle, in order, for every enabled component:

1. **Discover.** First-party components are listed explicitly in the host composition (no assembly scanning, which keeps Native AOT open, doc 03). Third-party components are found in the component folder (section 8).
2. **Validate the graph.** Unique ids, no unknown or cyclic dependencies, every `requires` satisfied by the kernel or a dependency, every `provides` unique per role. Failure stops startup with the failing ids named.
3. **Order.** Topological order by `dependsOn`, ties broken by `id` so the order is stable.
4. **Register.** Each component's `AddServices` runs in that order. A component may only resolve services from the kernel or from components it depends on.
5. **Migrate.** The migration runner applies each component's migrations in dependency order (section 5).
6. **Start.** Hosted services run. A component's `BackgroundService`s start through the normal host.
7. **Stop.** Reverse order.

Enablement comes from settings (`Bazaar:Components:<id>:Enabled`). A disabled component is not registered and its migrations are not applied, but its contracts are still validated so that a dependent that needs it fails at startup, not at first use. Core components cannot be disabled.

**Optional components (PROPOSED, 2026-11-22).** When no `Enabled` setting exists for a component, the host uses the manifest's `enabledByDefault` (default `true`). A component with `enabledByDefault: false` is optional: it is not registered, its migrations are not applied and its panes and commands do not appear until the operator (or, for a client module, the company's module list) switches it on. An explicit setting always wins over the manifest, in either direction. The manifest validator rejects `enabledByDefault: false` on a kernel component and on any component that a default-on component lists in `dependsOn` as a hard edge, because the host would otherwise fail to start with the defaults. The same field is read by the client module host (doc 41b, `ClientModuleHost.Load`) so that the Rubedo playground (doc 41d section 7) ships in the build but stays off until a company includes it. Docs-only change; the validator, host and `ClientModuleHost` follow in code PRs.

## 4. Contracts and dependencies

- A component references another component only through its contract assembly (`<Name>.Abstractions`) or through `Bazaar.Contracts`. A reference to another component's implementation project is a rule violation (section 6).
- Cross-component calls go through interfaces resolved from DI. A component never constructs another component's concrete class.
- Wire messages (client and server) stay in `Bazaar.Contracts` with the JSON codec (doc 02). A component that adds a wire message adds its contract types there, namespaced by component.
- Providers and adapters are a `provides` role. A role has one selected implementation, chosen by settings with the existing `auto` or explicit rule (doc 03). Adapters are many: each registers under its wire family name (for example `openai_chat`) and the gateway looks them up by name.

### 4.1 One dependency graph for Deps and written modules (owner, 2026-11-16, PLANNED)

Dependency modules (doc 42, tagged `Deps`) and written modules (first-party and third-party components) are one graph. Any node may depend on any other node, in either direction. There is no separate dependency manager and no one-way rule from modules to deps.

- **Hard edges** (`dependsOn`, and `requires` for kernel capabilities) mean the dependent cannot work without the target. Hard edges form a directed acyclic graph. The kernel checks this at install and at start, and names the cycle's ids when it fails. Hard edges set start order.
- **Soft edges** (`softDependsOn`) mean a feature works better with the target. Soft edges may form cycles, for example A and B each softly depend on each other while both hard-depend on C. Soft edges never set start order. A missing or disabled soft target degrades only the features that name it. The rest of the module still runs.
- Resolution happens from metadata before anything loads. The kernel reads all manifests, validates the hard graph, then computes start order from hard edges alone. Soft edges are wired after start, from the readiness of each target.
- Kodo readiness gates (doc 46 §10) follow hard edges. A failed hard target opens the readiness gate of every node that hard-depends on it. Soft targets raise an amber state, not a red one.
- Built: `ComponentGraph.Order` (start order from hard edges, cycles refused) and `ComponentGraph.Impact` (for one failed node, the transitive hard dependents to block and the soft dependents to mark amber). Not built: the `dependency` kind (doc 42 Q8 is open), `requires`, version ranges, and wiring `Impact` to the Kodo gates.

### 4.2 Principal: who did it (owner, 2026-11-19)

Every record of who acted uses one type, `Principal`, defined in `Cdax.Core` (the standalone Core, with no package references) (it carries no feature): a `kind` and an `id`. Kinds are `agent`, `user`, `service` and `system`. Its canonical text form is `kind:id` (for example `user:dom`, `agent:charles`, `service:kek`, `system:setup`). Audit entries, setting changes, module and install decisions, secret scopes and callers, approvals and telemetry consent all carry a `Principal`, so a person's change is recorded the same way as an agent's. An agent-only field remains only where the thing really is per-agent (for example an agent's own model usage or Kanban assignee). The audit log keeps its single `actor` text column and its hash chain, now written as the canonical `kind:id`, so no migration is needed. Existing bare values (`kek`, `setup:plan`) are read as `service:` or `system:` by a one-time mapping.

## 5. Data ownership: migrations per component

- Each component owns a migration namespace: `src/<Component>/Data/Sql/<component-id>/NNNN_name.sql`. Numbers are local to the namespace.
- The migration history table is keyed by `(component_id, version)`. The runner applies namespaces in dependency order and records each file once.
- Components never write another component's tables. A cross-component read goes through the owner's contract. Foreign keys to another component's table are allowed only to a table the dependency declares in its contract.
- The existing shared `common/` namespace is frozen. New migrations go in their component's namespace. Applied `common/` files are **not moved**: history rows are keyed by `(namespace, version)`, so a moved file would look unapplied and re-run. Files such as `0001_core_schema.sql` also mix several components' tables, so they cannot be split without rewriting applied history. `MigrationNamespaceTests` in `tests/Bazaar.Architecture.Tests` enforces the freeze: no new file in `common/`, and no frozen file removed or renamed.
- Personal fields stay encrypted per person (doc 01 decisions in force), and a component that stores them uses the kernel's secret and crypto services.

## 6. Enforced boundaries

These are checked by the architecture tests in `tests/Bazaar.Architecture.Tests`, on every build. A seeded-violation test proves each rule fires (the pattern already used there).

1. A component's implementation project references only: `Bazaar.Contracts`, `Bazaar.Sdk`, the kernel contract project, its own abstractions project, and the abstractions projects of components it depends on. Anything else is a failure.
2. No component implementation references another component implementation. (Baseline: existing violations are listed in a file with an owner and a wave, and the list may only shrink.)
3. Platform-neutral components use no Windows-only API (existing rule).
4. A component's SQL lives under its own namespace folder. A check fails on a new file in `common/`.
5. Every component has a manifest and a registration test that resolves its services in a test host.
6. Client components reference `Bazaar.Client.Core` contracts only, never another head (existing rule).

Rule 2 starts with a baseline. The `common` folder and the flat `Server` project are the baseline's main debt, and wave 2 removes them.

## 7. Isolation: in-process by default, out-of-process when needed

- **In-process** (default, first-party): a component runs in the host process, shares the DI container, and is trusted to the level of its manifest.
- **Out-of-process** (`process: out-of-process`): the component runs as its own process and talks through the wire protocol (doc 02) or the engine adapter protocol (doc 20). The kernel starts and supervises it, applies its `requires`, and routes its contract calls over the connection. Use this for: the FreeLLMAPI sidecar (a separate proxy process, already how it is run), Postgres management (a managed child process, doc 03 role), engine bridges (Godot, Studio), and any third-party component that is not trusted.
- A component's contract is the same in both modes. The kernel provides an out-of-process proxy that implements the same interface, so callers do not change.

## 8. Third-party components and the loader

Third-party extension is wanted (owner, 2026-10-30), so the design allows it, but the first release loads only first-party components.

- A third-party component is a folder under the install's `components/` directory with `bazaar-component.json` (the manifest) and its assemblies.
- The loader reads the manifest first, then loads the assemblies in a collectible `AssemblyLoadContext` only after validation passes.
- A third-party component is **out-of-process by default**. In-process third-party loading requires a signed package and an owner approval recorded in the audit log (signing is OPEN; doc 20 Q7).
- It may reach only the contracts it declares in `requires`. Its SQL runs in its own migration namespace.
- Client panes from third parties load in the client through the same `AssemblyLoadContext` mechanism described in doc 20, only after signing. The server never loads client UI code.

AOT and trimming: first-party components are compiled in and stay AOT-friendly. The loader is a separate, opt-in path, so it does not constrain the core.

## 9. What is the kernel

The kernel is the only code the host hard-codes. It contains:

- Component discovery, graph validation, ordering, lifecycle and the enable/disable setting.
- Configuration binding (`Bazaar:` sections), options validation and the safe-defaults rule.
- The shared `NpgsqlDataSource`, the instance lock, the schema gate and the migration runner.
- The wire transport and codec (`IWireCodec`), and the status listener.
- Secrets resolution and the taint/approval policy, because every component depends on them (doc 15).
- The Windows service and systemd hooks.

Everything else is a component, including GC log, Kanban, Vault, agents, gateway and its providers and adapters, settings UI, memory, Constellation and client panes. A new capability never adds a line to the host composition except its component's registration.

## 10. Migration plan in waves

Wave 0 (this PR): the model, the abstractions and graph validation in `Bazaar.Server`, the first converted component (Kanban), and rule 5 for it. Existing code keeps working; nothing else moves.

Wave 1: kernel split. Move discovery, ordering and lifecycle into a `Bazaar.Server.Kernel` folder; put the host composition's `Add*` calls behind component entries. Convert the gateway: `freellmapi` becomes a provider component, `openai_chat`, `anthropic_messages` and `gemini_generate_content` become adapter components. Convert settings and audit. Each conversion is one PR with its tests, and the wire contract stays unchanged.

Wave 2: data split, as decided 2026-11-02. The `common/` files applied before the freeze stay where they are and are frozen (section 5). Each component's new SQL goes in its own namespace. Components whose tables sit in `common/` keep those tables under the shared history until a later wave can rewrite them safely. GC log and Vault are components with their namespaces ready; Interrupts, Calendar, WorkLife, Council, Skimmer, Org and Souls become components as they are built, each with its contracts project.

Wave 3: project split. Each component's implementation moves to its own project (`Bazaar.Server.Components.<Name>` plus `.Abstractions`). Enforce rule 1 and 2 with no baseline left. Add the out-of-process proxy and move the FreeLLMAPI sidecar and managed Postgres onto it.

Wave 4: client seam. Doc 20 pane descriptors and the generic renderer in `Bazaar.Client.Core`. Dashboard, GC, Kanban and agent screens become `client-pane` components. Windows, Linux and Terminal heads register first-party panes.

Wave 5: third-party loader, signing, and the owner approval path (section 8). Opened only after waves 1 to 4 pass their gates.

Each wave gates on: green build and tests, architecture rules passing with the baseline no bigger than before, and a start test that brings the whole host up with every first-party component enabled.

## 11. New work today

Until wave 1 lands, new work follows the rule the coordinator relays to running threads:

- Give the work its own project or folder under `src/`, with one public `AddBazaarX()` registration called only from the host composition.
- Put its migrations in its own namespace (`components/<name>/NNNN_name.sql`), never in `common/`.
- Reference other components only through their contracts.
- Use the term "Bazaar Component" in docs and code. Mark any registration that wave 1 must convert with `TODO(component)`.

## 12. Open questions

1. Owner: confirm that every base function, including Kanban, GC and gateway, is a first-party component in v1, and that third-party loading is wave 5 (after signing and approval).
2. Owner: component enable/disable is a setting. Which components an operator may disable is a policy question (for example, may the taint policy be disabled?). Default: core components cannot be disabled.
3. Schema: the `(component_id, version)` history and the namespace folder layout need one answer before wave 2. Default: as in section 5.
4. Owner: confirm the optional-component rule above (`enabledByDefault`, explicit setting wins). Proposed because the Rubedo playground and other opt-in modules need a manifest-level default, and without one each module invents its own flag.
5. Doc 03 needs a `DataStore` provider role for managed Postgres (see MODULARITY-AUDIT section 5). It is a component under this model.

## 13. Standalone Core readiness audit (PROPOSED, 2026-11-19)

Coordinator request: get `Bazaar.Core` ready to become the standalone CDAX Core System repo, dependency-free and product-neutral. This is the audit of `src/Bazaar.Core` at main d7874f05. Nothing here is DECIDED; the two owner questions at the end block the code.

**What Core holds (9 files, about 210 lines).** `ComponentManifest`, `ComponentKind`, `ComponentGraph`, `ComponentGraphException`, `IBazaarComponent` and `BazaarComponentRegistration`. It has no reference to `Bazaar.Server`, `Bazaar.Contracts` or any other Bazaar project, and no `AgentId`, company, tenant or Postgres concept.

**Findings.**

| # | Finding | Where | Effect on standalone use |
|---|---|---|---|
| 1 | Two package references: `Microsoft.Extensions.Configuration` and `Microsoft.Extensions.DependencyInjection` | `Bazaar.Core.csproj`; `IBazaarComponent.AddServices(IServiceCollection, IConfiguration)`; `BazaarComponentRegistration` | Core is not dependency-free. The manifest, kind and graph are pure; only the entry point and registration use the two packages. |
| 2 | Product names in public API: `IBazaarComponent`, `BazaarComponentRegistration.AddBazaarComponents`, namespace `Bazaar.Core`, assembly `Bazaar.Core` | 5 files in Core; `IBazaarComponent` is used in 32 files, `AddBazaarComponents` in 23, `ComponentManifest` in 41 | Not product-neutral. |
| 3 | Product-specific configuration key `Bazaar:Components:<id>:Enabled` | `BazaarComponentRegistration.IsEnabled`; 14 files reference the key | Hard-coded prefix. |
| 4 | A Bazaar rename recorded in generic code: `LegacyIds = { ryusei -> ryusei-schedule }` | `BazaarComponentRegistration` | History of one product's component ids inside the generic kernel. |
| 5 | Doc comments cite `docs/design/BAZAAR-COMPONENTS.md` and Bazaar terms (migration namespace, `gateway:adapter:openai_chat`) | all Core files | Cosmetic. |
| 6 | `ComponentManifest.MigrationNamespace` encodes the Bazaar SQL-per-component layout | `ComponentManifest` | Mild: a Postgres-migration convention in the kernel, but it is only a pure string mapping. |

**Proposed split (needs the owner's go-ahead).**

1. `Core` keeps only the pure parts: `ComponentManifest`, `ComponentKind`, `ComponentGraph`, `ComponentGraphException`, and a neutral component interface that does not mention DI or configuration. No package references.
2. The DI and configuration entry point (`AddServices(IServiceCollection, IConfiguration)`, `AddBazaarComponents`, `IsEnabled`, the configuration key and `LegacyIds`) moves to its own adapter module, for example `Bazaar.Core.Hosting`, which keeps the two Microsoft packages. Heads and `Bazaar.Server.Host` reference the adapter.
3. The architecture tests gain a rule that `Bazaar.Core` has no package or project references, so it stays dependency-free once split.

**Owner questions.**

1. **Names.** What are the standalone repo's namespace and public type names? The docs record "CDAX" only as a trademark and the pilot company (`Bazaar.CDAX`), and nowhere as the name of a Core System repo. Renaming `IBazaarComponent` and `Bazaar.Core` touches about 40 files and every component's registration, and the `Bazaar:Components` key is an operator-facing setting that needs a compatibility read of the old key.
2. **Scope of the split.** Is the DI and configuration adapter module (item 2 above) the intended shape, or should Core keep `IServiceCollection` and accept the one package?

Coordination: the "Converge Bazaar and Resibo shared features" thread is adding `Principal` and `Scope` to Core and deleting `ProviderSelector` (#919). This audit touches none of those files; any change there lands through that thread.
