# Bazaar Server: .NET Hosting and Transport Feasibility (as of 2026-10-24)

**Recommendation (summary).** Target **.NET 10 LTS** (supported to November 2028). Build the server as a **Worker Service** using the **Generic Host**, registered with `AddWindowsService()` from `Microsoft.Extensions.Hosting.WindowsServices`, and run it as an automatic-start Windows Service with failure recovery configured. Use **JIT (no Native AOT) for v1**. Keep the **raw ASP.NET Core WebSocket** transport, which the Server doc already chose, but send messages as **C# records in a shared contracts project serialized with System.Text.Json source generation**, with a per-GC sequence number on every event. Protobuf adds value only if non-.NET SDK consumers are a near-term need; this conflicts with the Server doc's "DECIDED" Protobuf line and needs an explicit decision. Keep any Terminal.Gui or Spectre.Console UI **out of the service process**. Use a separate CLI or the Avalonia client over the same API. Use **built-in BackgroundService plus durable Postgres tables** for shifts, breaks, and inboxes. Add Quartz.NET only if cron-style jobs prove necessary. Use Aspire for local development only.

Verification legend used below: **[Primary]** = Microsoft Learn, dotnet.microsoft.com, or project GitHub page fetched directly. **[Secondary]** = search-result snippet or aggregator, not fetched. **[Opinion]** = my engineering judgment, not sourced.

---

## Key Question 1: Which .NET version to target?

### Takeaway
.NET 10 is the current LTS release and is the correct target for a 24/7 service that must stay supported through 2028. .NET 8 LTS and .NET 9 STS both reach end of support in November 2026. .NET 11 (STS) is expected in November 2026, but I could not confirm its date from a primary source.

