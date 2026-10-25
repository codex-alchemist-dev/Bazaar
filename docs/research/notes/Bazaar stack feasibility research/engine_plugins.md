# Engine Integration API: Godot and Roblox Studio as headless-capable plugins (as of late 2026)

Research date: 2026-10-24. Scope: how Bazaar's Engine Integration API can host Godot and Roblox Studio as plugins that agents use headlessly. Design context: Bazaar.Soul.md (headless agent VMs, GUI work on Bazaar.Windows) and Bazaar.md (Engine Integration API marked DECIDED in direction, Roblox first-class).

Verification legend: [V] = read on a fetched primary or near-primary page this session. [S] = search snippet only, not fetched. [U] = unverified, from memory or from the Bazaar design docs, not checked this session.

Research limits: about 15 tool calls. Not researched: GDExtension, GUT/gdUnit4, Luau tooling beyond what the design docs state, Roblox ToS, Avalonia pane patterns, MCP transport details. These are listed as Gaps.

---

## Key question 1: Godot headless capabilities and versions

### Takeaway
Godot's `--headless` mode covers import, export, script checks and running scripts without a window, which is enough for a headless agent loop. C# is supported on desktop but cannot export to web and is experimental on Android and iOS. The current docs are for 4.7; release status of 4.7.x is not settled from the sources I could reach.

