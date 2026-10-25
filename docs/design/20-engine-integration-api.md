# Engine Integration API (Godot and Roblox adapters)

Status: DRAFT for owner review (2026-10-25) · Owner questions: 13
Depends on: none yet (`docs/deep-dives/Bazaar.md`, `Bazaar.Soul.md`, `Bazaar.Windows.md`, `Bazaar.Server.md`) · Used by: none yet (Playtest Mode, Kanban, Constellation to follow)

## Purpose
Engines (Godot, Roblox Studio, later others) become plugins to Bazaar.Server. Each engine is an out-of-process adapter that declares capabilities and, optionally, contributes client UI panes, so the company and its clients never hard-code an engine.

## Decided inputs
- **Engine Integration API: DECIDED (direction)**, Roblox first-class (`Bazaar.md` §4).
- **Roblox Studio runs only on the human's Bazaar.Windows desktop**; agents do headless Luau static checks and file sync (`Bazaar.Soul.md` §2, 2026-10-24).
- **Godot has `--headless`**; compile, test and export run without a GUI (`Bazaar.Soul.md` §2).
- **Server does all compute; clients render; no web client** (`Bazaar.Server.md` §1).
- **Engine code runs in the engine's language** (GDScript/C# for Godot, Luau for Studio). Bazaar's own code stays .NET (`Bazaar.Server.md` §4 exceptions).
- **Playtest Mode is in v1 scope**, human-assisted on Windows (`Bazaar.md` §4, 2026-10-24).
- **Clients: Avalonia; UI tested with `Avalonia.Headless`; xUnit v3** (`CLAUDE.md`).
- **Limits are quotas and the Work-Life Simulation**, not money budgets (`Bazaar.md`).

## Design

### Components
| Component | Runs on | Responsibility |
|---|---|---|
| Engine adapter (child process) | Server host (headless VM) or Bazaar.Windows | Owns one engine binary; reports capabilities; executes commands |
| Studio bridge | Bazaar.Client.Windows [PROPOSED] | Starts the official Studio MCP server (stdio) next to the human's Studio and relays calls to the server |
| Capability registry | Bazaar.Server | Maps capabilities to adapters; answers "who supports X?" |
| Pane renderer | Each client | Renders server-sent pane descriptors; loads an optional in-process pane plugin |

### Capabilities [PROPOSED]
`scenes`, `assets`, `animation`, `timeline`, `debugging`, `hot_reload`, `script_check`, `import`, `export`, `test`, `playtest`. Each adapter declares its subset in a manifest (engine id, version, binary path, required build variant, `needs_gui_host`). The UI and agents query by capability, never by engine id.

### Adapter contract [PROPOSED]
- **Lifecycle:** JSON-RPC 2.0 over the child's stdio (`open`, `command`, `cancel`, `close`, `health`). Streaming output as notifications. Typed errors.
- **Agent tools:** each adapter exposes its tools as an MCP server. The server's gateway forwards an agent's tool call only if that agent holds the grant. Tool names carry a risk class: `read`, `write`, `exec`.
- **Panes:** the adapter returns `PaneDescriptor` records; the server pushes them to clients as declarative JSON.

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
2. The server sends the descriptor plus snapshots and deltas over the client connection.
3. The client maps `Kind` to a view model. If an in-process plugin for that kind is installed, it is loaded with an `AssemblyLoadContext` and provides the view; otherwise a generic tree, log or form view renders the model.
4. The server never loads UI code. A new engine GUI is a client plugin assembly that registers its kinds; no server change is needed.
5. v1 loads only first-party plugins. Third-party signing is a question (Q10).

