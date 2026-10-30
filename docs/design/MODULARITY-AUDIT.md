# Modularity audit: design versus code

Status: AUDIT (2026-10-30). Written from a read of `docs/` and `src/` at the commit this file lands on. No code was changed. Owner question: the original deep dives described a comprehensive module system where most base-app functions are modules or extensions. Is that still the design, and is it in the code?

## 1. Short answer

- **The "every base function is a module" design is not in this repo's docs.** A search of `docs/` for module, plugin, extension and addon finds only the items below. The deep dives (`Bazaar.md`, `Bazaar.Server.md`, `Bazaar.Windows.md`) describe GC, Kanban, Vault, agents, Constellation and the gateway as first-party systems, not as plugins. Git history has no deleted doc with that design either (`git log -S module -- docs` only matches an unrelated commit message). If the earlier deep dive exists, it is outside this repo's history. The owner should point to it so this audit can be checked against it.
- **What the docs do commit to** is narrower: a composition root that registers an explicit list of modules (not assembly scanning), pluggable infrastructure providers per role, a pluggable model gateway, and out-of-process engine adapters with first-party client panes. Third-party extension is deferred.
- **What the code has** is a DI-style module pattern (one `AddBazaarX()` per feature, called from the host), a real provider seam for infrastructure, and a real adapter seam for model wire families. It has no module system: no manifest, no loader, no capability registry, no isolation, no per-module migrations, and no enforced dependency rules between features.

So the answer is: **the design is only partly modular on paper, and the code is modular in registration but not in boundaries.** That is a workable state for v0.x, but it will not hold as parallel agent development speeds up unless the boundaries are made real now.

## 2. What the docs say (with references)

| Item | Source | Status | Modular? |
|---|---|---|---|
| Composition root: only `Bazaar.Server.Host` builds the host; modules are an explicit list, not assembly scanning (keeps Native AOT open) | `docs/design/03-server-host-and-providers.md`, Design | PROPOSED | Yes, registration only |
| Provider roles `ServiceHost`, `Vm`, `Cache`, `Search`; `auto` picks by `Probe()`; explicit unusable choice fails start | doc 03, Design | PROPOSED | Yes, infra only |
| Windows and Linux OS modules live in separate assemblies, so the Windows assembly never loads on Linux | doc 03, Project split | PROPOSED | Partly (see gaps) |
| Model gateway pluggable per install; operators may run their own proxies; FreeLLMAPI has priority support | `docs/deep-dives/Bazaar.Server.md` section 3; `docs/design/05-model-gateway.md`; `05b-freellmapi-first-class-provider.md` | DECIDED (gateway), FreeLLMAPI details in 05b | Yes, adapter per wire family |
| Engine Integration API: each engine is an out-of-process adapter declaring capabilities; optional in-process Avalonia pane plugins loaded via `AssemblyLoadContext`; v1 loads first-party plugins only | `docs/design/20-engine-integration-api.md` sections on pane plugins and Q7 | DECIDED (direction), details open | Yes, for engines and UI panes |
| Client pane plugins: first-party compiled-in only in v1 (default), answers doc 20 Q10 | `docs/design/27-client-architecture.md` Q3 | OPEN, default yes | Compile-time only in v1 |
| `Bazaar.Sdk` is the public plugin and agent SDK | `docs/research/notes/.../cicd_repo_licensing.md`; CLAUDE.md lists it | Owner-approved name | Code is a placeholder (`AssemblyMarker`) |
| Core systems (GC, Kanban, Vault, agents, Constellation, Company systems) | `docs/deep-dives/Bazaar.md` sections 2 and onward | DECIDED as features | Not described as plugins |

Not found anywhere in docs: a module manifest, a module lifecycle, a capability or permission model per module, per-module data ownership or migration namespaces, a dependency rule between feature modules, or a third-party extension contract.

## 3. What is built (with references)

**Module registration (real, works as a pattern).**
- `src/Bazaar.Server.Host/Composition/HostCompositionExtensions.cs` has `AddBazaarHostRuntime`, `AddBazaarSecrets`, `AddBazaarProviders`, `AddBazaarHostWire`, `AddBazaarAgentSafety`. Its summary says `HOST1 is the only place modules are wired`.
- Further `AddBazaar*` calls exist for gateway, sync, dev auth, settings and audit (`src/Bazaar.Server.Host/Composition/`), and for GC log, GC wire, wire connection, agent responder and hosting in `src/Bazaar.Server`.
- All platform logic is one assembly, `src/Bazaar.Server`, split into folders for Gc, Kanban, Vault, Agents, Gateway, Security, Interrupts, Calendar, WorkLife, Council, Skimmer, Org, Souls, Settings and Memory. The repo has about 400 C# files across all projects. Feature boundaries are folders, not projects.

