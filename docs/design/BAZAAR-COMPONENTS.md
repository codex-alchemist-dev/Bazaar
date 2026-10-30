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

## 4. Contracts and dependencies

- A component references another component only through its contract assembly (`<Name>.Abstractions`) or through `Bazaar.Contracts`. A reference to another component's implementation project is a rule violation (section 6).
- Cross-component calls go through interfaces resolved from DI. A component never constructs another component's concrete class.
- Wire messages (client and server) stay in `Bazaar.Contracts` with the JSON codec (doc 02). A component that adds a wire message adds its contract types there, namespaced by component.
- Providers and adapters are a `provides` role. A role has one selected implementation, chosen by settings with the existing `auto` or explicit rule (doc 03). Adapters are many: each registers under its wire family name (for example `openai_chat`) and the gateway looks them up by name.

## 5. Data ownership: migrations per component

- Each component owns a migration namespace: `src/<Component>/Data/Sql/<component-id>/NNNN_name.sql`. Numbers are local to the namespace.
- The migration history table is keyed by `(component_id, version)`. The runner applies namespaces in dependency order and records each file once.
- Components never write another component's tables. A cross-component read goes through the owner's contract. Foreign keys to another component's table are allowed only to a table the dependency declares in its contract.
- The existing shared `common/` namespace is frozen. New migrations go in their component's namespace. Moving `common/` files into their components is wave 2 of the plan (section 10); the history table migration keeps existing rows valid.
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

Wave 2: data split. Move each table family from `common/` to its component namespace, add the `(component_id, version)` history, and remove the `common/` baseline entries as each moves. Then GC log, Vault, Interrupts, Calendar, WorkLife, Council, Skimmer, Org and Souls become components, each with its contracts project.

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
4. Doc 03 needs a `DataStore` provider role for managed Postgres (see MODULARITY-AUDIT section 5). It is a component under this model.