### Godot adapter
- **Build check first:** run the binary with `--version` and confirm it is an editor build, that the variant (standard or .NET) matches the project, and that export templates for the preset exist.
- **Commands:** `--headless --import`; `--headless --check-only --script <file>`; `--headless --script <runner>` for tests; `--headless --export-release "<preset>" <dir>` (the output directory must exist; paths resolve against `project.godot`).
- **Exit-code validation:** Godot ignores unknown flags silently. The adapter treats a non-zero exit as failure, parses `ERROR` and `SCRIPT ERROR` lines from output, and runs a canary (a fixture script with a known error, which must fail) at startup.
- **Scene diffing [PROPOSED]:** `.tscn` and `.tres` are text (from memory, not checked); diff by node path and property, not by line.
- **Version pin:** **Godot 4.7.2**, maintenance release dated 18 August 2026, listed under the stable Release category on godotengine.org/news/release (checked 2026-10-25). Whether a .NET build of 4.7.2 is published was not checked.
- **C#:** desktop only; no web export; Android and iOS experimental (Godot C# docs, checked).

### Roblox adapter
- **Official Studio MCP** (checked 2026-10-25): 26 tools, including `script_read`, `multi_edit`, `execute_luau`, `start_stop_play`, `get_console_output`, `screen_capture`, `inspect_instance`, `http_get`, `list_roblox_studios`. Each call targets a `studio_id`. Enabled in Studio under Assistant → Manage MCP Servers. No authentication or permission prompt is described; the page warns that connected clients can read and modify open places.
- **Risk classes [PROPOSED]:** `read` = `script_read`, `script_search`, `script_grep`, `inspect_instance`, `search_game_tree`, `get_console_output`, `get_studio_state`. `write` = `multi_edit`, `insert_asset`, `upload_image`, `store_image`, asset generators. `exec` = `execute_luau`, `start_stop_play`, input tools, `http_get`.
- **Headless path:** `luau-analyze`, Selene and StyLua on the server VM; Rojo for file sync. Versions not verified (GitHub access to these repos was not attached this session).
- **Open Cloud:** REST with API key or OAuth. Script editing and Luau execution are not confirmed on its pages. Out of v1 unless Q6 says otherwise.

### Language boundaries
Bazaar never generates or ships engine-side code in v1. GDScript, C# and Luau appear only as project content the agents edit through the adapter. A Godot editor add-on or Studio plugin is added only if the official tool surface cannot do a needed task.

## Data and interfaces
- `engine_projects(id, adapter_id, root_path, engine_version, build_variant, gui_host_id)`
- `engine_grants(agent_id, adapter_id, tool_name, risk_class, granted_by, granted_at)`; default deny for `write` and `exec`.
- Audit: every `write` and `exec` call appended to the GC of the requesting agent (events are not a substitute for the GC record).
- Adapter messages: JSON-RPC envelopes with `{project_id, command_id, seq}`; sequence numbers follow the per-GC pattern in `implementation-plan.md`.

## Behaviour and edge cases
- **Adapter crash or hang:** the server kills the child after a timeout, marks the session `failed`, and reports the last output. Commands are idempotent or carry a `command_id` so retries are safe.
- **Studio not open or bridge offline:** Roblox capabilities report `unavailable` with a reason; headless checks keep working.
- **Multiple open Studios:** the bridge requires an explicit `studio_id`; no default.
- **Concurrency:** one writer per project; reads may run in parallel. Godot runs are serialised per project directory (the editor locks resources).
- **Untrusted content:** `script_read` output and console text are data. Place content can contain injected instructions; never auto-load MCP server definitions or agent instruction files from a project.
- **Exfiltration:** `http_get` and `execute_luau` can reach the network from inside a place. Default deny; allow-listed hosts only [PROPOSED].
- **Offline clients:** pane snapshots are replayed on reconnect; generic fallback renders if a plugin is missing.

## Testing
- **Fake adapter:** a test child process speaking the JSON-RPC contract; covers timeouts, crashes and cancellation (xUnit v3).
- **Godot, real binary in CI:** the 4.7.2 headless runs on hosted runners against a fixture project: import, `--check-only` pass and fail, export with a preset. Canary test fails the build if the flag is silently ignored.
- **Luau:** `luau-analyze` and Selene on fixtures, no Studio.
- **Studio MCP:** recorded transcripts replayed through the bridge; no live Studio in CI.
- **Panes:** `Avalonia.Headless` tests render a descriptor through the generic and plugin paths.
- **Manual on Bazaar.Windows:** Studio bridge, play-mode checks and Playtest Mode (human-assisted), recorded as a checklist.

