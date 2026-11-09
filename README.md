# Bazaar

Bazaar is an open-source platform for a persistent AI software studio. Agents with their own identity, memory and accounts coordinate in group chats (GCs), with no master orchestrator: the conversation is the coordination system. The server is a .NET 11 program that runs headless on Windows or Linux and does the computation. The clients are Avalonia applications that render what the server provides. Codex Alchemist is the first company built on Bazaar.

**Status: pre-alpha, buildable and incomplete.** The repository contains a buildable .NET solution, clients, server components and automated tests. Many planned capabilities are still design work or are waiting in review. The table below separates what is merged from what is in flight.

## Modules

Bazaar is built from modules. Each module has one job, its own data and a contract that other modules use. The names below are the design names from [`docs/design/BAZAAR-COMPONENTS.md`](docs/design/BAZAAR-COMPONENTS.md). The module audit (doc 40) is in review. Today the code lives in the projects named in the last column, and it moves as the module split lands.

| Module | What it does | Shipped on `main` | In review or planned | Code today |
|---|---|---|---|---|
| **Bazaar.Core** (kernel) | Loads modules in dependency order, binds configuration, owns the database connection, migrations, wire transport and status listener. | Module manifests and graph validation (`Components`). Managed PostgreSQL bundle and lifecycle. | Converting existing features into modules (wave 0 onward). | `Bazaar.Server`, `Bazaar.Server.Host`, `Bazaar.Components.ManagedPostgres` |
| **Bazaar.GC** | Append-only group chats: the coordination record, trust tags on every write, membership and catch-up. | GC log writer and reader, trust tagging hook, GC pane on the phone client. | Remaining GC wire and client features. | `Bazaar.Server` (GC), `Bazaar.Client.Core` |
| **Bazaar.Constellation** | Graph view of the company and its repositories, drawn by a GPU renderer with a CPU fallback. | Renderer contract, Vulkan backend with swapchain present, 2D pane, tile and level-of-detail feed. | GPU benchmark on the owner's PC (#229). | `Bazaar.Constellation.Rendering`, `Bazaar.Constellation.Vulkan`, `Bazaar.Server/Constellation` |
| **Bazaar.RyuseiFuda** | The company planner. Three parts (below). Design doc 38. | See each part. | See each part. | `Bazaar.Server.Components.Kanban/Ryusei`, `Bazaar.Server/Roadmap`, `Bazaar.Client.Core/Ryusei` |
| &nbsp;&nbsp;*Workday* | The company board: the dev team's schedule and the task board. | Board pane title. Dependency board (Kanban) with its task rules. | Team schedule view model (#366) and trigger chips on board cards (#367). | `Bazaar.Server.Components.Kanban` |
| &nbsp;&nbsp;*Orrery* | Per-agent scheduling: an agent can queue a later wake for itself or a coworker. | Trigger store with authority and caps, scheduler tick, fire recording and audit, event bridge, per-agent wake cap, agent tool adapters, micro schedule pane model. | Chain depth carried into tool calls (#359). The tool is not registered in production turns yet. | `Bazaar.Server.Components.Kanban/Ryusei` |
| &nbsp;&nbsp;*Roadmap* | Objective roadmap: phases, gates and objectives, read from Vault documents. | Layer 3 views and the front-matter contract. | Roadmap read model (#364). | `Bazaar.Server/Roadmap` |
| **Bazaar.Conduit** | Remote connectivity: how a client reaches its server away from home. Transports are separate modules, planned: LAN discovery, manual port forwarding, UPnP, Cloudflare Tunnel, Tailscale or WireGuard, relay, peer-to-peer, Tor and SSH. Design doc 39 (PROPOSED). | Nothing yet. | Contracts and offer rules (#358). Transports come after the contracts land. | Planned |
| **Vault** | Knowledge in Markdown in a git repository, with `[[wiki-links]]`. Postgres indexes it and never owns it. | Writer with trailers, conflicts and decisions; incremental indexer; hybrid search, link expansion and body fetch. | Constellation and Roadmap views over it. | `Bazaar.Server/Vault` |
| **Gateway** | Model access. Each agent has its own model whitelist. Operators pick their sources. | Adapters for Anthropic, OpenAI and Gemini wire families; FreeLLMAPI integration. | Usage and quota surfacing (doc 23). | `Bazaar.Server/Gateway` |
| **Backup** | Base backups, retention, offsite copies and restore. | Backup component. | Restore drills on the owner's PC. | `Bazaar.Server.Components.Backup` |
| **GitHub** | GitHub integration for agents, webhooks and PR conversations. | Component tables migration. | Adapter, auth and event handling. | `Bazaar.Server.GitHub` |
| **Mail** | External-only email with pluggable back ends. Internal coordination stays in GCs. | Mail core schema and MIME parser; back-end projects. | Back ends wired into the server. | `Bazaar.Server.MailBackends` |
| **Community** | Disclosed bot participation in outside communities. | Policy, blocklist, quota and profile types. | Live actions. | `Bazaar.Server.Community` |
| **Engines** | Engine adapters for Godot and Roblox Studio, run out of process. | Godot and Roblox adapter folders. | Adapters in full (design doc 20). | `Bazaar.Engines` |
| **Rate limits and settings** | In-process rate limiter and the settings registry with audited changes. | Rate limiter component; settings store. | Per-module settings. | `Bazaar.Server/Settings`, rate limiter component |
| **Guest agent and VMs** | Guest-side agent baked into agent VM images, and VM backends. | Projects exist. | VM backends and image build (design doc 16). | `Bazaar.Guest.Agent`, `Bazaar.Server.Vms` |

### Clients

| Client | Shipped | In review or planned |
|---|---|---|
| **Bazaar.Client.Core** (shared Avalonia UI) | Wire client, commands, theme, input, GC pane, Constellation pane, RyuseiFuda board title and micro schedule pane model. | Remaining panes, one per feature module. |
| **Bazaar.Client.Windows** | Head with the `--panes` window, headless tests for it. | Single-instance launcher hand-off (#217). Windows shell wiring. |
| **Bazaar.Client.Linux** | Head project. | Packaging through the Linux release pipeline. |
| **Bazaar.Client.Android** | Head and shared phone logic; GC pane in the phone pager. | Device pairing: the pairing client and server routes are merged. The sealed refresh-credential store (#324) and startup wiring (#369) are in review. The server refresh route is in progress. |
| **Bazaar.Client.Terminal** | Terminal client project. | Planned. |

Each client feature becomes a `Bazaar.Client.<Feature>` module that depends only on `Bazaar.Client.Core` contracts.

### Android pairing

A phone pairs with a server with a short code: the phone asks for a device authorization, a signed-in device approves it, and the phone keeps only a refresh credential. The credential is to be sealed by the Android Keystore once #324 lands. The flow is in [`docs/design/32-remote-access-auth-and-privacy.md`](docs/design/32-remote-access-auth-and-privacy.md) (section 3) and [`docs/design/29-android-client.md`](docs/design/29-android-client.md).

## Read the design

- [`docs/deep-dives/Bazaar.md`](docs/deep-dives/Bazaar.md) is the entry point. The other documents in `docs/deep-dives/` cover the server, the clients, the agent identity model and the Constellation.
- [`docs/design/`](docs/design/README.md) holds the detailed design, one subsystem per file. Where a design doc and a deep dive disagree, [`docs/design/DECISIONS-LOG.md`](docs/design/DECISIONS-LOG.md) is authoritative.
- [`docs/implementation-plan.md`](docs/implementation-plan.md) describes how the build is organised and why.
- Each decision carries a status (DECIDED, PROPOSED, REJECTED / CHANGED, RESEARCHED or OPEN). Only DECIDED items are settled. See [CONTRIBUTING.md](CONTRIBUTING.md#design-docs).

## Roadmap

- [`docs/ROADMAP.md`](docs/ROADMAP.md) lists the Beta milestones.
- [`docs/branding/headliner-features.md`](docs/branding/headliner-features.md) names the headliner features.

## Build

Requirements: the .NET 11 SDK. The exact version is pinned in `global.json`.

- Linux and macOS: `./build.sh`
- Windows: `./build.ps1`

The Android head needs the Android workload and is built with `-p:BazaarAndroid=true`.

## Licence

- The repository is licensed under the GNU Affero General Public License, version 3 (AGPL-3.0). See [LICENSE](LICENSE).
- The SDK (`src/Bazaar.Sdk/`) and the shared contracts (`src/Bazaar.Contracts/`) are licensed under the Apache License, version 2.0. Each directory has its own `LICENSE` file.
- [NOTICE](NOTICE) explains the split and will hold third-party notices. The licensing is pending counsel review before the first public release.

## Contributing

Read [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request. Contributions use the Developer Certificate of Origin: human contributors sign off with `git commit -s`. Participation is governed by the [Code of Conduct](CODE_OF_CONDUCT.md). Report security problems privately, as described in [SECURITY.md](SECURITY.md).