**Infrastructure providers (real seam, mostly empty).**
- `src/Bazaar.Server/Providers/` has `IProvider`, `ProviderRole` (ServiceHost, Vm, Cache, Search), `ProviderSelector` and `ProviderCapabilityReport`. Selection follows doc 03.
- Only ServiceHost has OS implementations: `WindowsServiceHostProvider` and `SystemdServiceHostProvider`. Vm, Cache and Search pass empty candidate lists, so they resolve to none. `Providers/Fakes/` holds test doubles.
- OS code is split across assemblies for Windows (`src/Bazaar.Server.Providers.Windows`) but not for Linux. `src/Bazaar.Server/Providers/Linux/` lives inside the platform-neutral core assembly. This departs from doc 03's project split.

**Model gateway (real adapter seam).**
- `src/Bazaar.Server/Gateway/Adapters/IModelAdapter.cs` is one interface per wire family, with implementations for Anthropic, OpenAI and Gemini. `IAdapterRegistry` and `ModelGateway` select among them.
- FreeLLMAPI is in `src/Bazaar.Server/Gateway/FreeLlmApi/` (11 files, including its own wiring, routing adapter and health probe) inside the core assembly. It is not a separate module project and not a sidecar. It does plug into the adapter seam.

**Wire and GC (interfaces exist for extension points).**
- `IWireRequestHandler` is one handler per request type; its comment says "W4 and later modules add theirs". This is a real registration point.
- GC has `IGcLogWriter`, `IGcLogReader`, `IGcAccessPolicy`, `IGcBodyCodec`, `IGcNotifier` and related interfaces.
- `ISettingStore` and `ISettingAuditSink` back the settings registry.

**Features with no seam (hard-wired concrete classes).**
- Kanban: `TaskService` is a concrete class with no interface. Its `BlockerGraph`, `TaskTransitions` and data repositories are used directly.
- Vault: `src/Bazaar.Server/Vault/` has `Data` and `Git` folders only. No interface boundary is visible at the module level.
- Interrupts, Calendar, WorkLife, Skimmer, Org and Souls: concrete services in the core assembly.

**Data layer (the clearest symptom of no module boundary).**
- All migrations are in one namespace: `src/Bazaar.Server/Data/Sql/common/`, numbered `0001` to `0018`. Vault, Kanban, Gateway, Approvals, Skimmer and Calendar all share one sequence. Migration numbers are claimed by thread in the project plan, which is a coordination workaround for the missing boundary.

**Clients (heads are thin; the module seam is not built).**
- `Bazaar.Client.Core` holds the shared Avalonia code (Wire, Commands, Theme, Input, Gc). `Bazaar.Client.Windows` and `Bazaar.Client.Linux` are a `Program.cs` each. `Bazaar.Client.Terminal` exists (5 files) but is not in CLAUDE.md's project list. Doc 20's pane descriptor and `AssemblyLoadContext` loader are not implemented. A grep of `src/` finds no plugin loader.

**Not built:** Bazaar.Sdk (placeholder), engine adapters (Godot, Roblox), Constellation, Windows client screens, a Postgres packaging provider, and any module manifest or loader.

**Tests that enforce boundaries:** none. The only visibility rule is `InternalsVisibleTo` for `Bazaar.Server.Host.Tests`. Nothing stops `Kanban` from calling `Vault` internals or `Gateway` from reaching into GC storage.

## 4. Gaps

1. **No module definition.** The code cannot say what a module is: its public surface, its dependencies, its data and its lifecycle. Doc 03 says "modules" but never defines the term beyond the registration list.
2. **Boundaries are not enforced.** Everything is one assembly. Any feature can reference any other. Parallel agent work relies on review to keep boundaries.
3. **Single migration namespace.** Blocks independent module ownership of schema, and creates number collisions that are handled by claim lists.
4. **Provider seam is half-built.** Vm, Cache and Search have no real implementation, and the Linux OS code is in the wrong assembly versus doc 03.
5. **FreeLLMAPI is inside core.** It works through the adapter seam, but its wiring, health and routing code is not isolated. Doc 05b calls it first-class, so the question of sidecar versus in-process is still open.
6. **Features are concrete.** Kanban, Vault and the company systems have no interface for other modules or heads to depend on. A client that wants to render Kanban has no contract beyond the wire types.
7. **Extension contract is undesigned.** Doc 20 covers engines and UI panes only. There is no design for a third-party module, a capability or permission model, or signing. Doc 20 Q7 and doc 27 Q3 both default to first-party only, which is consistent with the code.
8. **Client seam is undesigned in code.** Pane descriptors and the generic renderer are doc 20 work, not built.
9. **Drift from docs.** Doc 03's Linux project split is not followed. `Bazaar.Client.Terminal` is not in the owner's list. Both should be reconciled.