## Work packages
1. `Bazaar.Contracts`: `EngineManifest`, `EngineCapability`, `PaneDescriptor`, JSON-RPC envelope types. (Wave 1.)
2. Capability registry and grant table with migrations and real-Postgres tests. Depends on 1 and the migration runner.
3. Adapter host: child-process supervision, timeouts, cancellation, fake adapter. Depends on 1.
4. Gateway bridge: forwards granted MCP tool calls, audits to GC. Depends on 2, 3.
5. Godot adapter: build check, commands, canary, output parser. Depends on 3.
6. Godot scene-diff library for `.tscn`/`.tres`. Independent.
7. Luau static-check adapter (`luau-analyze`, Selene, StyLua). Depends on 3.
8. Studio bridge in Bazaar.Client.Windows with recorded-transcript tests. Depends on 3, 4.
9. Pane descriptor renderer plus plugin loader in `Bazaar.Client.Core`. Depends on 1.
10. Rojo sync wrapper. Depends on 7.

## Conflicts
1. **Transport:** the research note proposes "JSON-RPC or gRPC" for lifecycle; `Bazaar.Server.md` §4 says gRPC is not recommended for v1. This draft uses JSON-RPC over stdio. Owner decides (Q3).
2. **Compute location:** Studio runs on the desktop client, so Roblox tool execution happens on a client. This conflicts with "server does all compute" (`Bazaar.Server.md`). Mitigation: the client is only a relay; the server owns grants and audit. Owner decides (Q2).
3. **Playtest Mode v1 vs headless:** Studio playtesting needs the human's desktop, and Godot headless gives no visuals. v1 Playtest is effectively human-run, which narrows "agents playtest".
4. **Timeline** appears in the decided list of capabilities but no source defines it.
5. **Studio MCP authentication:** none described, and clients can write to open places. The Soul doc's "no official Linux client" and Studio-headless claims remain unverified (`engine_plugins.md`).
6. **Godot 4.7.x status:** a snippet site called 4.7.2 "Early Access"; the primary release feed lists it as stable. Resolved in favour of the feed; the snippet is discarded.
7. **Counsel:** Roblox Creator terms and automation policy are unread; Godot community posting rules are a secondary report only (needs counsel).

## Questions for the owner
1. **What is Timeline?** Default: engine-native animation/timeline tracks (Godot AnimationPlayer). Not the Historical Replay feature.
2. **Studio on the desktop client:** accept a Studio bridge in Bazaar.Client.Windows that relays to the server? Default: yes, with grants and audit on the server.
3. **Adapter transport:** JSON-RPC 2.0 over stdio, or gRPC? Default: JSON-RPC over stdio.
4. **Agent grants:** default deny for `write` and `exec`, granted per agent. Agree?
5. **Roblox write approval:** who approves `multi_edit` and `execute_luau`? Default: owner approves each `exec`, and agents write freely to a designated dev place only.
6. **Places in scope:** dev place only in v1, with Open Cloud publishing out of v1? Default: yes.
7. **Playtest Mode v1:** human-run on Bazaar.Windows, with agents writing test scripts? Default: yes.
8. **Godot build variant:** GDScript-first, .NET editor only on request? Default: yes.
9. **Godot test framework:** GUT or gdUnit4? Default: decide after a source check (neither checked).
10. **Pane plugin trust:** first-party only in v1, third-party later with signing? Default: first-party only.
11. **Roblox file sync:** Rojo or Roblox Script Sync beta? Default: Rojo.
12. **Godot pin policy:** pin 4.7.2 and follow maintenance releases within 4.7? Default: yes.
13. **Counsel review:** Roblox Creator terms, automation and AI-agent use, before the first Roblox run. Default: yes, before v1.
