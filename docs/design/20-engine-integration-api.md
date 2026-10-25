# Engine Integration API (Godot and Roblox adapters)

Status: RECONCILED with DECISIONS-LOG (2026-10-25) · Owner questions: 13
Depends on: `01-gc-data-model.md` (audit to the GC), `02-wire-protocol-and-sync.md` (stream registry, HTTP surface), `03-server-host-and-providers.md` (child-process hosting, status), `04-postgres-schema-and-migrations.md` (migrations, conventions), `15-security-taint-and-approvals.md` (grants, approvals, taint, audit log), `16-agent-vms-and-environments.md` (VM placement) · Used by: `15-security-taint-and-approvals.md`, `16-agent-vms-and-environments.md`, `26-fun-systems.md` (Playtest Tier A), `27-client-architecture.md`, `28-windows-client.md`, `35-spikes-plan.md` (S24)

## Purpose
Engines (Godot, Roblox Studio, later others) become plugins to Bazaar.Server. Each engine is an out-of-process adapter that declares capabilities and, optionally, contributes client UI panes, so the company and its clients never hard-code an engine.

## Decided inputs
- **Engine Integration API: DECIDED (direction)**, Roblox first-class (`Bazaar.md` §4).
- **Roblox Studio runs only on the human's Bazaar.Windows desktop**; agents do headless Luau static checks and file sync (`Bazaar.Soul.md` §2, 2026-10-24).
- **Godot has `--headless`**; compile, test and export run without a GUI (`Bazaar.Soul.md` §2).
- **Server does all compute; clients render; no web client** (`CLAUDE.md`, `Bazaar.Server.md` §1). **Ratified client-side exceptions** (owner, DECISIONS-LOG round 4, C17, 2026-10-25): NPU inference, the human's local terminal, **the Roblox Studio bridge relay**, and syncing the viewed branch. Layout stays server-side.
- **Engine code runs in the engine's language** (GDScript/C# for Godot, Luau for Studio). Bazaar's own code stays .NET (`Bazaar.Server.md` §4 exceptions).
- **Playtest Mode is in v1 scope**, human-assisted on Windows (`Bazaar.md` §4, 2026-10-24). Tier split per `26-fun-systems.md`.
- **Clients: Avalonia**, UI tested with `Avalonia.Headless`, xUnit v3. The Avalonia choice is conditional on the Monaco hosting gate (DECISIONS-LOG round 5, C26).
- **Limits are quotas and the Work-Life Simulation**, not money budgets (`Bazaar.md`). Shared rate limits come from the quota table in `15-security-taint-and-approvals.md`.
- **Taint and approvals** (DECISIONS-LOG rounds 2, 6, 7): external content taints hard; Tier 2 outward actions need owner approval on the 15 ladder; routine internal work needs no human approval (C18, owner statement).

## Design

### Components
| Component | Runs on | Responsibility |
|---|---|---|
| Engine adapter (child process) | Server host, or an agent's headless Linux VM (placement OPEN, Q14) | Owns one engine binary; reports capabilities; executes commands |
| Studio bridge | Bazaar.Client.Windows [PROPOSED] | Starts the official Studio MCP server (stdio) next to the human's Studio and relays granted calls to the server. Ratified exception (C17); keeps no grants or audit |
| Capability registry | Bazaar.Server | Maps capabilities to adapters; answers "who supports X?" |
| Tool dispatch | Bazaar.Server | Forwards an agent's granted adapter tool calls and records them (see Data) |
| Pane renderer | Each client | Renders server-sent pane descriptors; loads an optional in-process pane plugin |

### Capabilities [PROPOSED]
`scenes`, `assets`, `animation`, `timeline`, `debugging`, `hot_reload`, `script_check`, `import`, `export`, `test`, `playtest`. Each adapter declares its subset in a manifest (engine id, version, binary path, required build variant, `needs_gui_host`). The UI and agents query by capability, never by engine id. `timeline` is unconfirmed (Q1).

