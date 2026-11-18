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
| Agent safety (`agent-safety`, PR #665) | Project `Bazaar.Server.AgentSafety` (module wiring, durable nonce store, containment adapters, startup gate); `Bazaar.Server.Identity.Postgres` (durable credential leases) | none | `vms`, `identity` (leases), `audit` (via kill switch audit sink) | Kill switch and containment in `Bazaar.Server/Security/KillSwitch`; the host composes the module and the Vms module. Fail closed when no VM backend is configured. |

## 3. Gaps

1. **Undeclared hard dependencies.** RyuseiFuda resolves the GC listener and the audit log but declared only `kanban`. This PR declares `gc` and `audit` and adds a test (`RyuseiScheduleDependencyTests`). The kernel now refuses to start RyuseiFuda without them.
2. **The manifest has no optional dependencies.** Kanban and RyuseiFuda use GC only when it is present. Declaring GC as `DependsOn` would make Kanban fail when GC is disabled, which is wrong. Declaring nothing hides the link. The model needs an `OptionalDependsOn` (or a `soft` flag). That is a schema choice, so it needs an owner answer before code changes.
3. **Constellation's dependency on Vault is a type reference, not a declared dependency.** It only runs when `Constellation:VaultId` is set, so it is optional too. Same fix as item 2.
4. **GC is effectively core, but it is not declared as core.** Thirteen files outside `Gc/` depend on it. No consumer declares `gc`. Calendar, Org, Agents and Http are not components yet, so the kernel cannot see their edge.
5. **Single assembly for the server features.** GC, Constellation and Vault are folders in `Bazaar.Server`, so no project boundary is enforced. The architecture tests check only Contracts and Client.Core layering, not component boundaries. Rules 1 and 2 of `BAZAAR-COMPONENTS.md` section 6 are not yet tests.
6. **RyuseiFuda is inside the Kanban project.** Layer 1 (Kanban) and layer 2 (RyuseiFuda) share one assembly. The `kanban` dependency is therefore not a project boundary.
7. **Migration namespace mismatch (DECIDED 2026-11-09: rename the id to `ryusei`; applied as code, see PR #360 follow-up).** The `ryusei-schedule` component maps to namespace `ryusei_schedule`, but its SQL folder is `ryusei`. The runner still applies it (after the component namespaces, by name), so nothing breaks today, but the ordering and ownership are not what the manifest says. `DECISIONS-LOG.md` already records this as an owner choice: rename the component id or the folder. Renaming the folder would change applied history, so the component id is the safer side. Resolved 2026-11-16 (coordinator): the folder stays `ryusei` and the namespace is `ryusei`; the folder is not renamed.
8. **GC, Constellation and Vault tables sit in `common/`**, which is frozen (see `BAZAAR-COMPONENTS.md` section 5). Their components own no migration namespace yet. This is correct under the freeze, and moving it would re-run applied files, so it stays until a wave decides otherwise.
9. **Client panes are hard-wired.** `FirstPartyPanes.CreateRegistry` names each pane. Heads cannot add or drop a feature pane without editing Client.Core.
10. **Version format.** Manifests use SemVer (`0.1.0`). The owner's rule (2026-11-12) is plain SemVer with the commit appended (`0.1.0+a1b2c3d`) for internal modules too, so the manifest and the display show the same string. No code change is needed, but the rule belongs in `BAZAAR-COMPONENTS.md`.

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

- **GC:** `Bazaar.GC.Abstractions` (log reader and writer contracts, access policy), `Bazaar.GC` (the log). Its consumers (Kanban, RyuseiFuda, Calendar, Org, Agents) reference the abstractions only. Biggest payoff, so first.
- **Constellation:** `Bazaar.Constellation` with its Vault graph source going through a Vault abstractions project. Its client pane goes in `Bazaar.Client.Constellation`.
- **Erasure (legal hold, doc 48 §9):** `Bazaar.Erasure.Abstractions` (`ILegalHoldReader`), `Bazaar.Erasure` (`PostgresLegalHoldReader`, `AddBazaarLegalHoldReader`). The erasure coordinator stays in Server until its own extraction; Server references only the abstractions.
- **RyuseiFuda:** `Bazaar.RyuseiFuda` split out of the Kanban project. It depends on `Bazaar.Kanban.Abstractions` and `Bazaar.GC.Abstractions`. Its client pane goes in `Bazaar.Client.RyuseiFuda`.
- **Identity KEK (doc 14, `identity.kek_provider`, DECIDED 2026-11-16):** `Bazaar.Server.Identity.Kek.Abstractions` (`IKekProvider`, `KekUnwrapException`) and `Bazaar.Server.Identity.Kek` (file and DPAPI providers, `KekProviderFactory`, `KekComponent` with id `identity-kek`, registered through `Bazaar.Core`). Server code depends on the abstractions only.
- **Install (module install gate, doc 48 section 1):** `Bazaar.Install.Abstractions` (the store seam `IModuleInstallStore`, the audit entry and approval record, the owner signer `IOwnerApprovalSigner`), `Bazaar.Install` (the signature, host and permission checks, the start guard, the install and approval services, and the `Trust` code). It depends on `Bazaar.Contracts` and its own abstractions only. The Server persistence adapter is `ModuleManagerRepository`, which implements `IModuleInstallStore` and runs the audit payload guard before it appends.

## 4a. Renames (planned, small PRs)

| Now | Target | Notes |
|---|---|---|
| `ryusei` component (layer 2 code in `Bazaar.Server.Components.Kanban`) | `Bazaar.RyuseiFuda` with `RyuseiFuda.Orrery` and `RyuseiFuda.Workday`/`Roadmap` parts | Module split done (#384). The scheduling layer's namespace is `Bazaar.RyuseiFuda.Orrery`; the `Ryusei*` type names, the `ryusei` id and the `Bazaar:Ryusei` settings section stay. |
| Roadmap code in `Bazaar.Server/Roadmap`, namespace `Bazaar.Server.Roadmap` | `Bazaar.RyuseiFuda/Roadmap`, namespace `Bazaar.RyuseiFuda.Roadmap` | Done. The `ryusei-roadmap` id and the `roadmap/0001` migration in `Bazaar.Server` stay where they are. |
| Layer 1 kanban (`kanban`) | Under `RyuseiFuda.Workday` or kept as `Bazaar.Kanban`, owner to confirm | Id and stored settings keys stay (doc 38 R4 defers renaming). |
| No connectivity code yet | `Bazaar.Conduit` with `Conduit.Cloudflared`, `Conduit.P2P` | New code only; nothing to rename. |

## 4b. Dependency modules: Valkey cache (PROPOSED)

Status: PROPOSED (2026-11-18, revised after coordinator review the same day). Nothing here is built. The owner has not decided this module, and no DECISIONS-LOG entry records it yet. Dependencies are modules tagged `Deps` and `3rd party` (doc 42, D1), so the Linux cache dependency gets the same layout as a feature module, with a `Deps` tag. The module list below extends section 4 for dependency modules only; the feature rules of section 4 are unchanged.

| Project | Holds | Rule |
|---|---|---|
| `Bazaar.Server.Providers.Abstractions` | `IProvider`, `ProviderStatus`, `ICacheProvider` (with `DeleteAsync`, doc 03) and the other provider seams. Namespace stays `Bazaar.Server.Providers`. | Moved from `src/Bazaar.Server/Providers/` (`ICacheProvider.cs`, and `IProvider.cs` and `ProviderStatus.cs` with it, PROPOSED). Contracts only, no implementation. |
| `Bazaar.Server.Valkey.Abstractions` | `IValkeyClient`, `IValkeyClientFactory`, `ValkeyCacheOptions` (section `Bazaar:Cache:Valkey`), and the module's tag constants (4b.4 item 7). | Contracts other modules may reference. No library reference. |
| `Bazaar.Server.Valkey` | `ValkeyCacheProvider` (implements `ICacheProvider`), `StackExchangeValkeyClientFactory` and `StackExchangeValkeyClient` (StackExchange.Redis, which speaks the Redis protocol Valkey implements), the supervised `valkey-server` child (4b.1), and the health probes (4b.2). | Registers `AddBazaarValkey()`. The Server host references the abstractions; only the host composition root references this project. |

Code that leaves `src/Bazaar.Server/Providers/Linux/`: `ValkeyCacheProvider.cs`, `IValkeyClient.cs`, `StackExchangeValkeyClient.cs`, `ValkeyCacheOptions.cs`, and the Valkey candidate in `LinuxProviders.CacheCandidates` (that list moves into the module's registration, PROPOSED). `SystemdServiceHostProvider.cs` and `SystemdCredsSecretResolver.cs` stay, because they are the service host and secret scheme, not the cache. The old namespace gets no alias (4b.4 item 4). Garnet is not in this module. It is a separate later module behind spike S2 (C07, doc 35).

Module identity (PROPOSED): id `valkey`; tags `Deps` and `3rd party`; kind `service` under doc 42 section 4 (hidden supervised child of the host, one per install); licence BSD-3-Clause (doc 42 section 13.2, to be confirmed by the licence scan). The tags are constants in `Bazaar.Server.Valkey.Abstractions` until `ComponentManifest` gains a Tags field (4b.4 item 7). StackExchange.Redis is a NuGet package under central package management and not a dep module (doc 42 section 4 boundary). Its licence is checked by the same scan.

Settings: `host.provider.cache` (doc 03) is unchanged: `auto`, `garnet`, `valkey`, default `auto` (Garnet only after S2). The child-process values are the `deps.valkey.*` rows of doc 42 section 15 (PROPOSED names and defaults, owner to confirm): `deps.valkey.managed`, `deps.valkey.binaryPath`, `deps.valkey.runtimeDirectory`, `deps.valkey.managedPort`, `deps.valkey.maxMemoryMegabytes`, `deps.valkey.memoryWarnPercent`, `deps.valkey.parentDeathWrapper` and `deps.valkey.startWindowSeconds`.

### 4b.1 Floors and the child process (PROPOSED)

- **Loopback only.** The child listens on loopback. A non-loopback listen is not a setting (the doc 42 section 15 floor).
- **Port.** `deps.valkey.managedPort` of 0 (the default) means a free loopback port is chosen per child start. Clients re-resolve the endpoint on every call and do not cache it at construction, so a restarted child on a new port is picked up without a host restart.
- **Password custody (PROPOSED, 4b.4 items 1 and 12).** The managed child's password is generated per run and held in memory as a secret value. It is delivered to the child on stdin. It rotates on every start, and it is never written to disk, to an environment variable, to a command line, to a log, to the status page or to a Kodo fact. Nothing durable needs custody here, because this is a local cache. An externally configured Valkey takes its password from the secret store (doc 14), through the `SecretReference` path of `ValkeyCacheOptions`. The managed child's in-memory password is the one deviation from doc 14's custody rule.
- **Parent death.** On Linux the child starts through the util-linux `setpriv --pdeathsig` wrapper named by `deps.valkey.parentDeathWrapper`. A managed start is refused when no parent-death mechanism is available. This is a floor, not a warning, so an orphaned `valkey-server` cannot outlive the host.
- **Stale-child sweep (PROPOSED).** Before a managed start, the host reads `valkey-server.pid` in `deps.valkey.runtimeDirectory`. If that pid is alive and `/proc/<pid>/comm` reads `valkey-server`, the host stops it before starting a new child. If the pid is dead or does not match, the host removes the stale file. A process whose comm does not match is never killed.
- **Startup order (PROPOSED, 4b.4 item 8).** The managed child starts before the cache provider probe in doc 03 startup step 5. The probe retries inside the bounded window `deps.valkey.startWindowSeconds`, with the per-attempt bound of `ValkeyCacheOptions.ProbeTimeout`. The window expiring marks the probe unusable. It does not fail startup, because the cache is not a required readiness role (doc 03).
- **Restart and stop.** The restart policy follows doc 42 sections 6.2 and 6.3. Stop happens in the host's shutdown path (doc 03 steps 1 to 4). Tree kill of the child with the host is a doc 42 section 16 test (PROPOSED for this child).

### 4b.2 Health probes (PROPOSED, doc 46 contract)

Every probe is non-critical. This is explicit: no Valkey probe can make the state `failing` (4b.3).

| Probe (doc 42 section 11) | Check | Critical |
|---|---|---|
| Liveness | Heartbeat from the supervised child. No I/O in the provider's liveness path. | no |
| Readiness | PING, then a SET and GET round trip on a reserved key (`bazaar:health:valkey`, outside the cache key prefix so it cannot collide with cache entries). | no |
| Dependencies | Used memory under `deps.valkey.memoryWarnPercent` (default 80) of `deps.valkey.maxMemoryMegabytes`. The running version equals the installed package version (doc 42 section 11, drift rule). | no |
| Self-test (scheduled, Full scope) | Config-level check only: `CONFIG GET maxmemory-policy` returns `allkeys-lru`, and `CONFIG GET maxmemory` equals `deps.valkey.maxMemoryMegabytes`. Plus a round trip with a short TTL and an expiry check after the TTL. The behavioural eviction fill test (fill the keyspace past the cap, confirm evictions and that an evicted key reads as a miss) is deferred (4b.4 item 6). | no |

### 4b.3 Failure mapping (PROPOSED, C07)

- The cache is a cache. A Valkey outage is `degraded`, never `failing`. Doc 03 already states that GC delivery and turns do not depend on the cache, and GC fan-out is Postgres NOTIFY (C07).
- A down child or a failed probe makes the `valkey` module amber, with a reason and an audit event. Kodo does not open the readiness gate of any dependent module, and the server's readiness roles (doc 03 `host.readiness.required_roles`) do not include the cache.
- While the cache is unusable, `ICacheProvider` callers get misses and dropped writes (the existing degrade behaviour of `ValkeyCacheProvider`). What presence shows while the cache is down is doc 23's decision and is not decided here (OPEN).
- Restart backoff exhausted: the module stays amber and the operator sees it in the module manager (doc 42 section 12). It is not red and it does not start an incident.

### 4b.4 Open items and PROPOSED decisions (need one answer each before code)

1. **Password custody (PROPOSED).** The managed child keeps a per-run in-memory secret, delivered on stdin, rotated on every start (4b.1). Nothing durable needs custody, so no doc 14 store scope is required for it. Owner to confirm.
2. **Eviction self-test (PROPOSED).** Config-level only (`CONFIG GET maxmemory-policy` is `allkeys-lru` and `CONFIG GET maxmemory` matches the setting). The behavioural fill test is deferred.
3. **Probe criticality (PROPOSED).** All Valkey probes are non-critical, as stated in 4b.2.
4. **Namespace (PROPOSED).** No alias for the old `Bazaar.Server.Providers.Linux` cache types. Nothing outside the Linux provider folder references them.
5. **Child-process settings (PROPOSED).** The `deps.valkey.*` rows in doc 42 section 15 need owner confirmation of names and defaults. `deps.valkey.runtimeDirectory` defaults to `/var/lib/bazaar/valkey` and is overridable by that setting (PROPOSED, owner decision recorded by the coordinator). The `ICacheProvider.DeleteAsync` addition is PROPOSED in doc 03 (WP12 status and the interface sketch).
6. **Eviction behavioural test (deferred).** The fill-and-evict test in 4b.2 is out of the first code PR and needs its own answer.
7. **Tags (PROPOSED).** `ComponentManifest` has no Tags field today (`src/Bazaar.Core/ComponentManifest.cs`). PROPOSED: add a Tags field in `Bazaar.Core`. Until then the tags are constants in `Bazaar.Server.Valkey.Abstractions`.
8. **Startup ordering (PROPOSED).** Child first, bounded probe retry window `deps.valkey.startWindowSeconds` (4b.1).
9. **Term and kind.** Doc 42 Q8 (`module` and `kind: dependency`). This section follows the proposed answer.
10. **Licence of StackExchange.Redis.** MIT is expected, not verified. It is not in doc 42 section 13.2 yet, and the licence scan must confirm it.
11. **Presence while the cache is down.** Doc 23's decision (OPEN).
12. **Secret custody by mode (PROPOSED).** Managed child: per-run in-memory password, rotated each start (item 1). External Valkey (`deps.valkey.managed` false): password taken from the secret store (doc 14) through `ValkeyCacheOptions.SecretReference`, never a plain setting.

## 5. Plan, in order

Each step is a small PR with tests, per `CLAUDE.md`.

1. **Declare real hard deps** (this PR): RyuseiFuda declares `gc` and `audit`. Test added.
2. **Optional dependencies** in the manifest. Needs an owner answer on the field name and semantics (item 2).
3. **Architecture rule for component boundaries**, starting from a baseline that may only shrink (rule 2, `BAZAAR-COMPONENTS.md` section 6). The baseline lists today's violations.
4. **GC abstractions project**, and move its consumers onto it.
5. **Split RyuseiFuda** out of the Kanban project.
6. **Constellation and Vault** projects and abstractions.
7. **Client modules** for GC, Constellation and RyuseiFuda, with the pane registry taking modules instead of named panes. Heads compose them. Progress: the registry composes `IPaneModule`s (#373); a module also registers its view (`PaneRegistry.RegisterView`) and can read a head-supplied service from `PaneModuleContext.Get<T>()`, so Client.Core no longer needs a feature's view model type to render it. `Bazaar.Client.RyuseiFuda` holds the board, schedule, roadmap and Workday panes (namespaces `Bazaar.Client.RyuseiFuda.Workday`, `.Orrery`, `.Panes`); the Windows, Android and Terminal heads reference it and list its modules. Next: `Bazaar.Client.GC` and `Bazaar.Client.Constellation`.

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

1. **Project naming.** RESOLVED (owner, 2026-11-09 and confirmed 2026-11-16): `Bazaar.GC`, `Bazaar.Constellation`, `Bazaar.RyuseiFuda`, `Bazaar.KODO`, `Bazaar.Rubedo`, `Bazaar.Conduit`. Kernel: `Bazaar.Core`. Decision is in section 6.1.
2. **Optional dependencies** (item 2): RESOLVED (owner, 2026-11-16, unified graph #614, #616): optional links are `SoftDependsOn`, already in `ComponentManifest`. Hard edges are acyclic and set start order; soft edges may be mutual and never set order; a missing dependency degrades only the features that need it. See section 6.2.
3. **RyuseiFuda migration name** (item 7): rename the component id to `ryusei` (no stored data uses the id, so this is the cheaper side), or the folder to `ryusei_schedule`. Recommend the id.
4. **Scope of wave 2.** Whether Calendar, Org and Agents become components before GC is split, since they are the GC consumers.

## Owner approvals module (doc 32 section 5a)

The owner-signature contract is its own module, not server code. It moved out of `Bazaar.Server` in the PR that adds the durable nonce store:

- `Bazaar.Approvals.Abstractions`: `OwnerSignedPayload` (the `bazaar.owner.v1` byte layout), `IOwnerSignatureVerifier` and its verdicts, `IOwnerNonceStore`, `IOwnerKeyRegistry` and `OwnerDeviceKey`, and `OwnerSignatureCrypto` (the ECDSA P-256 check).
- `Bazaar.Approvals`: `OwnerSignatureVerifier`, `InMemoryOwnerNonceStore` (tests and dev runs only), and the Postgres stores `Db.PostgresOwnerKeyRegistry` and `Db.PostgresOwnerNonceStore`.

The migration (`remote/0004_owner_nonce.sql`) stays in `Bazaar.Server` under `Data/Sql/remote`, because migrations belong to the server host. `Bazaar.Server` references the abstractions only. `ApprovalsRegistration.AddBazaarApprovals()` composes the verifier on the durable stores (every registration is TryAdd), and the agent safety module calls it, so one verifier serves the kill switch, the approval service and the GitHub merge gate. The module owns no SQL of its own (the nonce table sits in the `remote` namespace above), so it registers no `IBazaarComponent`: a component would only restate that.