### Cited Findings
- Microsoft's support page lists .NET 10 (LTS) as supported until November 2028, .NET 9 (STS) until November 2026, and .NET 8 (LTS) until November 2026 — [Microsoft Learn: .NET releases, patches, and support](https://learn.microsoft.com/en-us/dotnet/core/releases-and-support) [Primary]
- LTS releases are supported for three years, or one year after the next LTS ships if that is later. STS releases are supported for 24 months. Releases alternate LTS and STS — same page [Primary]
- Microsoft's guidance: "If you're building a service and expect to continue updating it regularly, use the latest release, whether LTS or STS." — same page [Primary]
- Servicing updates ship monthly; patch roll-forward is automatic by default. Install them to stay supported — same page [Primary]
- Microsoft's .NET support policy page is the authoritative table; the Learn page defers to it — [.NET Support Policy](https://dotnet.microsoft.com/platform/support/policy/dotnet-core) [Primary, not fetched]
- A search summary reports .NET 11 STS is scheduled for November 10, 2026 and that it is still in preview as of August 2026. This is not confirmed on a primary page — [gapvelocity.ai: .NET 10 vs .NET 11](https://www.gapvelocity.ai/blog/dotnet-10-vs-dotnet-11) [Secondary]
- The ASP.NET Core Learn pages carry `aspnetcore-11.0` and `aspnetcore-12.0` monikers, which suggests docs for later versions are being prepared. This is an inference, not a release date — [Host ASP.NET Core in a Windows Service](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/windows-service?view=aspnetcore-10.0) [Primary]

### Inferences
- Pick .NET 10 LTS and stay on it until its successor LTS is mature. Standard-term releases add churn to a service that must run unattended.
- Do not target .NET 11 STS for production. An STS release would need upgrading within about two years.
- Use the latest SDK. Microsoft notes the SDK can target all available runtimes.

### Gaps
- The exact .NET 11 release date and its support end date are not confirmed on a primary source.
- I did not fetch the official support policy page, so the November 2028 date rests on the Learn page only.

---

## Key Question 2: Windows Service hosting, auto-restart, logging, config, graceful shutdown

### Takeaway
ASP.NET Core and Worker Service hosting on Windows is well documented and supported. `AddWindowsService()` handles the lifetime, content root, and event log integration. Auto-restart after a crash is not in the Learn page I fetched, so it must be configured through Windows service recovery settings. This is not verified from a primary source.

### Cited Findings
- Start from the Worker Service template, then install `Microsoft.Extensions.Hosting.WindowsServices` — [Host ASP.NET Core in a Windows Service](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/windows-service?view=aspnetcore-10.0) [Primary, ms.date 2026-04-27, updated 2026-10-14]
- Call `builder.Services.AddWindowsService();`. When running as a service it sets the host lifetime to `WindowsServiceLifetime`, sets the content root to `AppContext.BaseDirectory`, and enables Event Log logging — same page [Primary]
- Event Log default level is Warning or higher. Override with `Logging:EventLog:LogLevel:Default` in appsettings — same page [Primary]
- Only administrators can create new Event Log sources. If the source cannot be created, a warning is logged and Event Log is disabled — same page [Primary]
- Install with PowerShell: `New-Service -Name {SERVICE NAME} -BinaryPathName "{EXE FILE PATH} --contentRoot {EXE FOLDER PATH}" ... -StartupType Automatic`. Passing `--contentRoot` explicitly is shown in the docs — same page [Primary]
- The current directory for a service is `C:\WINDOWS\system32`. Do not use `GetCurrentDirectory` for resource paths. Use `ContentRootPath` or `ContentRootFileProvider` — same page [Primary]
- Hosted services: `ExecuteAsync` runs on the thread pool and must finish promptly when its cancellation token fires. The host blocks in `StopAsync` waiting for `ExecuteAsync` to complete. If the process fails unexpectedly, `StopAsync` may not be called — [Background tasks with hosted services](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0) [Primary, updated 2026-10-13]
- Shutdown is ungraceful after `HostOptions.ShutdownTimeout` if work does not stop in time — same page [Primary]
- Worker services are a supported Native AOT app type in .NET 8+ — same page and the ASP.NET Core Native AOT page [Primary]
- Auto-restart on crash: I did not find recovery or failure-action guidance on the Learn page. Windows `sc.exe failure` or Service Control Manager recovery settings are the usual route — [Not verified from primary source]

### Inferences
- Config layout for a service: keep `appsettings.json` beside the executable, pass `--contentRoot` at install time, and write mutable data (DB config, logs, agent workspaces) to a fixed folder such as `%ProgramData%\Bazaar`. Do not rely on the working directory.
- Logging: use the built-in `ILogger` with the Event Log provider for lifecycle events, and a file or structured sink for application logs. The Event Log provider's Warning default would hide routine logs. I did not research specific file-logging libraries.
- Graceful shutdown: give `ExecuteAsync` loops a cancellation check at every await. Make sure in-flight agent turns persist their state on cancellation, since a hard kill can skip `StopAsync` entirely. Treat persistence as the real safety net.
- Auto-restart: configure Windows recovery (restart on first, second, and subsequent failures) during install. Pair that with a watchdog or health endpoint for hangs, which a crash-restart policy does not catch.

### Gaps
- Windows service recovery (`sc.exe failure`) settings: not verified from a primary source.
- Default `ShutdownTimeout` value: the Learn page references it but I did not confirm the number.
- Logging sink choices (Serilog, etc.): not researched.

---

## Key Question 3: Client-server transport (WebSocket vs SignalR vs gRPC)

### Takeaway
gRPC is strong for typed streaming between services but is not practical for browser use and needs HTTP/2 on every client. SignalR gives broadcast and groups out of the box, and Microsoft's own gRPC comparison recommends SignalR for broadcast-style real-time apps. Raw ASP.NET Core WebSockets is the simplest choice that matches the Server doc's requirements, provided the team builds its own framing and catch-up. This is a judgment call, not a benchmark result.

### Cited Findings
- gRPC: Protobuf payloads, HTTP/2, all streaming modes (unary, server, client, bidi). Browser access requires gRPC-Web. Microsoft recommends SignalR for broadcasting a message to registered connections, such as chat rooms — [Compare gRPC services with HTTP APIs](https://learn.microsoft.com/en-us/aspnet/core/grpc/comparison?view=aspnetcore-10.0) [Primary]
- ASP.NET Core Native AOT compatibility table (ASP.NET Core 10 page): WebSockets supported; gRPC supported; SignalR partial; Minimal APIs partial; MVC not supported — [ASP.NET Core support for Native AOT](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/native-aot?view=aspnetcore-10.0) [Primary]
- Server doc decision: raw WebSocket, not Socket.io, plus Protobuf schemas (Bazaar.Server.md, Section 3) [Internal]
- Server doc note: WebTransport reached browser Baseline in March 2026 but native .NET library maturity is still catching up. Not re-verified here — Bazaar.Server.md [Internal, RESEARCHED per doc, not re-verified]
- Terminal.Gui and Avalonia clients are native .NET; no browser client is planned (Bazaar.Server.md, Section 1) [Internal]

### Inferences
- **Recommended: raw WebSocket with a tiny in-house envelope.** Every message carries `{gcId, seq, type, payload}`. This gives full control over catch-up (Key Question 5), has no framework dependency, and is AOT-compatible if AOT is ever needed. [Opinion]
- **SignalR is the fallback.** It gives groups, broadcast, and client reconnect logic. Costs: a second protocol layer, and partial Native AOT support. Choose it if the team prefers not to build reconnect and group fan-out. [Opinion]
- **gRPC is not recommended for v1.** Its streaming is good, but it adds HTTP/2 client requirements and `.proto` tooling for a single-language stack. Its main value is cross-language contracts, which the team does not need yet. [Opinion]
- Avalonia on Android: I did not verify whether `Grpc.Net.Client` or `ClientWebSocket` behaves well on Android, including Doze and background networking. This is the highest-risk unknown for mobile clients. Test on a real device before committing.

### Gaps
- Android-specific behavior for WebSocket vs gRPC over HTTP/2: not verified.
- No benchmark numbers for latency or throughput comparing the three transports for this workload.
- Whether `ClientWebSocket` survives Android background suspension: not verified.

---

## Key Question 4: Serialization and shared contracts (Protobuf vs C# contracts)

### Takeaway
When server and all clients are C#, a shared contracts project with source-generated System.Text.Json is the simplest option: no extra toolchain, AOT-safe, and readable on the wire. Protobuf is justified only if a non-C# SDK or plugin is planned. Faster binary options (MemoryPack, MessagePack) exist, but I did not verify their current versions or benchmarks.

### Cited Findings
- System.Text.Json source generation is required for Native AOT. Without it, reflection-based serialization fails under AOT — [ASP.NET Core Native AOT](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/native-aot?view=aspnetcore-10.0) [Primary]
- Protobuf is "small, binary" and gRPC uses it; gRPC requires a `.proto` contract — [Compare gRPC services](https://learn.microsoft.com/en-us/aspnet/core/grpc/comparison?view=aspnetcore-10.0) [Primary]
- Server doc decision: Protobuf schemas over WebSocket, to enforce the SDK contract at compile time for C# (Bazaar.Server.md, Section 3) [Internal]

### Inferences
- **Recommended for v1: shared C# contracts project, System.Text.Json source-generated, with a versioned envelope.** One project referenced by server and all clients. Contracts are compiler-checked. The wire format is debuggable. [Opinion]
- **Protobuf only if needed.** Protobuf makes sense if a non-.NET SDK, plugin, or external tool must speak to Bazaar. If that is the goal, keep Protobuf and accept the schema tooling. This is a real decision point and conflicts with the Server doc's DECIDED line. [Opinion]
- **MemoryPack or MessagePack-CSharp** are faster options if payload size becomes a bottleneck. I did not verify their current versions, AOT status, or benchmarks. Do not adopt one without a measurement. [Opinion]

### Gaps
- Current versions and benchmarks for MemoryPack, MessagePack-CSharp, Google.Protobuf, and protobuf-net: not verified.
- Whether a Protobuf-to-C# codegen route exists for every client: not verified.

---

## Key Question 5: Reconnect and catch-up for clients offline for hours

### Takeaway
Use a per-GC monotonic sequence number and let the server replay events after the client's last-seen sequence. The Server doc's append-only GC record makes this straightforward. Pure reconnect libraries do not solve catch-up. This design is my recommendation and is not sourced.

### Cited Findings
- The Server doc requires GC history to be append-only and never destructively purged (Bazaar.md, Section 2). This is the foundation for replay — [Internal]
- Server doc: Postgres indexing on `gc_id` plus `timestamp` is sufficient at expected scale — Bazaar.Server.md, Section 3 [Internal, RESEARCHED per doc, not re-verified]

### Inferences
- **Protocol:** On connect, the client sends `{gcId: lastSeq}` for each GC it watches. The server replays from Postgres, then switches to live push. Use a single `Subscribe` message with a list of pairs. [Opinion]
- **Snapshot fallback:** If a client's last sequence is very old, send a summary or snapshot rather than the full replay. Set a cutoff so a 10-day-old client does not download the whole history. [Opinion]
- **Idempotency:** Clients de-duplicate by `(gcId, seq)`. This makes resend safe.
- **Live buffer:** Keep a small in-memory ring per GC for fast replay of the last few minutes. Fall back to Postgres for older events. Use Valkey pub/sub only for fan-out, not as the source of truth, to match the Server doc. [Opinion]

### Gaps
- Replay limits and snapshot thresholds: design decisions with no external source.
- Whether Valkey pub/sub is needed on a single NUC: not verified. A single-process in-memory broadcaster may be enough.

---

## Key Question 6: Terminal.Gui or Spectre.Console for a headless server's admin console

### Takeaway
Do not run a TUI inside the Windows Service process. Terminal.Gui v2 is actively maintained and cross-platform, and Spectre.Console is a good output and prompt library for one-shot CLI commands. Both are better used by a separate process that talks to the server's API. Terminal.Gui is the stronger option if an interactive admin TUI is wanted, but it is a separate client, not part of the server.

### Cited Findings
- Terminal.Gui: the project now lives at `tui-cs/Terminal.Gui`; `gui-cs` links redirect — [GitHub releases, tui-cs/Terminal.Gui](https://github.com/tui-cs/Terminal.Gui/releases) [Primary]
- Terminal.Gui latest on the releases page: **v2.5.0, 11 Sep 2026, marked Latest**; v2.5.0-beta.1 (03 Sep); v2.4.17 (07 Jul); v2.4.16 and v2.4.15 (28 Jun) — same page [Primary]
- v2.5.0 release notes include a NativeAOT fix (RuneJsonConverter no longer trips IL2026) and a terminal size fix on other platforms — same page [Primary]
- Terminal.Gui NuGet description says it supports Windows, macOS, and Linux/Unix — [NuGet: Terminal.Gui](https://www.nuget.org/packages/Terminal.Gui.v2desktop) [Secondary, search snippet]
- Terminal.Gui targets .NET 8 and later per NuGet pages; beta.11 lists net10.0 — [Secondary]
- Spectre.Console GitHub releases page: latest entries are **0.57.2 (02 Jul 2026), 0.57.1 (24 Jun), 0.57.0 (11 Jun), 0.56.0 (05 Jun), 0.55.2 (17 Apr); all marked pre-release** — [GitHub releases, spectreconsole/spectre.console](https://github.com/spectreconsole/spectre.console/releases) [Primary]
- Spectre.Console 0.54.0 was announced as a release on 13 Nov 2025 — [Spectre.Console 0.54.0 released](https://spectreconsole.net/blog/2025-11-13-spectre-console-0.54-released/) [Primary]
- Spectre.Console.Cli moved to its own repository; its packages will no longer be versioned with Spectre.Console — [Secondary, NuGet profile snippet]
- NuGet profile snippet: Spectre.Console latest is 0.57.3-alpha.0.29 (prerelease); Spectre.Console.Cli stable 0.57.2 — [Secondary]

### Inferences
- **Recommended: no UI in the service.** A Windows Service runs in a non-interactive session, so a TUI cannot render there. This is from general Windows knowledge and not verified against a primary source. [Opinion]
- **Option A: no admin UI.** Simplest. Use logs and the Avalonia client. Good for v1.
- **Option B: CLI plus normal client.** A `bazaar` command, using Spectre.Console for tables and prompts or System.CommandLine for parsing, calls the same WebSocket or HTTP API. Low risk, easy to script. [Opinion]
- **Option C: TUI client over the same API.** Terminal.Gui v2 as a separate process. Good for live status and a log view. Ships as a second client and must track server API versions. Adds maintenance. [Opinion]
- Versioning: Terminal.Gui is pre-1.0 in its v2 line but has shipped many releases. Pin a version and check the breaking-changes doc on each upgrade. The v2.5.0 breaking-change list is referenced on the project docs site but not fetched.
- Spectre.Console has been prerelease-only since 0.55. Pin a prerelease or stay on stable 0.54.0 if that is acceptable.

### Gaps
- Whether Terminal.Gui or Spectre.Console runs inside a Windows Service session: not verified from a primary source. The Session 0 limitation is general Windows knowledge.
- Terminal.Gui NuGet current stable version: search results conflicted (2.4.x vs 2.5.0). Confirm on nuget.org before pinning.
- Spectre.Console stable release after 0.54.0: not confirmed. The GitHub page shows only prereleases after 0.54.

---

## Key Question 7: Background jobs and scheduling

### Takeaway
Built-in `BackgroundService` plus durable tables in Postgres covers agent shifts, breaks, vacations, and async inboxes. Quartz.NET is the right add-on if real cron-style triggers are needed. Hangfire is free for core use but its Pro features are paid. Temporal is a strong fit for durable workflows, but it is heavy for a single NUC and I did not verify its server-side requirements.

### Cited Findings
- Hosted services: long-running background work is implemented by overriding `ExecuteAsync`, and it is started and stopped by the host — [Background tasks with hosted services](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0) [Primary]
- Quartz.NET NuGet profile: latest version listed as **4.6.0**, packages updated 19 Oct 2026. Description cites cron, calendar, and interval triggers, and clustering over a persistent job store — [Quartz.NET NuGet profile](https://www.nuget.org/profiles/Quartz.NET) [Secondary]
- Quartz.NET 4.0 release post: targets net10.0, has breaking changes, and a mandatory schema migration; several 3.x packages were folded into `Quartz` — [Quartz.NET 4.0 Released](https://www.quartz-scheduler.net/posts/2026-09-03-quartznet-4.0-released.html) [Primary, search snippet only, not fetched]
- Hangfire core is LGPLv3 and free. Paid Hangfire Pro tiers are listed on the vendor page at Startup $500 and Business $1,500 per organization per year. Enterprise pricing from aggregators is unverified — [Hangfire pricing](https://www.hangfire.io/pricing/) [Primary, search snippet only, not fetched]
- Temporal .NET: NuGet publisher profile lists Temporalio 1.18.0 (13 Aug 2026); a package page shows 1.20.0; the two conflict — [Temporalio NuGet profile](https://www.nuget.org/profiles/Temporal) [Secondary]
- Temporal changelog: Standalone Activities GA across all six SDKs as of 15 Sep 2026, including .NET — [Temporal changelog](https://temporal.io/changelog) [Primary, search snippet only]

### Inferences
- **Recommended: BackgroundService plus Postgres tables.** Model shifts, breaks, vacations, and inbox items as rows with due times. A single poller or Channel-based worker picks up due work. This survives restarts and needs no extra infrastructure. [Opinion]
- **Quartz only for cron-like triggers.** It adds a schema and a second scheduling model. Adopt it only when there are many recurring schedules. [Opinion]
- **Hangfire is less attractive.** Its Pro features are paid, and its storage model overlaps with the Postgres design. [Opinion]
- **Temporal is architecturally close to the agent problem** (durable, resumable workflows), but it requires running Temporal Server. On one NUC that is a significant operational addition. Revisit if workflows become complex. [Opinion]

### Gaps
- Temporal Server self-hosting requirements and resource use: not researched.
- Quartz 4.x clustering and schema migration details: not fetched.
- Hangfire Enterprise pricing: unverified.

---

## Key Question 8: Aspire for local dev, and Native AOT feasibility

### Takeaway
Use .NET Aspire for local orchestration and telemetry during development, not as the production host. Native AOT is feasible for parts of the ASP.NET Core stack, including WebSockets and worker services, but not MVC and only partly for SignalR. Given the Windows Service, Postgres, and LibGit2Sharp dependencies, AOT adds risk with little benefit for v1.

### Cited Findings
- Aspire 13.6 was released on 29 Sep 2026 per aspire.dev. NuGet lists Aspire packages at 13.6.3 (updated 21 Oct 2026). Aspire 13.6 adds SQLite-backed telemetry and run history — [Aspire 13.6 what's new](https://aspire.dev/whats-new/aspire-13-6/) [Primary, search snippet only]; [InfoQ: Aspire 13.6](https://www.infoq.com/news/2026/10/dotnet-aspire-13-6-release/) [Secondary]
- Aspire support policy page lists 13.5 as most recent — [Aspire support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/aspire) [Primary, out of date per NuGet]
- Native AOT compatibility (ASP.NET Core 10): WebSockets supported; gRPC supported; HealthChecks, CORS, JWT, RateLimiting supported; SignalR partial; Minimal APIs partial; MVC, Session, Spa, Blazor Server not supported — [ASP.NET Core Native AOT](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/native-aot?view=aspnetcore-10.0) [Primary]
- Native AOT requires `PublishAot` and System.Text.Json source generation. Reflection-based code throws at runtime. Libraries that use reflection need updates — same page [Primary]
- Worker services are a supported Native AOT app type — [Hosted services](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0) [Primary]

### Inferences
- **Aspire:** use it on developer machines to run Postgres, Valkey, and the server together, with telemetry. Keep it out of the NUC deployment. [Opinion]
- **Native AOT for v1: no.** The benefit (startup time, memory) matters less on a 32 GB NUC running one long-lived process. The risk is in the dependencies: LibGit2Sharp wraps native libgit2, and Postgres, Valkey, and any agent SDKs need AOT-compatibility checks I have not done. [Opinion]
- **Keep AOT as an option.** Keep WebSocket and JSON contracts AOT-friendly from day one (source-generated JSON, no reflection in hot paths). Then AOT is a later switch, not a rewrite. [Opinion]

### Gaps
- Whether LibGit2Sharp, Npgsql, and the agent SDKs are AOT-compatible: not verified.
- Aspire's Windows host support for the dashboard: not verified.

---

## Open items for the decision-maker
1. Protobuf vs shared C# contracts. The Server doc says Protobuf is DECIDED. This note recommends C# contracts unless non-.NET SDK consumers are planned.
2. Confirm .NET 11 release and support dates on the official support policy page before any upgrade decision.
3. Test Avalonia on Android over raw WebSocket in background and in Doze before committing to transport.
4. Verify Windows service recovery settings against primary Microsoft documentation (`sc.exe failure`).
5. Confirm current NuGet versions for Terminal.Gui, Quartz.NET, and Temporalio before pinning.

## Sources (primary, fetched)
- https://learn.microsoft.com/en-us/dotnet/core/releases-and-support
- https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/windows-service?view=aspnetcore-10.0
- https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0
- https://learn.microsoft.com/en-us/aspnet/core/fundamentals/native-aot?view=aspnetcore-10.0
- https://learn.microsoft.com/en-us/aspnet/core/grpc/comparison?view=aspnetcore-10.0
- https://github.com/tui-cs/Terminal.Gui/releases
- https://github.com/spectreconsole/spectre.console/releases
- https://spectreconsole.net/blog/2025-11-13-spectre-console-0.54-released/

## Sources (secondary, search snippets only)
- https://www.gapvelocity.ai/blog/dotnet-10-vs-dotnet-11
- https://www.quartz-scheduler.net/posts/2026-09-03-quartznet-4.0-released.html
- https://www.nuget.org/profiles/Quartz.NET
- https://www.hangfire.io/pricing/
- https://www.nuget.org/profiles/Temporal
- https://temporal.io/changelog
- https://aspire.dev/whats-new/aspire-13-6/
- https://www.infoq.com/news/2026/10/dotnet-aspire-13-6-release/
- https://dotnet.microsoft.com/en-us/platform/support/policy/aspire