## 5. Recommended plan

Principle: keep the owner's "fully modular" goal as a **boundary and contract** goal for the first-party code now, and defer runtime plugin loading and third-party extension until the Sdk is needed. Plugin loading is expensive, conflicts with Native AOT and trimming (doc 27 Q3), and nothing in v1 needs it.

**Decision for the owner (one question):** v1 modules are first-party, compiled in, with enforced boundaries; third-party loading is deferred to a later stage. This matches the current defaults in doc 20 Q7 and doc 27 Q3. Confirm or change it.

**Steps, in order.** Each is a small PR with tests, per CLAUDE.md.

1. **Define a module.** Add a short doc section (or a `MODULES.md`) that defines a module as: a project or folder with one public `AddBazaarX()` entry point, a public contract surface in `Bazaar.Contracts` or an abstractions project, its own migration namespace, and no references to other modules' internals.
2. **Enforce it.** Add an architecture test (for example with NetArchTest, or a plain test that reads project references) that fails the build when a feature module references another feature's internals. Start with a baseline list of known violations so the test is green and new violations fail.
3. **Split migrations per module.** Move `common/` into per-module folders (`gc/`, `vault/`, `kanban/`, `gateway/`, and so on) with a migration history keyed by (module, version). New work claims a module namespace, not a global number. This removes the claim-list workaround.
4. **Split the Linux providers** into `Bazaar.Server.Providers.Linux` to match doc 03, and reconcile the doc with `Bazaar.Client.Terminal`.
5. **Put an interface in front of Kanban, Vault and the company systems** where another module or head needs them. Interfaces go in the module's abstractions project, not in the implementation.
6. **Make FreeLLMAPI a module** in its own project (`Bazaar.Server.Gateway.FreeLlmApi`), registered by `AddBazaarFreeLlmApi()` from the host. Keep the adapter seam as its only coupling. Decide sidecar versus in-process under doc 05b; the module shape works for both, because a sidecar is just a different adapter.
7. **Build the client seam** from doc 20: pane descriptors and a generic renderer in `Bazaar.Client.Core`. Windows, Linux and Terminal heads register first-party panes at compile time. This is what lets "the base app is modules" hold on the client too.

**How the new work plugs in:**

- **FreeLLMAPI sidecar or module:** a gateway adapter in its own project, behind `IModelAdapter`. Core sees only the gateway contract. Operators can replace it with another proxy without core changes.
- **Postgres packaging:** a new provider role (for example `DataStore`), with managed child-process implementations in the Windows and Linux provider assemblies, chosen by `ProviderSelector` like the others. Core sees only a connection string. Doc 03 needs this role added; it is PROPOSED, so the change is in scope for the doc.
- **Constellation:** a feature module `Bazaar.Server.Constellation` with its own migration namespace, reading the Vault graph through the Vault contract. Its client pane is a first-party pane from step 7. Doc 19 owns the design.
- **Windows client:** `Bazaar.Client.Windows` stays a head that supplies platform services (tray, service host, dictation). Screens (start dashboard, GC, Kanban, agents) are feature views in `Bazaar.Client.Core`, so Linux and Terminal get them for free. Avalonia.Headless tests per CLAUDE.md.

**What to avoid:** a runtime plugin loader now; a module framework with its own DI container; splitting every folder into a project before steps 1 and 2 define what a boundary is.

## 6. Open questions

1. Owner: where is the original, comprehensive module design? It is not in this repo's history. If it exists in a private or earlier draft, its requirements should be checked against section 5.
2. Owner: confirm v1 modules are first-party with enforced boundaries, and third-party loading is deferred (section 5 decision).
3. Doc owners: doc 03 (Linux project split, `DataStore` role), doc 05b (FreeLLMAPI sidecar versus in-process) and doc 20 Q7 and Q10 need answers that match this plan.