### Adapter contract [PROPOSED]
- **Lifecycle:** JSON-RPC 2.0 over the child's stdio (`open`, `command`, `cancel`, `close`, `health`). Streaming output as notifications. Typed errors. Transport choice is Q3.
- **Agent tools:** each adapter exposes its tools as an MCP server. Tool dispatch forwards an agent's call only if that agent holds a grant. Tool names carry a risk class: `read`, `write`, `exec`.
- **Panes:** the adapter returns `PaneDescriptor` records; the server pushes them to clients as declarative JSON on a registered stream (`pane`, doc 02). Snapshots carry a revision; they are not GC replay.

```csharp
public interface IEngineAdapter
{
    EngineManifest Manifest { get; }                  // id, version, capabilities, needsGuiHost
    Task<IEngineSession> OpenAsync(ProjectRef project, CancellationToken ct);
}
public interface IEngineSession : IAsyncDisposable
{
    Task<CommandResult> RunAsync(EngineCommand command, CancellationToken ct);
    IReadOnlyList<PaneDescriptor> Panes { get; }
}
public sealed record PaneDescriptor(string Kind, string Title, string Capability, JsonElement Model);
```

### Pane streaming and new GUIs [PROPOSED]
1. The adapter emits a `PaneDescriptor` whose `Kind` is namespaced (`godot.scene-tree`, `roblox.explorer`).
2. The server sends the descriptor plus snapshots and deltas over the client connection (doc 02's stream registry). Large payloads (for example screen captures) go over the authenticated HTTP blob surface in doc 02, never in a socket frame.
3. The client maps `Kind` to a view model. If an in-process plugin for that kind is installed, it is loaded with an `AssemblyLoadContext` and provides the view; otherwise a generic tree, log or form view renders the model.
4. The server never loads UI code. A new engine GUI is a client plugin assembly that registers its kinds; no server change is needed.
5. v1 loads only first-party plugins. Third-party signing is Q10.

### Godot adapter
- **Build check first:** run the binary with `--version` and confirm it is an editor build, that the variant (standard or .NET) matches the project, and that export templates for the preset exist.
- **Commands:** `--headless --import`; `--headless --check-only --script <file>`; `--headless --script <runner>` for tests; `--headless --export-release "<preset>" <dir>` (the output directory must exist; paths resolve against `project.godot`).
- **Exit-code validation:** Godot ignores unknown flags silently. The adapter treats a non-zero exit as failure, parses `ERROR` and `SCRIPT ERROR` lines from output, and runs a canary (a fixture script with a known error, which must fail) at startup.
- **Scene diffing [PROPOSED]:** `.tscn` and `.tres` are text (from memory, not checked); diff by node path and property, not by line.
- **Version pin:** **Godot 4.7.2**, maintenance release dated 18 August 2026, listed under the stable Release category on the official release feed (checked 2026-10-25). A snippet site's "Early Access" label was checked against the feed and discarded. Whether a .NET build of 4.7.2 is published was not checked.
- **C#:** desktop only; no web export; Android and iOS experimental (Godot C# docs, checked).

### Roblox adapter
- **Official Studio MCP** (checked 2026-10-25): 26 tools, including `script_read`, `multi_edit`, `execute_luau`, `start_stop_play`, `get_console_output`, `screen_capture`, `inspect_instance`, `http_get`, `list_roblox_studios`. Each call targets a `studio_id`. Enabled in Studio under Assistant → Manage MCP Servers. No authentication or permission prompt is described; the page warns that connected clients can read and modify open places.
- **Risk classes [PROPOSED]:** `read` = `script_read`, `script_search`, `script_grep`, `inspect_instance`, `search_game_tree`, `get_console_output`, `get_studio_state`. `write` = `multi_edit`, `insert_asset`, `upload_image`, `store_image`, asset generators. `exec` = `execute_luau`, `start_stop_play`, input tools, `http_get`.
- **Headless path:** `luau-analyze`, Selene and StyLua on the agent's Linux VM or server host; Rojo for file sync (Q11). Versions not verified (GitHub access to these repos was not attached this session).
- **Open Cloud:** REST with API key or OAuth. Script editing and Luau execution are not confirmed on its pages. Out of v1 unless Q6 says otherwise. Any credential it needs is held by the Identity Service (C05), never by an adapter.

### Language boundaries
Bazaar never generates or ships engine-side code in v1. GDScript, C# and Luau appear only as project content the agents edit through the adapter. A Godot editor add-on or Studio plugin is added only if the official tool surface cannot do a needed task.

## Data and interfaces
- `engine_projects(id, adapter_id, root_path, engine_version, build_variant, gui_host_id)`
- `engine_grants(agent_id, adapter_id, tool_name, risk_class, granted_by, granted_at)`; default deny for `write` and `exec`. Each grant is issued as an approval row in doc 15's `approval` table (one approval table, C16). Studio relay requests carry the grant reference (GrantId) and the server checks it before relaying.
- **Audit:** every `write` and `exec` call is written to doc 15's `audit_log` and recorded as a message in the requesting agent's GC (doc 01). Events are not a substitute for the GC record.
- **Adapter messages:** JSON-RPC envelopes with `{project_id, command_id, seq}`. `seq` numbers the adapter's own stream per project; GC sequence numbers belong to doc 04 and are not used here.

## Behaviour and edge cases
- **Adapter crash or hang:** the server kills the child after a timeout, marks the session `failed`, and reports the last output. Commands are idempotent or carry a `command_id` so retries are safe. Timeout values follow doc 15's quota table and doc 16's exec limits.
- **Studio not open or bridge offline:** Roblox capabilities report `unavailable` with a reason; headless checks keep working.
- **Multiple open Studios:** the bridge requires an explicit `studio_id`; no default.
- **Concurrency:** one writer per project; reads may run in parallel. Godot runs are serialised per project directory (the editor locks resources).
- **Untrusted content:** `script_read` output, console text and place content are data. Under doc 15's provenance enum, place content and console output that can carry player-entered text are `external` and taint the session hard; agent-written scripts are `agent` and taint softly. Tier 2 actions in a hard-tainted session need approval; summaries of external content go through a reader session. Never auto-load MCP server definitions or agent instruction files from a project.
- **Exfiltration:** `http_get` and `execute_luau` can reach the network from inside a place. Default deny; allow-listed hosts only [PROPOSED].
- **Offline clients:** pane snapshots carry a revision and are re-sent on reconnect; the generic fallback renders if a plugin is missing.

## Testing
- **Fake adapter:** a test child process speaking the JSON-RPC contract; covers timeouts, crashes and cancellation (xUnit v3, real Postgres for the grant tables).
- **Godot, real binary in CI:** the 4.7.2 headless runs on hosted runners against a fixture project: import, `--check-only` pass and fail, export with a preset. Canary test fails the build if the flag is silently ignored.
- **Luau:** `luau-analyze` and Selene on fixtures, no Studio.
- **Studio MCP:** recorded transcripts replayed through the bridge; no live Studio in CI.
- **Grants and audit:** a denied `write` or `exec` is refused, and every granted call appears in `audit_log` and the agent's GC.
- **Panes:** `Avalonia.Headless` tests render a descriptor through the generic and plugin paths.
- **Manual on Bazaar.Windows:** Studio bridge, play-mode checks and Playtest Tier B (human-assisted), recorded as a checklist.

## Work packages
1. `Bazaar.Contracts`: `EngineManifest`, `EngineCapability`, `PaneDescriptor`, JSON-RPC envelope types. (Wave 1.)
2. Capability registry and grant tables with migrations (doc 04's runner, `migrate` command) and real-Postgres tests. Depends on 1.
3. Adapter host: child-process supervision, timeouts, cancellation, fake adapter. Depends on 1.
4. Tool dispatch bridge: forwards granted MCP tool calls, audits to `audit_log` and the GC. Depends on 2, 3.
5. Godot adapter: build check, commands, canary, output parser. Depends on 3.
6. Godot scene-diff library for `.tscn`/`.tres`. Independent.
7. Luau static-check adapter (`luau-analyze`, Selene, StyLua). Depends on 3.
8. Studio bridge in Bazaar.Client.Windows with recorded-transcript tests. Depends on 3, 4.
9. Pane descriptor renderer plus plugin loader in `Bazaar.Client.Core`. Depends on 1.
10. Rojo sync wrapper. Depends on 7.

## Conflicts
1. **Adapter transport:** the research note proposes "JSON-RPC or gRPC" for lifecycle; `Bazaar.Server.md` §4 says gRPC is not recommended for v1. This draft uses JSON-RPC over stdio until Q3 is answered.
2. **Playtest v1 versus headless:** Godot headless gives state assertions but no visuals, and Studio playtesting needs the human's desktop. `26-fun-systems.md` splits Playtest into Tier A (agents, headless, this doc's Godot adapter) and Tier B (human on Bazaar.Windows). This doc's Q7 must match that split.
3. **Timeline** appears in the decided capability list, but no source defines it (Q1).
4. **Studio MCP authentication:** none described, and clients can write to open places. Mitigated by grants and audit on the server; the Soul doc's "no official Linux client" and Studio-headless claims remain unverified (`engine_plugins.md`).
5. **Adapter placement:** the component table is OPEN between the server host and an agent's Linux VM. If adapters run in a VM, their wire is `IGuestChannel` (doc 16), not stdio (Q14).
6. **Counsel:** Roblox Creator terms and automation policy are unread; Godot community posting rules are a secondary report only. Needs counsel (Q13); doc 36 holds Godot as Deny meanwhile.

**Cross-doc conflicts that other docs must fix:**
- `28-windows-client.md` (line on the Studio bridge): "The server ... issues a grant per call" conflicts with this doc's durable `engine_grants` rows per agent and tool. Doc 28 should reference a per-call grant ID that points to an `engine_grants` row or approval.
- `28-windows-client.md` (Q13): "every write needs owner approval" conflicts with this doc's default (Q5: agents write freely to a designated dev place). Doc 28 should follow whichever answer Q5 gets.
- `README.md` index row 20 lists doc 13 as a dependency; this doc does not use the Skimmer Bouncer. README should be corrected.

## Questions for the owner
1. **What is Timeline?** Default: engine-native animation/timeline tracks (Godot AnimationPlayer). Not the Historical Replay feature.
2. ANSWERED: Studio bridge in Bazaar.Client.Windows relaying to the server, with grants and audit on the server (DECISIONS-LOG round 4, C17).
3. **Adapter transport:** JSON-RPC 2.0 over stdio, or gRPC? Default: JSON-RPC over stdio.
4. **Agent grants:** default deny for `write` and `exec`, granted per agent as approval rows in doc 15. Agree?
5. **Roblox write approval:** which doc 15 tier applies to `multi_edit` and `execute_luau`? Default: `exec` needs owner approval each time; `write` to a designated dev place only is routine internal work (C18) and needs no approval.
6. **Places in scope:** dev place only in v1, with Open Cloud publishing out of v1? Default: yes.
7. **Playtest Mode v1:** Tier A headless checks by agents (state assertions, Luau checks) plus Tier B human-run on Bazaar.Windows, as doc 26 proposes? Default: yes.
8. **Godot build variant:** GDScript-first, .NET editor only on request? Default: yes.
9. **Godot test framework:** GUT or gdUnit4? Default: decide after a source check (neither checked).
10. **Pane plugin trust:** first-party only in v1, third-party later with signing? Default: first-party only.
11. **Roblox file sync:** Rojo or Roblox Script Sync beta? Default: Rojo.
12. **Godot pin policy:** pin 4.7.2 and follow maintenance releases within 4.7? Default: yes.
13. **Counsel review:** Roblox Creator terms, automation and AI-agent use, before the first Roblox run. Default: yes, before v1.
14. **Adapter placement:** do adapters run on the server host, or inside each agent's headless Linux VM (wired through `IGuestChannel`, doc 16)? Default: the server host for v1 headless checks, with the VM option kept open until doc 16's guest protocol is settled.