### Cited Findings
- The official CLI docs describe `--headless` as shorthand for `--display-driver headless --audio-driver Dummy`, useful for servers and with `--script`, and required for exports on platforms without GPU access such as CI. [V] — [Godot CLI tutorial](https://docs.godotengine.org/en/stable/tutorials/editor/command_line_tutorial.html)
- `--import` starts the editor, imports resources, then quits. It implies `--editor` and `--quit` and is editor-build only. [V] — same page
- `--export-release <preset> <path>` and `--export-debug` export a project. Caveats: the binary must be an editor build, export templates must be installed (or the preset must define a custom template), the preset name must match `export_presets.cfg`, the target directory must already exist, and relative output paths resolve against the folder containing `project.godot`. [V] — same page
- `--script <script>` runs a script that inherits `SceneTree` or `MainLoop`. `--check-only` parses for errors and quits. Both are documented as extended-template-build features. [V] — same page
- The docs warn that unknown arguments are silently ignored, so a mistyped flag fails without a warning. Agent tooling should validate flags itself. [V] — same page
- `--write-movie <file>` writes movie or PNG output. Whether this works under the headless display driver (no GPU) is not stated on the page. [V for flag existence; the headless behavior is unverified]
- C#: "Since Godot 4.2, projects written in C# support all desktop platforms (Windows, Linux, and macOS)." Android support is experimental. iOS is experimental, with limits. "Currently, projects written in C# cannot be exported to the web platform." The standard Godot executable has no C# support, so a .NET editor build is required. [V] — [Godot C# docs](https://docs.godotengine.org/en/stable/tutorials/scripting/c_sharp/index.html)
- The C# page does not describe hot reload or the GDScript-versus-C# differences; that article is linked but not summarized on the fetched page. [V, as a gap on that page]
- Documented version on the fetched pages is Godot 4.7. [V]
- Release status is unclear. The Godot release feed (godotengine.org) snapshot showed maintenance release 4.6.3 dated 20 May 2026 and no 4.7 announcement. A search returned APKMirror listings for 4.7.0 (18 June 2026), 4.7.1 (14 July 2026) and 4.7.2 (18 August 2026), but labeled 4.7.2 "Early Access". Treat 4.7.x stable status as unverified; check godotengine.org/releases before pinning. [S] — [APKMirror 4.7.2 listing](https://www.apkmirror.com/?p=13170693); [Godot release feed](https://godotengine.org/news/release)

### Inferences
- A Godot pipeline for agents can run entirely in the headless VM: import, `--check-only` on scripts, `--script` test runners, and `--export-release` with templates installed. The Windows GUI host is not needed for these steps.
- Because unknown flags fail silently, the Godot adapter should check exit codes and parse output rather than trust flags. It should also check that the binary is the right build (editor vs export template, .NET vs standard) before running.
- C# projects need a .NET Godot build of the editor in the agent VM, and web export is off the table for C# projects. A GDScript-first default is safer for web targets.

### Gaps
- GDExtension (C/C++ or other languages via native extensions): not researched this session. [U] from memory: godot-cpp and godot-rust are the usual routes. Needs a source check.
- Scene formats (.tscn/.tres) as text-diffable files: not fetched. [U] from memory: Godot scenes and resources are text by default and diff well in git. Needs a source check.
- Editor plugin language and in-editor LSP/DAP servers an agent could call: not fetched. [U] from memory: the editor exposes a GDScript language server and a debug adapter on local ports. Needs a source check.
- GUT and gdUnit4 for tests: not researched. Both are commonly used with GDScript. Needs a check of current versions.
- Hot reload status for C#: not stated on the fetched page.

---

## Key question 2: Roblox Studio integration, sync, and automation

### Takeaway
Roblox now ships an official Studio MCP server, built into Studio, that exposes 26 tools including script editing, Luau execution, play-mode control and console output. It only works while a Studio instance is open and enabled. That means the Roblox path needs a GUI-capable Windows host, which conflicts with the headless-VM default in the design docs. Rojo covers file-based sync for scripts. Open Cloud covers REST automation of published experiences, but I could not confirm it covers script editing or Luau execution.

### Cited Findings
- The official Studio MCP server runs as a local process over stdio. Studio must be open, and the server is enabled under Assistant > ... > Manage MCP Servers > Enable Studio as MCP server. Config examples exist for Windows (`mcp.bat` via `cmd.exe`) and macOS (`StudioMCP` binary). [V] — [Roblox Studio MCP docs](https://create.roblox.com/docs/studio/mcp)
- Each tool call takes a `studio_id`, and `list_roblox_studios` lists open instances, so one server can target several open Studio sessions. [V] — same page
- The page lists 26 tools: scripts (`script_read`, `multi_edit`, `script_search`, `script_grep`); assets (`generate_mesh`, `generate_material`, `generate_procedural_model`, `wait_job_finished`, `search_asset`, `insert_asset`, `upload_image`, `store_image`); data model (`subagent`, `search_game_tree`, `inspect_instance`); Luau (`execute_luau`); playtesting (`get_studio_state`, `start_stop_play`, `get_console_output`, `screen_capture`); player input (`character_navigation`, `user_keyboard_input`, `user_mouse_input`); docs (`http_get`, `skill`); session (`list_roblox_studios`). [V] — same page
- The page describes no authentication mechanism and warns that connected clients can read and modify content in open places, so only trusted clients should connect. [V] — same page
- A February 2026 update reportedly added console-output reading, play-mode start/stop, running a script in play mode, a mode check, and bring-your-own-key support for Studio's Assistant (Claude, OpenAI, Gemini). [S — secondary source, MoguraVR in Japanese; not fetched] — [MoguraVR](https://www.moguravr.com/roblox-studio-mcp-server-agentic-update/)
- Community MCP server `boshyxd/robloxstudio-mcp` (about 487 stars, 69 tools) was archived on 6 June 2026, with a pointer to a fork at `Chrrxs/robloxstudio-mcp`. [S] — [mcpservers.org listing](https://mcpservers.org/en/servers/boshyxd/robloxstudio-mcp)
- Community server `weppy-roblox-mcp` supports Claude Code, Cursor, Codex and Gemini, but it is PowerShell-based and so Windows-only. About 30 GitHub stars. [S] — [EnterpriseDNA listing](https://enterprisedna.co/directories/mcp/hope1026-weppy-roblox-mcp)
- Rojo: the latest release in the search results is v7.7.0. It adds `rojo syncback` (convert an existing place file to a Rojo project) and moves networking to WebSockets with MessagePack. The release date was not confirmed. [S] — [Rojo v7.7.0 release notes](https://newreleases.io/project/github/rojo-rbx/rojo/release/v7.7.0); [Rojo on lib.rs](https://lib.rs/crates/rojo)
- Roblox has its own beta sync feature, Script Sync, and says it has no plans to replace Rojo. Rojo's maintainer frames Rojo as the choice for teams that treat the file system as source of truth. [S] — [DevForum Script Sync beta](https://devforum.roblox.com/t/studio-script-sync-now-in-beta/4065468)
- Open Cloud: the page describes REST APIs for updating games, restarting servers, data stores, memory stores, user restrictions, inventory and more. API keys are "the easiest way to get started"; OAuth 2.0 is also supported. Legacy APIs use cookie-based auth, may change "without notice", and have minimal stability guarantees. [V] — [Open Cloud docs](https://create.roblox.com/docs/cloud/open-cloud)
- The Open Cloud page did not confirm Luau Execution, place publishing, assets or messaging endpoints, and it did not say whether Open Cloud can edit scripts inside a place. [V as a gap]
- The Bazaar.Soul design doc states that Roblox Studio has no official Linux client, so Windows is effectively required. [U for this session: taken from the design doc, not independently checked]
- Bazaar.Soul also says `luau-analyze` (Roblox's official CLI analyzer) and Selene (community linter) run headless without Studio. [U: from the design doc; not checked this session]

### Inferences
- The most practical Roblox path is for Bazaar to act as an MCP client of Roblox's built-in Studio server. That gives agents script read/edit, `execute_luau`, play-mode control and console output without writing a Studio plugin. The cost is that Studio must be open on a Windows GUI host, so this path cannot run in the headless agent VM as currently designed.
- A plausible split: headless VMs run `luau-analyze`, Selene, StyLua and Rojo builds. A GUI-capable Windows host runs Studio plus the MCP server, and is reached only for play-testing and runtime checks. This matches the existing Soul design, but the GUI host becomes a shared, checked-out resource, which the Soul doc already marks as an open question.
- Because the official server lets connected clients modify open places, Bazaar needs an explicit approval layer in front of any Roblox tool call that writes.

### Gaps
- Whether Studio can run in a headless Windows VM (no display, no GPU): not verified. The official MCP docs require Studio to be open, and Studio is a GUI application. Needs a direct test on Server Core + WSL2 or a Windows Server VM with a virtual display.
- Roblox Creator / Open Cloud terms regarding automation and AI agents: not checked. Needs a read of the current Creator Terms and Open Cloud terms before shipping.
- Luau-specific tools (StyLua, roblox-ts): not researched beyond the design doc.
- Whether a Studio plugin can open a localhost connection (for a custom bridge rather than the official MCP server): not verified. [U] from memory, Studio plugins can use HttpService with HTTP enabled, but this needs a source check.
- Roblox's February 2026 and August 2026 MCP updates: changelog text not fetched.

---

## Key question 3: Where plugin code lives, and how the .NET server talks to it

### Takeaway
Engine-side code must be in the engine's language: GDScript or C# for a Godot editor add-on, Luau for a Studio plugin. The Bazaar server stays .NET. The clean seam is out-of-process: each engine integration is a .NET adapter that talks to the engine over a local channel, which is either the engine's own MCP server (Roblox), a process invocation (Godot CLI), or a small bridge the adapter ships (Godot editor, Studio plugin).

### Cited Findings
- Roblox's official Studio MCP server uses stdio and runs locally next to Studio. [V] — [Roblox Studio MCP docs](https://create.roblox.com/docs/studio/mcp)
- Godot's headless path needs no bridge: the .NET adapter launches the Godot binary with CLI flags and reads exit codes and output. [V] — [Godot CLI tutorial](https://docs.godotengine.org/en/stable/tutorials/editor/command_line_tutorial.html)
- The official C# MCP SDK repo's README lists an ASP.NET Core package for HTTP-based MCP servers, but the fetch did not give version, target framework or transport details. [V as a gap] — [csharp-sdk repo](https://github.com/modelcontextprotocol/csharp-sdk)
- NuGet search results disagree on the latest `ModelContextProtocol` version. One NuGet "absoluteLatest" page shows 2.0.0, and a 2.0.0-preview.2 also exists. Several profile mirrors show 1.x. Confirm with `dotnet package search ModelContextProtocol --exact-match` before pinning. [S] — [NuGet ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol/absoluteLatest)

### Inferences
- Keep everything Bazaar owns in .NET. Engine-side bridges are the only non-.NET code, and each is small and disposable: a Godot editor add-on (GDScript or C#) and, if the official Roblox MCP is not enough, a Luau Studio plugin.
- The Godot bridge is optional for headless work. Add it only when the agent needs live editor state.
- The Roblox official MCP server can be consumed directly, so Bazaar needs no Studio plugin for the first version.

### Gaps
- Exact Godot editor-plugin APIs for a local socket or HTTP server: not researched.
- Whether a Studio plugin is required for anything the official MCP server cannot do: not established.

---

## Key question 4: .NET plugin architecture patterns and a recommended contract

### Takeaway
Use capability-declared, out-of-process engine adapters for all engine work, and use in-process plugins only for Avalonia UI panes. Expose the agent-facing tool surface through MCP. Keep the contract small: a manifest, a capability enum, and a session interface. This is a design recommendation, not a sourced finding; the patterns are from general .NET practice and were not checked against sources this session.

### Recommended design (not sourced; [U])

Two plugin kinds:
1. **Engine adapters (out-of-process).** Each runs as a child process with its own lifecycle, so an engine crash or hang cannot take down the Bazaar server. Communication with the host uses JSON-RPC or gRPC for lifecycle, and MCP over stdio for the tools agents call.
2. **UI contributions (in-process, client only).** Avalonia panes are loaded in the client through `AssemblyLoadContext`. The server never loads UI code. The server sends a declarative pane descriptor (JSON), and the client maps it to a view model and a DataTemplate it already has. Clients that cannot load a plugin still show a generic pane from the descriptor.

Sketch of the contract:

```csharp
public enum EngineCapability { Import, Export, Test, ScriptCheck, Scenes, Assets, Playtest, LiveSync, HotReload, Timeline }

public interface IEngineAdapter
{
    string EngineId { get; }                       // "godot", "roblox-studio"
    string Version { get; }
    IReadOnlySet<EngineCapability> Capabilities { get; }
    Task<IEngineSession> OpenAsync(ProjectRef project, CancellationToken ct);
}

public interface IEngineSession : IAsyncDisposable
{
    Task<CommandResult> RunAsync(EngineCommand command, CancellationToken ct);
}

public sealed record PaneDescriptor(string Id, string Title, string Kind, JsonElement Model);
```

Each adapter's manifest declares its capabilities, required binaries and versions, and whether it needs a GUI host. The scheduler uses that to place work: headless VM for `Import`, `Export`, `Test`, `ScriptCheck`; GUI host for `Playtest` and any Studio session.

### Cited Findings
- None of the architecture recommendations above are sourced. Pattern names (AssemblyLoadContext, JSON-RPC, gRPC, declarative UI descriptors) are standard .NET and protocol patterns; no source for them was fetched this session. [U]

### Inferences
- Capability flags let the scheduler route work to the right host, which is the main reason to have them. Keep the enum short and add to it only when an adapter needs a new verb.
- A declarative pane descriptor keeps UI plugins from being a server-side security risk and fits the remote-described UI idea if the server needs to show engine panes in clients it does not ship code for.

### Gaps
- Avalonia-specific pane registration and DataTemplate patterns: not researched.
- MEF versus manual `AssemblyLoadContext` loading: not compared against current guidance.

---

## Key question 5: Is MCP a sensible plugin transport for engine tools?

### Takeaway
Yes for the agent-facing tool surface. Roblox's official Studio server already uses MCP over stdio, so Bazaar's Roblox adapter would be an MCP client. The C# SDK is official, but its version is not settled from what I could check.

### Cited Findings
- Roblox's built-in Studio server is an MCP server over stdio with 26 tools. [V] — [Roblox Studio MCP docs](https://create.roblox.com/docs/studio/mcp)
- The official C# SDK exists at `modelcontextprotocol/csharp-sdk` with an ASP.NET Core package for HTTP servers. [V] — [csharp-sdk repo](https://github.com/modelcontextprotocol/csharp-sdk)
- NuGet `ModelContextProtocol` shows 2.0.0 on the "absoluteLatest" page, with 2.0.0-preview.2 also listed; mirrors show older 1.x versions. [S] — [NuGet](https://www.nuget.org/packages/ModelContextProtocol/absoluteLatest)

### Inferences
- MCP fits the agent-facing side. It does not replace the host-to-adapter lifecycle channel, which should stay as JSON-RPC or gRPC because it needs streaming, cancellation and typed errors.

### Gaps
- MCP transports in the C# SDK (stdio, Streamable HTTP, SSE) and their stability: not confirmed from the fetched page. [U] from general knowledge, the MCP spec has stdio and Streamable HTTP, and SSE is deprecated in favor of Streamable HTTP. Verify against modelcontextprotocol.io.

---

## Key question 6: Practical limits of automated playtesting without a display

### Takeaway
Headless runs can check that a project compiles, imports, passes unit tests and exports. Real playtesting needs the engine's runtime, and with no display or GPU the visual part is limited. Roblox's official server exposes play-mode control and console output, but it requires an open Studio.

### Cited Findings
- Roblox's Studio MCP tools include `start_stop_play`, `get_console_output`, `get_studio_state`, `screen_capture` and input tools (`user_keyboard_input`, `user_mouse_input`, `character_navigation`). These need a running Studio. [V] — [Roblox Studio MCP docs](https://create.roblox.com/docs/studio/mcp)
- Godot's `--headless` replaces the display driver with headless and the audio driver with Dummy, so visual output is not produced. [V] — [Godot CLI tutorial](https://docs.godotengine.org/en/stable/tutorials/editor/command_line_tutorial.html)
- Bazaar.Soul states agents cannot continuously watch a viewport, and periodic screenshots are costly. [U: design doc, not checked]

### Inferences
- Practical playtest checks for agents are console-based: assert on logs, scripted input sequences, and state dumps, not on screenshots.
- Visual checks need the GUI host and a human or a screenshot diff, which is the existing design split.

### Gaps
- Whether Godot's `--write-movie` produces frames under the headless driver: not verified.
- Whether Roblox `screen_capture` works without a visible Studio window: not verified.

---

## Summary of what is verified vs not

- Verified this session: Godot CLI flags and caveats; Godot C# platform limits; Roblox Studio MCP setup, tool list and warnings; Open Cloud auth model.
- Search snippets only: Godot 4.7.x release status, Rojo v7.7.0, Script Sync beta, boshyxd archive, MCP C# SDK version, Roblox Feb 2026 update.
- Unverified: GDExtension, scene formats, Godot LSP/DAP, GUT/gdUnit4, Roblox ToS, headless Studio on a VM, Luau tool details, all architecture recommendations.

## Sources

- https://docs.godotengine.org/en/stable/tutorials/editor/command_line_tutorial.html
- https://docs.godotengine.org/en/stable/tutorials/scripting/c_sharp/index.html
- https://godotengine.org/news/release
- https://www.apkmirror.com/?p=13170693
- https://create.roblox.com/docs/studio/mcp
- https://create.roblox.com/docs/cloud/open-cloud
- https://www.moguravr.com/roblox-studio-mcp-server-agentic-update/
- https://mcpservers.org/en/servers/boshyxd/robloxstudio-mcp
- https://enterprisedna.co/directories/mcp/hope1026-weppy-roblox-mcp
- https://newreleases.io/project/github/rojo-rbx/rojo/release/v7.7.0
- https://lib.rs/crates/rojo
- https://devforum.roblox.com/t/studio-script-sync-now-in-beta/4065468
- https://github.com/modelcontextprotocol/csharp-sdk
- https://www.nuget.org/packages/ModelContextProtocol/absoluteLatest
