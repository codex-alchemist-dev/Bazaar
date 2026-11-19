# 80. MCP access and the tool registry

Status: PROPOSED (2026-11-19). Nothing here is built. The owner's direction of 2026-11-19 (relayed in the task brief) to incorporate everything from Resibo that can benefit Bazaar, and to put anything that conflicts with Bazaar's design into a separate module, is DECIDED. Under it, the adoption of Resibo's Agent Control API as `Bazaar.Server.McpAccess`, its Tool Call registry as `Bazaar.Server.ToolRegistry`, and its Architect schema registry as the optional `Bazaar.Server.SchemaRegistry` is DECIDED (2026-11-19). Every detail below those module names is PROPOSED unless it restates a DECIDED item of another doc. Owner questions: 13 (section 15).
Depends on: 03-server-host-and-providers.md (provider seam, settings registry), 04-postgres-schema-and-migrations.md (conventions, NOTIFY), 05-model-gateway.md (prescreen calls), 06-agent-runtime.md (tool dispatch, `ToolRegistration`, step cap), 14-identity-service-and-passport.md (signing keys, `ISecretStore`), 15-security-taint-and-approvals.md (tiers, taint, approvals, `rate_bucket`, hand-over schemas), 17-coding-agent-harness.md (tool set, `run_checks`), 20-engine-integration-api.md (engine tool contract), 32-remote-access-auth-and-privacy.md (access tokens, roles, step-up), 40-core-feature-modules-audit.md (module layout, `SoftDependsOn`), 46-kodo-health-module.md (health contract, chaos), 48-gap-decisions-security-identity.md (module trust, roles, audit, sandboxes), BAZAAR-COMPONENTS.md (section 4.2 `Principal`, section 5 migrations).
Referenced, not read in this checkout: doc 72 (access grants; used for the DM rule) and doc 54 (Soul Marketplace, open PR #772). Statements about them are marked unverified.
Used by: 17, 20, 34 (test strategy), 46 (Kodo probes), 51 (web admin sections via ports), 54, 72.

## 1. Purpose

Two things, one design.

1. **`Bazaar.Server.McpAccess`** lets an external AI agent (Claude Code, Codex, any MCP-capable tool) or a script use a running Bazaar the way a careful contractor would. It is an MCP server plus a headless command line, both thin front ends on one server-side core. The human owner issues a token. The token is capped below the owner, so the owner's authority is structurally unreachable. Every call is audited against a `Principal`. Rate limits are tighter than a human's.
2. **`Bazaar.Server.ToolRegistry`** is the one directory of tools: what exists, its schema, its category, who may see it on which surface, and whether it is up right now. Four categories (`READ_ONLY`, `MUTATING_STAGED`, `DEV_OBSERVABILITY`, `TEST_EXECUTION`) decide which surfaces may offer a tool. A mutating tool never changes the real target. It writes a staged proposal into an asynchronous review gate, which is Bazaar's approval service.

The two modules are separate because the registry serves more than MCP: the agent turn loop (doc 06), engine adapters (doc 20) and the coding harness (doc 17) all draw from it. MCP is one surface onto it.

Three optional or dev-only companions are described here so nothing from Resibo is dropped: `Bazaar.Server.SchemaRegistry` (section 9), `Bazaar.Server.TestOrchestration` (section 8) and `Bazaar.Server.McpAccess.DevSeed` (section 7.5).

## 2. Decided inputs

- **Owner direction, 2026-11-19 (DECIDED):** adopt Resibo's useful features; overengineer; separate conflicting variants into separate modules rather than choosing; account for everything. Providers are plain modules on Core, with no selector.
- **`Principal` (owner, 2026-11-19, BAZAAR-COMPONENTS 4.2):** one type `kind:id`, kinds `agent`, `user`, `service`, `system`. All attribution here uses it. An external tool's actions are `service:<id>` (or `agent:<id>` when bound to an agent, section 5.2).
- **Module rules (CLAUDE.md, BAZAAR-COMPONENTS):** every feature is its own module with an Abstractions project; no feature code in `Bazaar.Server` or `Bazaar.Client.Core`; a module references only other modules' Abstractions; shared libraries hold contracts and logic with no third-party dependencies; providers are separate modules; migrations live in the module's own namespace.
- **Doc 15 (DECIDED items):** external content taints hard; `ToolDispatcher` is the only path from model output to side effects; approvals are signed by the owner's device key and bound to the args hash; agents never hold an approval capability; no blanket approvals; the kill switch freezes write tools.
- **Doc 48 (DECIDED):** custom roles over capability and scope grants; agents never hold `admin.*`; no role can grant itself an approval capability; the audit log records every read and write by humans and agents by object id, never content; developer sandboxes use a fake-data test company; high-risk actions need biometric step-up (doc 48 section 8).
- **Doc 15 and DECISIONS-LOG round 13 (configurability):** undecided values are operator settings with the most protective shipped default. Settings tighten floors and never loosen them.
- **Doc 46 section 10:** every module implements the Kodo health contract.
- **Client rules (CLAUDE.md):** keyboard-first, mnemonic palette, no Ctrl chords, fully themeable, headless UI tests, no web client. The server does all compute.

## 3. What this doc does not decide

- The audit log's storage, anchoring and Merkle proofs (doc 48 section 5). This doc only names the events it writes.
- Access grants to DMs and other private GCs (doc 72, unread here). Section 12 states only that MCP never reads a DM without one.
- The approval card, signing key and approval store (docs 15, 32 section 5a). The staging gate calls them and adds no approval path.
- The role and grant tables (doc 48 section 4). Token scopes reuse the grant vocabulary.
- Whether Streamable HTTP or any network transport is ever offered (section 5.4). Only stdio and the authenticated local socket are in scope.
- The Soul Marketplace's moderation design (doc 54, unread). Section 9 flags the overlap only.
- Which MCP specification revision to support first. The server negotiates a protocol version at initialize, and the supported list is a setting.

## 4. Design: module map

| Module (id) | Projects | Kind |
|---|---|---|
| `tool-registry` | `Bazaar.Server.ToolRegistry`, `Bazaar.Server.ToolRegistry.Abstractions` | Server module. No client side. |
| `mcp-access` | `Bazaar.Server.McpAccess`, `Bazaar.Server.McpAccess.Abstractions`, `Bazaar.Client.McpAccess` (token pane) | Server module plus client module |
| `mcp-transport-local-socket` | `Bazaar.Server.McpAccess.Transport.LocalSocket` | Provider module (named pipe on Windows, Unix socket on Linux, behind `IMcpTransport`) |
| `mcp-transport-stdio-bridge` | `Bazaar.McpAccess.Bridge` (executable `bazaar-agent`), `Bazaar.Server.McpAccess.Transport.StdioBridge` (server side of the bridge handshake) | Provider module plus a small executable |
| `mcp-dev-seed` | `Bazaar.Server.McpAccess.DevSeed` | Provider module, dev only (section 7.5) |
| `test-orchestration` | `Bazaar.Server.TestOrchestration`, `Bazaar.Server.TestOrchestration.Abstractions` | Server module, off in production (section 8) |
| `schema-registry` | `Bazaar.Server.SchemaRegistry`, `Bazaar.Server.SchemaRegistry.Abstractions` | Optional server module (section 9) |

Hard dependencies: `tool-registry` needs `audit` and the policy and approval abstractions (section 11, item 1). `mcp-access` needs `tool-registry`, `audit`, `remote` (token and revocation conventions) and `identity` (hash pepper and signing). Soft dependencies: `mcp-access` soft-depends on `kodo` (DEV tools), `test-orchestration` and `gc` (read tools); `tool-registry` soft-depends on `agents` (turn surface), `engine` adapters and `schema-registry`. A missing soft target removes only the tools that need it, as BAZAAR-COMPONENTS 4.1 specifies.

Namespaces follow the project names. Note that `Bazaar.Server.Agents.Abstractions` already has a class `ToolRegistry` in `Bazaar.Server.Agents.Turns.Tools`; it compiles beside the namespace `Bazaar.Server.ToolRegistry`, but section 15 asks the owner whether to rename for clarity.

## 5. Design: Bazaar.Server.McpAccess

### 5.1 Two front ends, one core

The MCP server and the headless CLI are both clients of one server-side `IAgentAccessService`. Every token check, scope check, rate limit and audit write happens once, on the server. Neither front end enforces anything itself. This is Resibo's rule (`core/agent_control/CLAUDE.md`, "Real gotchas"), kept because the moment one surface enforces on its own, the two drift and one becomes the permissive path.

```csharp
// Bazaar.Server.McpAccess.Abstractions
public interface IAgentAccessService
{
    Task<AccessSession> OpenSessionAsync(PresentedToken token, TransportInfo transport, CancellationToken ct);
    Task<IReadOnlyList<ToolSpec>> ListToolsAsync(AccessSession session, CancellationToken ct);
    Task<AccessCallResult> CallToolAsync(AccessSession session, string tool, JsonElement args, CancellationToken ct);
    Task CloseSessionAsync(AccessSession session, CancellationToken ct);
}

public sealed record PresentedToken(string Secret);          // never logged
public sealed record TransportInfo(string TransportId, string? PeerOsUser, string? PeerAddress);
public sealed record AccessSession(Guid SessionId, Guid TokenId, Principal Principal, TransportInfo Transport, DateTimeOffset OpenedAt);

public enum AccessOutcome { Executed, Staged, Denied, RateLimited, NotOffered, Unavailable, Failed }
public sealed record AccessCallResult(AccessOutcome Outcome, string? ResultJson, string? Reason, Guid? StagedChangeId);

public interface IAccessTokenService
{
    Task<IssuedToken> IssueAsync(IssueTokenRequest request, Principal issuer, StepUpProof stepUp, CancellationToken ct);
    Task<bool> RevokeAsync(Guid tokenId, Principal by, string reason, CancellationToken ct);   // idempotent
    Task<IReadOnlyList<AccessTokenInfo>> ListAsync(Principal viewer, bool includeInactive, CancellationToken ct);
    Task<AccessTokenInfo?> AuthenticateAsync(PresentedToken token, TransportInfo transport, CancellationToken ct); // null = refused, one generic refusal
}

public sealed record IssueTokenRequest(string Label, PrincipalKind Kind, string? AgentId, IReadOnlyList<TokenScope> Scopes,
    TimeSpan Lifetime, string? BoundOsUser, string TransportId);
public sealed record IssuedToken(AccessTokenInfo Info, string Secret);   // Secret shown once, never recoverable
public sealed record TokenScope(string Capability, string Resource);     // doc 48 grant vocabulary
```

`Principal` and `PrincipalKind` are the Core types of BAZAAR-COMPONENTS 4.2. `StepUpProof` is the biometric step-up result of doc 48 section 8 and doc 32.

The MCP protocol layer is a small dependency-free JSON-RPC 2.0 implementation over newline-delimited frames, with source-generated `System.Text.Json` (consistent with the `IWireCodec` decision, `docs/spikes/S6.md`). Resibo made the same choice for the same reason (`core/agent_control/CLAUDE.md`, "MCP transport is deliberately dependency-free"): adopting an SDK later replaces one project. A provider module that wraps the official C# MCP SDK (`ModelContextProtocol`, Apache-2.0, per `docs/research/notes/Bazaar stack feasibility research/agent_runtime_dotnet.md`, secondary) can replace the protocol layer without touching the core. The server speaks only the server role: it answers `initialize`, `tools/list` and `tools/call`. It does not issue sampling or elicitation requests to the client in v1 (OPEN, section 15).

### 5.2 Tokens, Principal kind and scope

A token is always issued by a human `user` principal, through the client (section 5.6), with step-up. It is never self-provisioned.

- **Principal kind.** `service` (default): the token acts as `service:mcp.<tokenId>`, a pure external tool with no Bazaar identity. `agent`: the token acts as an existing agent, `agent:<id>`, and its effective tools are the intersection of that agent's `agent_tool_grant` rows and the token's scopes. `agent` kind is off by default (`mcp.token.allow_agent_principal`) because it lets an outside process act under a company member's name. Both kinds are recorded as the `Principal` text in every audit entry.
- **Scope.** A token holds a list of `(capability, resource)` pairs in the doc 48 grant vocabulary (for example `data.read`/`kanban`, `data.read`/`vault:docs/*`, `staging.propose`/`kanban`, `dev.observe`/`kodo`). A tool declares the one capability it requires (section 6.1). The call is allowed only if the token holds it, the issuer still holds it (checked on every call, so a later role cut takes effect at once), and it is not in the ownerless set below.
- **Ceiling, structural.** A token's effective power is `token scopes ∩ issuer's current grants − owner-only set`. The owner-only set is a fixed code list, not a setting: approval capabilities (`approval.*`), `admin.*`, role and grant changes, secret and Passport access, token issuing and listing other tokens, audit export, kill-switch control, Tier 3 actions. No code path maps a token to the owner role, and the table has a CHECK that the ceiling column equals `below-owner`. This is Resibo's "never carries `owner`" rule (`v3-deepdive-55` section 3.1) restated for custom roles: the owner who wants an owner-level action performs it, informed by the agent's staged recommendation.
- **Expiry.** Every token has an expiry. Default 30 days, maximum 90 (settings). A token never renews itself.
- **Storage.** Only a SHA-256 hash is stored, with a server pepper held by the Identity Service. The secret has a fixed visible prefix (`bzr_mcp_`) so secret scanners and the client can recognise it. It is shown once at issue.
- **Optional binding.** On a local transport the token can be bound to an OS account (`BoundOsUser`). The transport checks the peer's account against it, so a leaked token used from another local account fails. This is on by default (`mcp.token.require_os_user_binding`).

### 5.3 Session lifecycle and taint

An MCP session opens with `OpenSessionAsync`, which authenticates, checks expiry and revocation, applies the transport binding, and writes `mcp.session.opened`. The session is **born hard-tainted**: its `session_taint` row starts in state `Hard` with `taint_sources = ["mcp:<tokenId>"]`. Doc 15 treats an authenticated external tool as an untrusted input source, and this follows the task's rule that tokens from MCP clients are untrusted input. Consequences under doc 15 with no new mechanism: Tier 1 calls escalate to Tier 2, Tier 2 calls need per-action approval, mounts and leases are never attached, and anything the external agent writes is recorded with `source_trust = external` so Bazaar agents that later read it see it quoted and labelled, not as instructions. A session is per connection and ends on close, idle timeout or revocation.

### 5.4 Transports (provider modules)

```csharp
public interface IMcpTransport
{
    string TransportId { get; }                       // "local-socket", "stdio-bridge"
    Task RunAsync(IMcpFrameHandler handler, CancellationToken ct);   // accepts connections, hands frames to the core
}
public interface IMcpFrameHandler
{
    Task<ReadOnlyMemory<byte>?> HandleAsync(TransportInfo transport, ReadOnlyMemory<byte> frame, CancellationToken ct);
}
```

Providers are plain modules. All enabled transports run at once, and the core has no selector.

- **Authenticated local socket** (`mcp-transport-local-socket`). Windows: a per-user named pipe with an ACL for the owning account only. Linux: a Unix domain socket in the user runtime directory, mode 0600. The token is the first frame. The provider supplies the peer's OS account for binding. No network listener exists. The OS-specific code sits in this provider, behind `IMcpTransport`, as the Windows-first rule requires.
- **Stdio bridge** (`mcp-transport-stdio-bridge`). The server is a service, so it cannot itself be a stdio child. Most MCP clients launch a command and talk to it over stdio. The `bazaar-agent` executable is that command: it reads the token from an environment variable or a file path argument (never a command-line value, which leaks into process listings), opens the local socket, and relays frames. It enforces nothing. It ships beside the clients, is small enough to review in one sitting, and contains no model or tool logic. It never spawns processes from configuration, which avoids the class of stdio command-injection issues reported against MCP tooling (secondary; `docs/research/notes/Bazaar remaining gaps research/agent_security_threat_model.md`, CVE-2026-30623, unchecked against NVD).
- **Later, OPEN.** A network transport over Conduit (doc 39) and a guest-channel transport so the in-VM coding harness can use the same server (section 11, item 5). Neither is designed here.

The headless CLI is the same `bazaar-agent` executable in command mode (`bazaar-agent --token-file <path> kanban list`, `... stage-change ...`). It gives CI and scripts the same server-side enforcement with a non-conversational shape. Command names are PROPOSED.

### 5.5 Revocation and kill

- Revocation is immediate. Authentication re-reads the token row on every call (no cache longer than the call), and `RevokeAsync` sends a Postgres NOTIFY so live sessions on every transport close within the next frame. Double revoke is a no-op and writes no second audit entry.
- Who may revoke: the issuer, the owner, or any human holding `mcp.tokens.revoke`. The token itself may revoke only itself.
- A global kill (doc 15 kill switch) freezes every `MUTATING_STAGED` and `TEST_EXECUTION` tool at the registry. Read tools stay up so the status page and Kodo keep working. The owner can also pause all MCP sessions with one setting (`mcp.enabled`).
- Expired and revoked tokens produce one generic refusal to the caller. The reason goes to the audit log only.

### 5.6 Token issuing UI (`Bazaar.Client.McpAccess`)

A pane registered through the client module registry (doc 41b). It is a view of server state; the client computes nothing.

- **List.** Label, kind, scopes in plain language, issued by, expiry, last used, call count, state. Filter to active. Selecting a token opens its activity (the audit entries filtered by its `Principal`).
- **Issue wizard** (Windows and Linux heads; the Android head can view and revoke only, so a phone cannot mint a standing credential, PROPOSED). Steps: label and purpose; kind (`service` or `agent`, `agent` hidden unless enabled); scope picker with one plain sentence per capability and the doc 48 permission-card styling; lifetime; local binding; review; step-up. The secret is shown once, with a copy button and a generic JSON stanza for an MCP client's server list. The stanza names the `bazaar-agent` command and the token file path, never the secret itself.
- **Revoke and revoke-all** with confirmation. Revoke-all needs step-up.
- **Mnemonics** (PROPOSED, subject to doc 41's registry): `mcp` opens the pane, `mtok` starts issuing, `mrev` revokes the selected token. No Ctrl chords. Every state word has a screen reader label; the pane is fully keyboard reachable.
- Per the CLAUDE.md UI rule it ships with `Avalonia.Headless` tests.

## 6. Design: Bazaar.Server.ToolRegistry

### 6.1 What is reused and what is new

Reused as is (no duplicate is created): `ITool`, `IContextualTool`, `ToolCallContext`, `IToolRegistry`, `ToolRegistration` (`Bazaar.Server.Agents.Abstractions`, `Turns/Tools`); `ModelToolDefinition` (`Bazaar.Contracts.Gateway`); `ToolCatalogue`, which refuses a definition with no tool, a repeated name and a non-object schema (`Bazaar.Server.Agents`); `ToolCapability`, `ToolTier`, `CapabilityClass`, `SessionRoles`, `IToolPolicyEngine` and `DispatchContext` (`Bazaar.Server/Security/Policy`); the approval service and `IApprovalStore` (`Bazaar.Server/Security/Approvals`); `agent_tool_grant` (doc 06); `TurnRunnerOptions.StepCap` and `TurnEndReason.StepCap`.

New in this module: the **category** axis, the **surface** axis (who may be offered a tool), one merged **`ToolSpec`** manifest record that unifies the three existing registration records, the **availability** hook, the **staging gate**, and **external exposure** as an explicit opt-in. The registry sits in front of the existing dispatch path. It does not replace `ToolDispatcher`, which stays the only path from model output to side effects for agent turns.

```csharp
// Bazaar.Server.ToolRegistry.Abstractions
public enum ToolCategory
{
    ReadOnly,           // no write path at all
    MutatingStaged,     // only effect: write a proposal into the review gate
    DevObservability,   // pure reads for development and debugging, zero side effects
    TestExecution,      // real, disruptive side effects, test targets only
}

[Flags] public enum ToolSurface { AgentTurn = 1, McpAccess = 2, HeadlessCli = 4, EngineAdapter = 8, Harness = 16 }

public sealed record ToolSpec(
    string Name, string Description, string ParametersJsonSchema,
    ToolCategory Category, ToolSurface Surfaces,
    string RequiredCapability,          // doc 48 capability, e.g. "data.read"
    string OwnerModule,                 // component id that registered it
    bool ExternallyExposed);            // false by default; true only by the module author and the owner's install approval

public interface IToolDirectory
{
    // The manifest for a caller, filtered through the same Check used at dispatch. The caller's authority is resolved
    // server-side from its session; ToolCallerContext carries no role field, so a caller cannot assert one.
    IReadOnlyList<ToolSpec> Enabled(ToolCallerContext caller);
    ToolCheck Check(string name, ToolCallerContext caller);       // Allowed | one of the denial reasons below
}

public sealed record ToolCallerContext(Principal Principal, ToolSurface Surface, Guid SessionId, IReadOnlySet<TokenScope> TokenScopes);

public enum ToolDenial { Unregistered, NotOfferedOnSurface, CategoryDisabled, CapabilityMissing, PolicyDenied, Unavailable }

public interface IToolAvailability { bool IsAvailable(string toolName); }   // "do not offer an always-failing tool"
public interface IToolRegistrar { void Register(ToolSpec spec, ITool tool, Func<bool>? available = null); }
```

### 6.2 The four categories and how they map onto doc 15

The category does not replace `CapabilityClass` or `ToolTier`. It is orthogonal: it decides which surfaces may offer a tool and which review gate applies. Registration validates that the combination is consistent, or the registrar throws at start.

| Category | Meaning | Consistent with | Offered on |
|---|---|---|---|
| `ReadOnly` | No write path at all. | `CapabilityClass.Read`, `ToolTier.Autonomous` | Any surface the install enables |
| `MutatingStaged` | The tool's own effect is only to write a `staged_change` row. The real target changes later, when a human decision applies it. | The tool is `Read` or `Post`-free; the **apply** step carries the real class and tier (for example `Push` or `Merge`, Tier 2) | Agent turns and MCP; the proposal is allowed at Tier 0 or 1, the apply is gated |
| `DevObservability` | Pure reads for development and debugging (Kodo status, logs, run status, dependency versions). | `CapabilityClass.Read` | MCP and the CLI by default; agent turns off |
| `TestExecution` | Real, disruptive side effects (process kill, reset a test company, submit through the live pipeline). | `CapabilityClass.Exec`, never offered on a real company | MCP and the CLI, only when enabled (section 8) |

A tool is in exactly one category. A tool that is both observability and execution is split into two tools. `MUTATING_DIRECT` (an irreversible action with no review gate) does not exist as a category, and a test asserts the enum's exact membership so adding one is deliberate. This is Resibo's decision (`v3-deepdive-07` section 4, `core/tool_call/CLAUDE.md`), kept because it prevents a "just this once" ungated tool.

### 6.3 Mutating tools: the staging gate onto Bazaar approvals

This is where Resibo and Bazaar differ, and the difference is resolved without a second approval path.

- **Resibo:** a `MUTATING_STAGED` tool self-applies during unattended processing because it only stages into an existing moderation queue; no live human is present, so a confirmation prompt would hang (`v3-deepdive-07` section 4).
- **Bazaar:** Tier 2 actions need the owner's signed approval, and hard-tainted sessions always do (doc 15). Agent text carries no authority.

The shared idea is that the *tool* never touches the target. So in Bazaar a staged tool call:

1. Validates its arguments against its schema and an **enumerated field allowlist** (Resibo's `WRITABLE_FIELDS` lesson, `v3-deepdive-07` section 8: even within staged tools the writable surface is narrow and explicit, never "any field").
2. Writes a `tool_registry.staged_change` row (section 6.5) with the proposer's `Principal`, the target, a canonical diff, the args hash and the session's taint sources. It returns `Staged` and the change id to the caller. The caller is told plainly that nothing has changed.
3. Calls `IStagingGate.RequestApplyAsync`, which creates a normal doc 15 approval request for the tool `toolregistry.apply:<tool>` with the diff as the card payload and taint excerpts marked as quoted. The owner approves, edits then approves, or denies, exactly as for any other Tier 2 action, signed by the device key and bound to the args hash.
4. On an approved, unexpired, unconsumed approval, the owning module's `IStagedChangeApplier` applies the change once and the approval moves to `Consumed`. A changed diff voids the approval. A denial or expiry marks the change `rejected` or `expired`.

```csharp
public interface IStagingGate
{
    Task<StagedChange> StageAsync(StageRequest request, CancellationToken ct);        // writes the row, requests approval
    Task<StagedChange?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<StagedChange>> ListPendingAsync(Principal? proposer, CancellationToken ct);
}
public interface IStagedChangeApplier                       // implemented by the module that owns the target
{
    string Tool { get; }
    Task<ApplyResult> ApplyAsync(StagedChange change, VerifiedApproval approval, CancellationToken ct);
}
public sealed record StageRequest(string Tool, Principal Proposer, Guid SessionId, string TargetKind, string TargetRef,
    JsonElement Diff, ImmutableArray<TaintSource> TaintSources);
public enum StagedStatus { Pending, Approved, Applied, Rejected, Expired, Superseded }
```

Who may decide a staged change is whatever doc 15 and doc 32 already say; this module adds no reviewer role. Whether a lower-risk staged change could be decided by a delegated reviewer instead of the owner is OPEN (section 15). A staged change on a target where the apply is below Tier 2 (for example a note in the proposer's own private area) is applied after a Tier 0 or 1 policy decision and a GC notice, never silently.

The Resibo "LOCAL layer self-applies, SHARED needs review" pattern (`v3-deepdive-40` section 3) maps cleanly: writing into the proposer's own private Vault area is Tier 0 and applies directly because it is not a shared target; promotion to a shared target is a staged Tier 2 change. This is the rule section 9 uses.

### 6.4 Dispatch order and the multi-round loop

`Check` runs in a fixed order, cheapest and least informative first: registered; offered on this surface; category enabled; the caller holds the capability (token scope, or `agent_tool_grant` for an agent turn); `IToolPolicyEngine` (taint, tier, rate); `IToolAvailability`. Any resolver failure, exception or null is a denial (fail closed, doc 15). The surface check comes before the capability check on purpose (Resibo, `core/tool_call/CLAUDE.md`): "this surface does not offer that tool at all" must not read as "a better role would unlock it". A denied attempt on a privileged tool is audited too, because an investigation starts from "did something try".

The enabled list and the dispatch check use one method, so a tool can never be callable that a caller was not shown.

**Multi-round loop.** Resibo's loop (`v3-deepdive-07` section 3.3) is one loop in which the model either calls tools or gives its final answer. There is no fake "finalize" tool, and `max_rounds` is a safety cap, not the primary stop. Bazaar's agent turn loop already is this loop (doc 06 "Turn loop": dispatch tool calls, append results, repeat until a final message, step cap, token ceiling, wall clock or interrupt), with the cap `runtime.turn_step_cap` (default 40) and `TurnEndReason.StepCap`. So:

- **Agent turns:** no new loop and no second cap. The registry adds nothing here beyond the category filter.
- **Server-hosted non-turn loops** (the SchemaRegistry prescreen, a future server-run assistant): `IToolLoopGuard` gives them the same shape with `toolregistry.loop.max_rounds` (default 8, Resibo's value, range 1 to 64 PROPOSED). Hitting the cap is a failure that goes to the review queue or the Psych Ward (doc 06 loop guards), never a normal end.
- **External agents over MCP** run their own loop outside Bazaar. Bazaar bounds them with the per-token rate limits (section 5.2 and settings), not a round count.

```csharp
public interface IToolLoopGuard
{
    LoopVerdict OnRound(Guid loopId, int round, bool modelProducedFinalAnswer);   // Continue | Finished | CapReached
}
```

### 6.5 Data (migration namespace `tool-registry`)

```sql
-- src/Bazaar.Server.ToolRegistry/Data/Sql/tool-registry/0001_staged_change.sql
create table tool_registry.staged_change (
  id            uuid primary key,
  tool          text   not null,
  category      text   not null check (category = 'MutatingStaged'),
  proposer      text   not null,            -- Principal text form, kind:id
  session_id    uuid   not null,
  target_kind   text   not null,
  target_ref    text   not null,
  diff          jsonb  not null,            -- canonical diff, exactly what the owner sees
  args_hash     bytea  not null,
  taint_sources jsonb  not null,
  source_trust  text   not null default 'external',
  status        text   not null check (status in ('Pending','Approved','Applied','Rejected','Expired','Superseded')),
  approval_id   uuid,                       -- soft reference to the doc 15 approval; no FK across components
  created_at    timestamptz not null,
  expires_at    timestamptz not null,
  decided_at    timestamptz,
  applied_at    timestamptz
);
create index on tool_registry.staged_change (proposer, status);
create index on tool_registry.staged_change (status, expires_at);

-- 0002_category_policy.sql: per-install enablement of a category on a surface (settings are the source; this is the audited cache)
create table tool_registry.category_policy (
  category text not null, surface text not null, enabled boolean not null,
  changed_by text not null, changed_at timestamptz not null,
  primary key (category, surface)
);
```

Tools are registered in code at start, not stored. The registry keeps no copy of schemas in the database.

### 6.6 Settings registry rows

| Key | Type | Default | Scope | Who may change | Approval |
|---|---|---|---|---|---|
| `toolregistry.category.read_only.surfaces` | flags | all enabled surfaces | install | operator | No |
| `toolregistry.category.mutating_staged.surfaces` | flags | AgentTurn, McpAccess | install | operator | Yes, owner; cannot be widened past the staging gate |
| `toolregistry.category.dev_observability.surfaces` | flags | McpAccess, HeadlessCli | install | operator | No |
| `toolregistry.category.test_execution.enabled` | bool | false | install | owner | Yes, owner; floor in section 8 |
| `toolregistry.loop.max_rounds` | int | 8 (range 1 to 64) | install | operator | No |
| `toolregistry.staging.pending_cap_per_proposer` | int | 25 | install | operator | No (may only tighten below doc 15's approval request cap interplay) |
| `toolregistry.tool_result.max_bytes` | int | 262144 | install | operator | No |
| `toolregistry.external_exposure.default` | bool | false | install | owner | Yes, owner; per-tool opt-in stays required |

Staged-change expiry is not a new setting: it reuses `security.approval.expiry_hours` (doc 15).

### 6.7 Audit events (actor is a `Principal`)

`toolregistry.tool.registered` (actor `system:host`, once per start with the manifest hash), `toolregistry.call` (actor the caller's `Principal`; tool, category, surface, args hash, outcome), `toolregistry.call.denied` (reason from `ToolDenial`), `toolregistry.change.staged`, `toolregistry.change.approved`, `toolregistry.change.applied`, `toolregistry.change.rejected`, `toolregistry.change.expired`, `toolregistry.loop.cap_reached`, `toolregistry.category.changed`. Payloads hold ids and hashes, never content (doc 48 section 5.1).

### 6.8 Kodo health contract

Module id `tool-registry`. `IModuleHealth` per doc 46 section 10. Liveness: no I/O. Readiness: registrar finished, audit reachable. Hard dependencies: `audit`, policy and approval abstractions. Soft: `agents`, `schema-registry`. Probes (proposed catalogue additions, signed with the runtime like all probes): `toolregistry.consistency` (critical: every registered tool has a policy entry and a catalogue definition, none in two categories; this is the existing `ToolCatalogue` refusal run continuously), `toolregistry.staged_backlog` (non-critical: age of the oldest `Pending` row against the approval expiry), `toolregistry.dev_tools_exposure` (non-critical Fail if `test_execution` is enabled on an install not marked as a test install). Self-test `Quick`: register a fake tool in each category and check the surface filters. `Full`: stage and reject a change inside a rolled-back sandbox scope.

## 7. Design: McpAccess data, limits, audit and health

### 7.1 Data (migration namespace `mcp-access`)

```sql
-- src/Bazaar.Server.McpAccess/Data/Sql/mcp-access/0001_token.sql
create table mcp_access.token (
  token_id        uuid primary key,
  token_hash      bytea not null unique,                     -- SHA-256 with server pepper; plaintext is never stored
  label           text  not null,
  issued_by       text  not null check (issued_by like 'user:%'),   -- always a human
  principal       text  not null,                             -- service:mcp.<tokenId> or agent:<id>
  principal_kind  text  not null check (principal_kind in ('service','agent')),
  ceiling         text  not null default 'below-owner' check (ceiling = 'below-owner'),
  bound_os_user   text,
  transport_id    text  not null,
  issued_at       timestamptz not null,
  expires_at      timestamptz not null,
  revoked_at      timestamptz,
  revoked_by      text,
  revoke_reason   text,
  last_used_at    timestamptz,
  check (principal not like 'user:%' and principal not like 'system:%')
);
create table mcp_access.token_scope (
  token_id uuid not null references mcp_access.token(token_id),
  capability text not null, resource text not null,
  primary key (token_id, capability, resource),
  check (capability not like 'admin.%' and capability not like 'approval.%')
);

-- 0002_token_rate.sql: windowed counters for the per-token buckets (doc 15 rate_bucket semantics, keyed by Principal text)
create table mcp_access.token_rate (
  token_id uuid not null, bucket text not null check (bucket in ('actions','mutating','staged_pending')),
  window_start timestamptz not null, count int not null,
  primary key (token_id, bucket, window_start)
);
```

The CHECK constraints are the second enforcement of the ceiling; the code that builds the scope set is the first (the double enforcement is Bazaar's own design, PROPOSED, not from Resibo).

### 7.2 Rate limits (tighter than a human's)

Two independent buckets per token, defaults from Resibo (`v3-deepdive-55` section 7, `max_actions_per_minute` 30 and `max_mutating_actions_per_hour` 20): 30 calls per minute and 20 mutating calls per hour. The mutating cap triggers independently of the general cap. A third bound caps pending staged changes per token. On a breach the call is refused with `RateLimited`, audited, and a repeat within the window freezes the session and notifies the owner. These are quotas, not money budgets (Resource Economy stays REJECTED). They sit beside doc 15's per-agent approval request cap (10 per hour), which still applies to the apply requests a token's staged changes create.

### 7.3 Settings registry rows

| Key | Type | Default | Scope | Who may change | Approval |
|---|---|---|---|---|---|
| `mcp.enabled` | bool | false | install | owner | Yes, owner |
| `mcp.token.default_lifetime_days` | int | 30 | install | operator | No |
| `mcp.token.max_lifetime_days` | int | 90 | install | operator | No (may only lower) |
| `mcp.token.max_active` | int | 20 | install | operator | No |
| `mcp.token.allow_agent_principal` | bool | false | install | owner | Yes, owner |
| `mcp.token.require_os_user_binding` | bool | true | install | operator | Floor: stays true on shared hosts |
| `mcp.rate.actions_per_minute` | int | 30 | token | operator | No (may lower; raising needs the owner) |
| `mcp.rate.mutating_per_hour` | int | 20 | token | operator | No (may lower; raising needs the owner) |
| `mcp.session.idle_timeout_min` | int | 30 | install | operator | No |
| `mcp.session.max_concurrent_per_token` | int | 2 | install | operator | No |
| `mcp.protocol.supported_versions` | list | newest stable, one prior | install | operator | No |
| `mcp.sampling.allowed` | bool | false | install | owner | Yes, owner; counsel note (server-initiated model requests) |
| `mcp.dev_seed.enabled` | bool | false | install | owner | Yes, owner; refused on a production install |

### 7.4 Audit events

`mcp.token.issued` (actor `user:<issuer>`; token id, kind, scope list, expiry, transport), `mcp.token.revoked`, `mcp.token.expired` (actor `system:mcp-access`), `mcp.auth.failed` (no token id when unknown; peer, transport; rate-limited itself so it cannot flood the chain), `mcp.session.opened`, `mcp.session.closed`, `mcp.call` (actor `service:mcp.<tokenId>` or `agent:<id>`; tool, category, args hash, object ids touched, outcome), `mcp.call.denied`, `mcp.rate.limited`, `mcp.session.frozen`. Reads follow doc 48 section 5.1: one compact row with a range descriptor and count for bulk reads, plus a row per personal object. The audit chain holds no token secret and no result content.

### 7.5 DevSeed: the conflicting variant as a separate module

Resibo has a path for an unattended, fully AI-developed install where no human exists to click "issue" (`docs/MAINTENANCE.md` section on the dev agent token, `core/agent_control/CLAUDE.md`): a seed step issues a `staff`-capped token with `issued_by="setup-dev-bootstrap"`, scoped to read, staged and dev-observability but never test-execution, and writes the plaintext to a file in the install config directory. That conflicts with Bazaar's rule that a token is always human-issued, so it is a separate module (`mcp-dev-seed`), not a mode of the core.

- It loads only when `mcp.dev_seed.enabled` is true **and** the install is a development or sandbox install. How an install is marked as such is OPEN (section 15); until it is settled the module refuses to start.
- It issues at most one token, as `system:setup-dev` (a `Principal` of kind `system`). The core's `issued_by like 'user:%'` CHECK would reject that, so the module writes to its own table `mcp_access_devseed.seed_token` and the core accepts it only through a narrow `IDevSeedTokenSource` that the core consults when the module is present. The seed token still obeys every ceiling, the taint rule and the rate limits. It is never `agent` kind.
- The plaintext goes to a file outside the repository with owner-only permissions. The file path and the seed are audited (`mcp.devseed.issued`). `TEST_EXECUTION` is never in its scope.
- Owner decision needed before this module is built (section 15).

### 7.6 Kodo health contract

Module id `mcp-access`. Readiness: at least one transport running, token store reachable, `tool-registry` ready. Hard dependencies: `tool-registry`, `audit`, `remote`, `identity`. Soft: `kodo`, `test-orchestration`, `gc`. Probes: `mcp.transport.<id>` (non-critical: listener up, bind permissions correct), `mcp.tokens.expiring` (non-critical: tokens expiring within 7 days, so the owner is told), `mcp.auth.failure_rate` (non-critical, drift-sensitive), `mcp.sessions.stuck` (non-critical). Self-test `Quick`: authenticate a throwaway token against the in-memory store. `Full`: issue, use, revoke and verify refusal inside a sandbox scope, with `LeftNoResidue` true.

## 8. Test orchestration sub-API (DEV tools, off in production)

Resibo's Test Orchestration (`core/agent_control/test_orchestration/CLAUDE.md`, `v3-deepdive-56`) makes complex tests callable by an agent and returns a structured result: runners for crash isolation, environment reset, pipeline integration and a bench suite, with a result status that separates `FAILED` from `UNAVAILABLE` ("this environment cannot run the test" is not the finding "the system misbehaved"). It does not replace ordinary unit tests.

In Bazaar this is `Bazaar.Server.TestOrchestration` with its own Abstractions, so the runner contract can be implemented by any module. It **wraps** existing capability and implements none of it again:

| Runner id | Wraps | Category |
|---|---|---|
| `checks.run` | Doc 17's server-owned `run_checks` verification in a fresh clone | `TestExecution` |
| `kodo.selftest` | `IModuleHealth.SelfTestAsync` (`Quick` or `Full`) per doc 46 section 10 | `DevObservability` for `Quick`, `TestExecution` for `Full` |
| `kodo.fault` | Kodo's fault harness and chaos mode (doc 46 section 17), only when `kodo.chaos.allowed` is true | `TestExecution` |
| `company.reset` | Resets the doc 48 section 2 fake-data test company to its template | `TestExecution` |
| `pipeline.replay` | Replays recorded fixtures through a pipeline in the test company (doc 34) | `TestExecution` |

```csharp
public enum TestStatus { Pending, Running, Passed, Failed, Unavailable }
public sealed record TestSpec(string Runner, string TargetCompany, JsonElement Options);
public sealed record TestResult(Guid RunId, string Runner, TestStatus Status, DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt, string Detail, JsonElement Findings, IReadOnlyList<string> Warnings);
public interface ITestRunner { string Id { get; } ToolCategory Category { get; } Task<TestResult> RunAsync(TestSpec spec, CancellationToken ct); }
```

**Gates, all required:** `toolregistry.category.test_execution.enabled` (default false); the token holds a `test.execute` scope that is never in the seed or a default template; the target must be a doc 48 fake-data test company or a sandbox, never the real company (a floor, not a setting); and the kill switch freezes it. A runner that cannot run reports `Unavailable`, not `Failed`. A synthetic fixture that looks like real data raises a warning and does not fail the run, as in Resibo, because that check is a heuristic. `DEV_OBSERVABILITY` tools (Kodo status, logs, run status, dependency versions, a history narrative) are separate, read-only, and available to tokens with `dev.observe`, and a test asserts they have no side effects.

Module id `test-orchestration`. Hard dependency: `tool-registry`. Soft: `kodo`, `company-build` (doc 17 checks), `agent-safety`. Kodo probes: `testorch.runners` (non-critical: each registered runner reports available or unavailable). Settings: `testorch.max_concurrent_runs` (default 1), `testorch.run_timeout_min` (default 30). Audit: `testorch.run.started`, `.finished` with the runner id, the target company id and the status.

## 9. Optional: Bazaar.Server.SchemaRegistry (Resibo's Architect registry)

Resibo's Architect API (`v3-deepdive-26`, `v3-deepdive-40`) is a single registry for typed data definitions (taxonomy categories, reference identifier types, flag types) with local and global layers, an explicit sharing-consent gate, and an LLM-prescreen then human-review moderation queue. Resibo's rule 7 makes it the only place such types may be defined. Bazaar keeps schemas in settings and component manifests (CONVERGENCE, row on Architect), so adopting the rule as a rule is a conflict and is **not** done. The optional module offers the mechanism without the mandate, per the owner's direction.

**Overlap checked.** Doc 15 already specifies registered hand-over schemas (`mail_triage`, the community hand-over) and an `IHandoverValidator`, and its work package 5 includes a "schema registry". `SchemaRegistry` is that registry. If doc 15's package 5 is built first, this module extends it through its Abstractions. If this module is built first, it supplies the registry and package 5 consumes it. Either way there is one registry, never two. Doc 54 (Soul Marketplace) is unread here; its user-generated, moderated, consent-gated content is the same moderation shape, so this doc proposes that the moderation queue be a shared contract (`IModerationQueue` in `SchemaRegistry.Abstractions`) and asks the owner to confirm (section 15). Nothing in this module handles Soul content.

```csharp
public enum SchemaKind { Handover, Taxonomy, ReferenceId, FlagType }
public enum SchemaLayer { Local, Shared }
public sealed record SchemaDefinition(string Id, int Version, SchemaKind Kind, SchemaLayer Layer, string JsonSchema, string OwnerModule, bool Shared);
public interface ISchemaRegistry
{
    Task<SchemaDefinition?> GetAsync(string id, int? version, CancellationToken ct);
    Task<SchemaDefinition> RegisterLocalAsync(SchemaDefinition d, Principal by, CancellationToken ct);   // no review, local only
    Task<ModerationItem> ShareAsync(string id, int version, Principal by, StepUpProof? stepUp, CancellationToken ct); // the explicit consent step
}
public interface IModerationQueue
{
    Task<ModerationItem> SubmitAsync(ModerationSubmission s, CancellationToken ct);   // runs prescreen, then waits for a human decision
    Task DecideAsync(Guid itemId, ModerationDecision decision, Principal by, CancellationToken ct);
}
```

What is taken, kept narrow: the local layer is free to write (Tier 0, a private area); promotion to `Shared` happens only through an explicit share act and never automatically; a shared definition passes prescreen, which is a no-tools gateway call over redacted content, then a human decision; the human decision uses the approval service, so it is signed and audited like any other. A direct owner path to `Shared` still runs prescreen (a malformed definition has the same cost whoever submits it) and skips a second human review, as Resibo's staff path does (`v3-deepdive-40` section 3.2), because the owner's own decision is the review. Not taken: the Corporation, Branch and Franchiser model, Wikidata bootstrap and Curate's cleanup passes, which are Resibo's receipt domain.

Tables (namespace `schema-registry`): `schema_registry.definition (id, version, kind, layer, json_schema, owner_module, created_by, created_at)` and `schema_registry.moderation_item (id, definition_id, submitted_by, status, prescreen_result jsonb, approval_id, created_at, decided_at)`. Settings: `schemareg.enabled` (default false, owner), `schemareg.share.enabled` (default false, owner, counsel note because sharing leaves the install), `schemareg.prescreen.mode` (enum `off`, `ask`; default `ask`). Audit: `schemareg.definition.registered`, `.shared`, `.moderation.decided`. Kodo probe: `schemareg.queue_age` (non-critical). Hard dependency: `audit`. Soft: `tool-registry`, `gateway`, approvals. The full design is not done here (section 14, work packages 17 to 19).

## 10. What is taken from Resibo, and what changes

| Resibo source | Taken | Changed for Bazaar, and why |
|---|---|---|
| `docs/apis/v3-deepdive-55-agent-control-api.md` §1, §3 (tokens: human-issued, scoped, revocable, never owner) | Human-issued token, ceiling below the owner, hash-only storage, shown once, idempotent revoke, immediate effect | Resibo caps at `staff` role. Bazaar has custom roles (doc 48), so the cap is a scope intersection minus an owner-only set. `issued_by` is a `user` Principal and calls are `service` or `agent` Principals (4.2), not a `token_id` string |
| §4 (MCP server, curated tool set), §5 (headless CLI) | Two thin front ends, one server core | Resibo's gRPC core becomes `IAgentAccessService`. Tool set is built from the registry, not hand-listed. Transports are provider modules |
| §6 (audit every action), §7 (30 per minute, 20 mutating per hour) | Both, with the same defaults | Audit writes `Principal` and follows doc 48's by-id rule. Limits are settings (configurability rule) |
| `core/agent_control/CLAUDE.md` (dependency-free JSON-RPC, "unavailable callee returns an error, never plausible data") | Dependency-free protocol layer; unavailable tools return `Unavailable`, never invented data | SDK wrapper is a provider module, not baked in |
| `core/agent_control/dev_token_gateway.py` | The idea of a seeded token for an unattended developer install | Conflicts with human-issued rule, so it is the separate `mcp-dev-seed` module with extra gates (7.5) |
| `core/agent_control/test_orchestration/` | Runner contract, `Unavailable` vs `Failed`, no replacement of unit tests | Wraps Kodo, doc 17 checks and the doc 48 test company instead of reimplementing; gated off by default (section 8) |
| `v3-deepdive-07-tool-call-api.md` §3.3 (agentic loop, `max_rounds`) | Same loop shape; cap is a failure, not a normal end | Bazaar's turn loop already has it (`runtime.turn_step_cap`); new cap only for non-turn loops |
| §4 (four categories; no `MUTATING_DIRECT`; staged tools) | Four categories, closed enum, staging into a review gate | Staged tools do **not** self-apply: the review gate is the owner-signed approval (6.3) because Bazaar has live human approval and Tier 2 rules |
| §3.4 (do not offer an always-failing tool), `core/tool_call/registry.py` (one `check`, fail closed, no caller-asserted role, surface gate before role gate) | All of it | Role becomes token scope plus issuer grants |
| §8 (enumerated writable fields) | Enumerated allowlist per staged tool | Fields owned by each target module |
| `v3-deepdive-26-architect-api.md`, `v3-deepdive-40-temporal-learning.md` | Local and shared layers, explicit share consent, prescreen then review | Optional module; rule 7 and the receipt domain model not adopted (section 9) |

Not taken: Resibo's SQLite store for tokens (Bazaar uses Postgres per doc 04), its gRPC and protobuf surface (the wire is JSON via `IWireCodec`), `FrozenDict` (a Python concern; the C# equivalent is `ImmutableDictionary` and records, already the code's style), and the `client` role question (moot with scopes).

## 11. Interaction with existing modules, and conflicts found

1. **Policy and approval code is not yet an abstractions project.** `ToolPolicyEngine`, `ToolTier`, `CapabilityClass`, `SessionRoles` and the approval service live in `Bazaar.Server` (implementation project). A module may not reference `Bazaar.Server` (architecture rule). `ToolRegistry.Abstractions` therefore needs a precursor: extract a security abstractions project (name OPEN) holding those contracts. `Bazaar.Audit.Abstractions` and `Bazaar.Approvals.Abstractions` already set the pattern. This is work package 1 and a blocker.
2. **Existing tool types are reused, not duplicated** (section 6.1). `Bazaar.Server.Agents.Abstractions` keeps `ITool`, `ToolRegistration` and the `ToolRegistry` class. `ToolRegistry` module decorates them with `ToolSpec`. Agents does not depend on the registry module.
3. **Approvals and sessions are agent-keyed today.** `ApprovalRequest.AgentId` and `session_taint.agent_id` assume an agent. A `service` principal needs the Principal-keyed form that BAZAAR-COMPONENTS 4.2 introduces. Until it lands, this design cannot create approvals or sessions for `service:` callers. The same applies to `rate_bucket.agent_id`.
4. **Doc 48 section 4** says "Principal kinds are unchanged: human, agent, service". Doc 4.2 supersedes it (`user`, `agent`, `service`, `system`). This doc uses 4.2.
5. **Doc 17's "MCP" row** (Bazaar tools for the in-VM coding agent) and **doc 20's adapter tools "as an MCP server"** overlap this design. Proposal: both become surfaces of the same registry (`Harness`, `EngineAdapter`), engine tool risk classes `read`, `write`, `exec` map to `ReadOnly`, `MutatingStaged` (the git PR is the review gate) and `TestExecution` or Tier-2 `Exec`, and the harness may later reach `McpAccess` over a guest-channel transport provider. Doc 20's own contract is not changed by this doc. Needs owner confirmation (section 15).
6. **Doc 17 P1** (repo-supplied MCP server definitions are never auto-loaded) is a statement about Bazaar as an MCP *client*. This module is an MCP *server* only and adds no client behaviour.
7. **Doc 48 section 1 permission classes** have no "external tool exposure" class. A module that wants a tool visible to MCP sets `ExternallyExposed` in its manifest, and the owner sees the tool in the install permission card. Adding a class to doc 48's vocabulary is OPEN.
8. **Doc 15** reader and actor sessions: an MCP session is a hard-tainted session with no reader hand-over, so it can use read tools and stage proposals but holds no credentials, mounts or leases. It is never an actor session in the doc 15 sense.
9. **Kodo** (doc 46): this doc adds probes and a contract per module, and uses Kodo's `IModuleHealth` and chaos gate; it does not touch Kodo's API.
10. **Doc 51 (web admin):** token management is client-only here. A read-only web admin section would be a port the module implements (OPEN, not required).
11. **Doc 72 and doc 54 were not in this checkout.** Their effect on section 6.3 (DM grants) and section 9 (shared moderation) is unverified.

## 12. Security and privacy

**What an external agent can see.** Only what its token's scopes name, intersected with its issuer's current grants:

- Kanban and Roadmap items, Vault pages the issuer can read and that are not in an agent's private area, build and check results, Kodo status and logs (`dev.observe`), the tool manifest, its own staged changes and its own audit trail.
- **GCs.** A token reads a GC only through an explicit `data.read`/`gc:<id>` scope that the issuer holds. **DMs and any GC whose members exclude the issuer are never readable** unless an access grant under doc 72 exists for that token's `Principal` (doc 72 unverified). Even then the owner's read-all rule is not extended to tokens. A thinking GC is never exposed (doc 15 C11, extended to external readers).
- **Never exposed, regardless of scope:** secrets, key material, Passport contents, mounts and cookie profiles, the audit log beyond the token's own entries, other tokens, email bodies (reader sessions only, doc 22), approval cards, owner device keys, personal fields of other people without the per-person decryption path.

**Floors (not settings):** no code path maps a token to the owner; a token cannot issue, list or revoke other tokens; no approval, `admin.*`, role, grant, secret or kill-switch capability is ever in a token; MCP sessions are born hard-tainted and everything they write is `external`; a staged change is applied only through a signed approval bound to the args hash; every call, including denials of privileged tools and failed authentication, is audited with a `Principal`; unresolvable authority means deny; token plaintext is never stored or logged; `TestExecution` never targets a real company.

**Threats and mitigations.**
- *Stolen token:* hash-only storage, short expiry, OS-account binding on local transports, per-token rate limits, immediate revocation, owner-visible activity.
- *Prompt injection reaching the external agent through Bazaar output:* results carry the `trust` label of their source, quoted external text is marked, email and web content are never in results, and `tools/list` descriptions come only from registered code, never from a client or a repository.
- *Confused deputy and approval flooding:* proposals create approvals through the same cap (10 per hour per proposer) and a per-token pending cap; the card shows the exact diff and the taint source `mcp:<tokenId>`.
- *Exfiltration:* read scope is explicit, reads are audited by object id, result size is capped, and there is no outbound tool on the MCP surface (no post, send, push or spend; those would be staged proposals at most).
- *Server-initiated model calls:* the server does not send sampling or elicitation requests to clients by default.
- *Resource exhaustion:* per-token concurrency, frame size and result caps.

**Privacy.** Tokens and call metadata are personal data of the issuing human only as `user:<id>` text; audit fields follow the per-person encryption rule (doc 48 section 5.5). Erasure of the issuer's key does not break the chain. The issuance UI warns that an MCP client may send results to its own model provider, which is outside Bazaar's control. That warning cannot be switched off (legal and privacy exposure setting, most protective option).

## 13. Testing

xUnit v3 everywhere first (CLAUDE.md). Real Postgres via the existing fixture for token, staging and audit tests. `Avalonia.Headless` for the token pane.

- **Ceiling.** No code path yields owner authority for any issuer; DB CHECKs reject `admin.*` and `approval.*` scopes, a non-`user` issuer and a non-`below-owner` ceiling (seeded-violation test, as in the existing architecture tests); a token cannot issue or list tokens.
- **Revocation.** The next call after revoke fails; live sessions on every transport close; revoke is idempotent and audited once; an expired token fails; reuse of an unknown secret is one generic refusal.
- **Rate limits.** The mutating cap triggers independently of the general cap; a breach audits and, repeated, freezes the session.
- **Category enforcement.** A `ReadOnly` tool has no write path; a `MutatingStaged` tool cannot reach its target without a signed approval; no tool is in two categories; the enum's membership is asserted; an inconsistent category and `CapabilityClass` pair fails at registration.
- **Surface and ordering.** A tool not offered on a surface is unreachable and reports `NotOfferedOnSurface` (not `CapabilityMissing`) even to a caller with the right scope; the manifest and dispatch share one check; every resolver failure is a denial.
- **Loop.** The non-turn loop ends on a final answer; `max_rounds` is enforced and routes to failure; agent turns use `runtime.turn_step_cap` only.
- **Taint.** An MCP session starts `Hard`; a Tier 1 tool escalates; written content carries `source_trust = external`.
- **Staging.** Approve applies once; changed args void the approval; expiry marks the change expired; the staged-change cap holds.
- **Privacy.** A GC outside the token's scope and a DM without a grant return nothing and write a denied audit row.
- **Dev tools.** `TestExecution` is absent from `tools/list` by default, refused against a real company even when enabled, and frozen by the kill switch; `DevObservability` tools are checked for no side effects with a recording fake.
- **Transports.** Local socket peer-account binding (fake peer); the bridge reads the token only from environment or file; frames over the size cap are refused.
- **Contract.** `ModuleHealthContractTests<T>` (doc 46) for each module; red-team corpus (doc 15) extended with tool-description poisoning and hidden-Unicode arguments, expecting no side effect without approval.

## 14. Work packages (small, independent, each with tests)

Each is its own project or file group and its own PR, with a `Docs section this implements:` line.

1. **Security abstractions extraction** (blocker): move `ToolTier`, `CapabilityClass`, `SessionRoles`, `IToolPolicyEngine`, `DispatchContext`, `PolicyDecision` and the approval request contract into an abstractions project; `Bazaar.Server` references it. No deps.
2. **`ToolRegistry.Abstractions`**: `ToolCategory`, `ToolSurface`, `ToolSpec`, `IToolDirectory`, `IStagingGate`, `IStagedChangeApplier`, `IToolLoopGuard`. Deps: 1.
3. **`ToolRegistry` core**: registrar, consistency validation, `Check` ordering, availability, directory filtered manifest. Deps: 2.
4. **Staging gate and migration** (`staged_change`, approval request, applier dispatch, expiry). Deps: 3, doc 15 approval service.
5. **Loop guard** and the `max_rounds` setting. Deps: 2.
6. **ToolRegistry settings, audit events and Kodo health contract.** Deps: 3.
7. **`McpAccess.Abstractions`** (service, token, transport contracts). Deps: 2.
8. **Token service and migration**: issue, hash, authenticate, revoke, CHECK constraints, NOTIFY on revoke. Deps: 7, Principal in Core.
9. **Rate limiting** (token buckets, migration, tests). Deps: 8.
10. **Protocol layer**: dependency-free JSON-RPC and MCP server role, version negotiation, frame limits. Deps: 7.
11. **`McpAccess` core**: sessions, born-hard-taint, call path, audit, health contract. Deps: 3, 8, 9, 10.
12. **Local socket provider** (Windows pipe and Linux socket behind `IMcpTransport`, peer binding). Deps: 10.
13. **Stdio bridge and CLI executable.** Deps: 12.
14. **`Bazaar.Client.McpAccess` pane**: list, activity, revoke; then the issue wizard with step-up; headless tests. Deps: 8, client module registry.
15. **`TestOrchestration` module** with the `checks.run` and `kodo.selftest` runners first. Deps: 3, docs 17 and 46 modules.
16. **`McpAccess.DevSeed`** (only after the owner answers question 4). Deps: 8, 11.
17. **`SchemaRegistry.Abstractions` and local layer.** Deps: 3.
18. **Share gate and moderation queue** (prescreen, approval). Deps: 17, 4, gateway.
19. **Engine and harness surfaces** (registration adapters for docs 17 and 20). Deps: 3, and the owner's answer to question 5.

## 15. Open questions

1. **Owner-only set.** Confirm the fixed list in 5.2 (approvals, `admin.*`, role and grant changes, secrets, token issuing, audit export, kill switch, Tier 3) as the capabilities a token can never hold. Default: yes.
2. **Principal kind `agent` for a token.** Default off. Should it exist at all, given it lets an outside process act under a company member's name?
3. **Source trust of MCP writes.** Default `external` (hard taint for any Bazaar agent that reads it). Should a human-driven token be allowed a softer tag, given the human issued it? Default: no.
4. **DevSeed and a development install marker.** How is an install marked as a dev or test install, and may a `system:setup-dev` seed token exist given the human-issued rule? Default: module not built until answered.
5. **Doc 17 and doc 20 overlap.** Confirm that the harness's MCP row and the adapters' MCP exposure become surfaces of this registry, and that the in-VM harness may use `McpAccess` over a guest-channel transport.
6. **Delegated reviewers for staged changes.** Default: only the owner decides, through the signed approval. May an owner delegate lower-risk staged targets to a reviewer role (doc 48 section 4)? This needs doc 15's no-agent-approval floor kept.
7. **Sampling and elicitation.** Default: the server never asks the client to run a model or prompt the user. Confirm.
8. **Network transport.** Should an MCP transport over Conduit (doc 39) ever be offered? It would be an API, not a web client, but it widens exposure. Default: no, local only.
9. **Android token minting.** Default: view and revoke only. Confirm.
10. **Class name clash.** Keep `Bazaar.Server.ToolRegistry` beside `Bazaar.Server.Agents.Turns.Tools.ToolRegistry`, or rename the module (for example `Bazaar.Server.ToolDirectory`)?
11. **Doc 54 moderation.** Confirm that SchemaRegistry's `IModerationQueue` should be the shared queue for the Soul Marketplace once doc 54 is read.
12. **Migration namespace spelling.** Component ids contain hyphens (`tool-registry`); confirm whether the namespace folder and schema use underscores (`tool_registry`), as this doc assumes.
13. **Doc 48 permission classes.** Add "external tool exposure" as a class, or treat `ExternallyExposed` as part of the existing "UI pane, background job" family? Default: add a class.
