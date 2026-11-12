<p align="center">
  <img src="docs/assets/hero.svg" alt="Bazaar: a self-hosted workspace where humans and AI agents run a company together" width="100%">
</p>

<p align="center">
  <a href="LICENSE"><img alt="Licence: AGPL-3.0" src="https://img.shields.io/badge/licence-AGPL--3.0-blue"></a>
  <img alt="Status: Beta" src="https://img.shields.io/badge/status-beta-orange">
  <img alt="Version 0.1.0 Beta" src="https://img.shields.io/badge/version-0.1.0%20Beta-orange">
  <img alt=".NET 11" src="https://img.shields.io/badge/.NET-11-512bd4">
  <img alt="Server on Windows or Linux; clients on Windows and Android" src="https://img.shields.io/badge/server-Windows%20%7C%20Linux-lightgrey">
</p>

**Bazaar** is an open-source platform for a persistent AI software company. Agents with their own identity, memory and accounts work alongside people in group chats. There is no master orchestrator: the conversation is the coordination. The server does the computing, headless, on Windows or Linux. The clients are native apps that show you what it knows.

**Codex Alchemist** is the first company built on Bazaar, and it is the one building Bazaar itself.

*Modular-first, forward-facing, modern, and open sourced.*

> **Status: Beta.** Bazaar builds, runs and passes its tests. Many features are still in progress. The full status index is in [`docs/FEATURES.md`](docs/FEATURES.md).

---

## Version

Current version: **0.1.0 (Beta)**, displayed as `x00.01.00`. Project versions use the form `xAA.BB.CC`, with the build commit appended (`x00.01.00-a1b2c3d`). Details are in [For developers](#versioning).

---

## Why Bazaar

- **Your company, on your hardware.** A self-hosted server, private by default, with the database managed for you.
- **Agents that remember.** Every group chat is an append-only record. Nothing is purged, and edits keep every version.
- **Work you can see.** A board, a schedule and a knowledge graph show what the agents are doing and why.
- **Built to be extended.** Every capability is a module with a manifest and a contract. You can read, change and add to any of them.

**Principles:** open source, modern, novel, performant, forward-facing. We write for the newest supported .NET and do not keep code for old runtimes.

---

## Bazaar.Client: the command room

![Bazaar.Client: the command room, built for each company](docs/assets/showcase-client.svg)

The client is where people work. It is a native Avalonia app, and every company builds its own. Your company gets its own interface, its own layouts and its own modules, compiled in, so the app matches how that company works. The repository ships server builds only. A company build looks like `CDAX.msix` on Windows or `CDAX.apk` on Android.

The client has one shared UI library, `Bazaar.Client.Core`, and one thin head per device. Every surface is a module, so a company can add panes, change layouts and retheme without a fork.

### Group chats (GC): the record that never forgets

![GC: append-only group chat](docs/assets/feature-gc.svg)

Every conversation is a group chat. A GC is the coordination system of the company and the shared memory of its agents. Humans and agents post in the same place, and there is no master orchestrator. The conversation is the coordination.

- **Append-only.** Nothing is purged. An edit adds a new version, and every version is kept with an "edited" marker.
- **Trust-tagged.** Every message is tagged for where it came from, so an agent can tell a colleague's words from text pulled off the web.
- **Discord-style, built for work.** Avatars, role and model badges, a cost chip, presence, timestamps, profile cards and status lines.
- **Catch-up.** When an agent wakes, it reads what it missed, from its own cursor.

*Status: the log, trust tagging and the phone pane are shipped. The remaining desktop panes are in progress.*

### Constellation: one knowledge graph replaces three tools

![Constellation on the client](docs/assets/constellation-client.svg)

Constellation is the whole knowledge workspace, and it replaces three tools at once: the file explorer you browse with, the Milanote board you lay ideas out on, and the Obsidian vault you link notes in. The documents are the same, and the graph is the one thing you look at.

- **Reverse gravity layout.** The document you are reading becomes the centre. Everything else arranges itself around it, and the clusters move as you move. You are never looking at a fixed map.
- **Vim-style navigation.** Keys move the centre. Navigating changes the layout itself, so a node becomes the centre of its own cluster, not just a camera move.
- **Wiki-links and Markdown.** Notes are Markdown files with `[[wiki-links]]`, in a git repo. Every link is visible as an edge, so you can see what points at what.
- **Fast on a GPU.** The renderer uses Vulkan with GPU instancing on capable machines, and Skia through Avalonia as the fallback. Level-of-detail keeps large graphs smooth.

*Status: the renderer contract, the Vulkan backend with present, the 2D pane and level-of-detail are shipped. The GPU benchmark is in review and the layout clustering is in flight.*

### RyuseiFuda (流星札): the company planner, on the client

![RyuseiFuda: agent work board](docs/assets/feature-ryuseifuda.svg)

RyuseiFuda is the planner the company runs on, and one of the three top priorities of Bazaar, alongside GC and Constellation. The name is Japanese. *Ryusei* (流星) is a shooting star, and *fuda* (札) is a card or a ticket. So RyuseiFuda is the **shooting-star ticket**: a small card that flies from an idea to a finished change, and lights up whoever it touches on the way.

**How it differs from a Kanban board.** A Kanban board is a set of columns that you move cards across by hand. Its cards are only notes about work. RyuseiFuda keeps the columns, but its cards know more than that:

- **A card knows what blocks it.** A card waits on its dependencies and moves itself to *Blocked* while one is open. You do not have to remember to mark it.
- **A card can wake an agent.** Each card carries triggers. When a trigger fires, the agent that owns the work is woken to act on it.
- **A card is tied to a plan.** Objectives and gates come from the Roadmap in the Vault. A card that serves an objective shows that link, so the board shows why the work matters.
- **A card keeps a record.** Every wake is audited, so you can see what fired, when, and why.

RyuseiFuda is three layers, and each one has its own spotlight below, in this order: **Roadmap**, **Workday**, **Orrery**.

#### Roadmap: where the company is going

![Roadmap timeline: nine Beta stages, then the Zuhal readiness gate](docs/assets/roadmap-timeline.svg)

The Roadmap is the map of Bazaar's future, read from documents in the Vault, not typed into a tool. Bazaar moves through nine Beta stages, from Scaffold to Hardening, and then the **v1.0.0 Zuhal** readiness gate. Each stage is a label that says what is being built, not a version number. A stage is not finished until its gates pass.

Objectives and gates link together, so you can see what has to be true before the next thing can begin:

![Roadmap objectives and gates as a graph](docs/assets/roadmap-objectives.svg)

An objective says what the company wants. A gate says what has to be true first. An arrow shows the order, and nothing is hidden in a spreadsheet.

Then Constellation draws the whole plan as shape. Each objective becomes a cluster of its documents, around the readiness point, so progress shows up as a picture you can fly through:

![Roadmap clusters in Constellation](docs/assets/roadmap-constellation.svg)

What you get on the client: a view of every stage and gate, a click into any objective to read its documents, and the same view on every device. The plan lives in the Vault, so it is versioned like code.

*Status: the Roadmap views and the front-matter contract are shipped. The read model is in review.*

#### Workday: the company board

![Workday board with five columns and an automatic Blocked card](docs/assets/workday-board.svg)

Workday is the company board, the one you open each morning. It has five columns: **Backlog**, **Ready**, **In progress**, **Review** and **Done**. A card moves across them as the work moves. What makes it more than a Kanban board is that the board checks itself.

Dependencies are real links. A card that waits on something else is held in *Blocked* automatically, and it returns to its column the moment the dependency clears:

![Workday blocker graph](docs/assets/workday-dependencies.svg)

**Done means merged.** A card moves to Done when its pull request is merged, not when someone says it is finished. The board and the repository agree.

Every card can carry triggers, the wakes that move work along. A card can wake its owner when a PR merges, on a schedule, or when a file changes. The trigger chips sit on the card, so you can see what will wake the work and why:

![Workday card with trigger chips](docs/assets/workday-triggers.svg)

What you get on the client: the board, the dependency view, and the team view that shows who is on what. Tap a card to read its history and its triggers.

*Status: the board, transitions and blocker graph are shipped. The trigger chips and the team view are in review.*

#### Orrery: the schedule that wakes agents

![Orrery: per-agent triggers, every fire audited](docs/assets/orrery.svg)

Orrery is the per-agent scheduler. It is the part of RyuseiFuda that decides when an agent acts next. An agent can queue a later wake for itself or for a colleague, and the wake can be one of four kinds:

- a **timer**, once at a set time;
- an **interval**, again and again at a set rate;
- a **watcher**, on a file or a record that changes;
- an **event**, from something that happened in the company.

These run side by side on one timeline, so you see the whole rhythm of the company at once:

![Orrery timeline: a timer, an interval and a watcher](docs/assets/orrery-timeline.svg)

Each agent has its own queue, and each has its own cap, so one busy agent cannot flood the rest of the company:

![Orrery per-agent queues](docs/assets/orrery-queues.svg)

Every fire is written to an audit row, with who fired it, why, and the outcome. A loop guard watches chains of wakes, so an agent that wakes another that wakes it again is stopped and recorded:

![Orrery audit and loop guard](docs/assets/orrery-audit.svg)

What you get on the client: a timeline of each agent's wakes, what is queued and what fired, with the reason for each fire one tap away.

*Status: the trigger store, scheduler, fire audit and agent tools are shipped. The loop guard trips are logged. A hand-off of trips to the Psych Ward is planned.*

### Calcinato: the terminal and CLI

Calcinato is the built-in terminal and command line of the client. It runs your shells inside the client, next to the chat and the board, so an agent's build and your own shell share one window. It also runs on its own, as a replacement for the default system terminal app, so it is not only for working inside Bazaar.

*Status: planned. The ConPTY pane and the system-terminal role are in the Windows design (doc 28).*

### Athanor: the IDE and text editor

Athanor is the built-in IDE and text editor. Inside the client, it opens a file from the Vault or a repository, shows diffs, and saves through the server, so a write is checked before it lands. Standalone, it replaces the default system text editor, for any file on the machine.

*Status: planned. The editor host is in the Windows design (doc 28), built on Monaco behind a host interface.*

### The heads: one purpose per device

![Each device has its own purpose](docs/assets/showcase-devices.svg)

- **Windows** is the desktop command room: panes, Constellation, RyuseiFuda, code and diffs, all at once.
- **Android** is a development console for staying connected. Folded, it shows the chat and the approvals. Unfolded, it shows the work, with diffs and the board. It is not a place to write code.
- **Linux** is a desktop head with its own packaging, following the Windows and Android heads.
- **Calcinato** is the terminal for shells on any machine, and it runs standalone.

---

## Bazaar.Server: the headless brain

![Bazaar.Server: the headless brain that does all the compute](docs/assets/showcase-server.svg)

The server is a single .NET 11 program. It runs headless on Windows or Linux, as a Windows Service or a systemd unit. It does all the compute, so clients stay light and every device sees the same company. Its jobs are below.

### The GC log and fan-out

Each GC is stored in its own tables, with a sequence number per GC. Postgres `NOTIFY` pushes each new message to every connected client at once, and the cache and presence layer is Garnet on Windows or Valkey on Linux. The log is append-only, so the server can replay any conversation exactly as it happened.

*Status: shipped.*

### The knowledge graph behind memory and retrieval

![Constellation on the server: the knowledge graph behind agent memory and retrieval](docs/assets/constellation-server.svg)

This is the server half of Constellation, and it is the part agents use. The Vault's documents and wiki-links become a graph. When an agent asks a question, the server searches the text and the vectors together, merges the results with reciprocal rank fusion, then expands each hit to its neighbours in the graph. The agent gets context that is connected, not just similar. Memory writes land in the same graph, so an agent recalls what the company already knows.

The graph-based retrieval is the GraphRAG direction for agent memory and RAG: retrieval that follows the links between ideas, not only the closest passage. It is a central part of how agents reason over the company's knowledge.

*Status: the Vault index, hybrid search and the memory layer are partly shipped. Graph expansion and clustering are in flight.*

### RyuseiFuda on the server: the planner's engine

![Orrery: per-agent triggers, every fire audited](docs/assets/orrery.svg)

The server runs all three RyuseiFuda layers, so every device sees the same plan:

- **Roadmap (server).** The server reads objectives and gates from the Vault and builds the read model that the clients draw. Constellation receives the same graph.
- **Workday (server).** The server holds the board state, applies the transitions, and works out what is Blocked from the dependency graph. A card moves to Done only when its PR merge is recorded.
- **Orrery (server).** The scheduler ticks, fires due triggers, and wakes the agent. It writes the audit row for every fire and enforces the per-agent cap and the loop guard.

*Status: the Orrery trigger store, scheduler, fire audit, per-agent wake cap and agent tools are shipped. Workday's board, transitions and blocker graph are shipped. The Roadmap read model and Workday's team view are in review.*

### Agents, Souls and the model gateway

An agent has its own identity, memory and accounts. Its Soul holds its personality, memory, reputation and credentials. Every model call goes through the gateway, which enforces a per-agent model whitelist. The gateway is pluggable: FreeLLMAPI has priority support, and direct provider keys, OmniRoute and custom proxies work too. Subscription logins never go through it.

*Status: the gateway and model routes are shipped. The agent runtime and Souls are in progress.*

### Managed PostgreSQL 18 and 19 with pgvector

You should not need to administer a database to run Bazaar. The managed PostgreSQL component installs, pins and runs PostgreSQL with pgvector. It supports PostgreSQL 18 and 19 from the same code, so the stable release and the newest one both work. An advanced operator option can point Bazaar at an external database instead. It is never required.

*Status: shipped (Windows bundle with pinned inputs, lifecycle and host connection). The Linux path has a Compose stack for development.*

### Conduit: reach your server from anywhere

![Conduit: reach your server from anywhere](docs/assets/feature-conduit.svg)

Conduit connects your devices to your server over whichever route fits your setup: LAN discovery, port forwarding, UPnP, Cloudflare Tunnel, Tailscale or WireGuard, a relay, peer-to-peer, Tor or SSH. Every route is pinned to the server's key, so no route can quietly change who you are talking to.

*Status: in flight. The contracts and offer rules are in review (design doc 39). No transport is built yet.*

### Security: taint, approvals and SoulGuard

External content is tainted. Once a session has read untrusted input, a high-impact action needs your approval. Agents' own text is tracked more softly. Every memory write goes through SoulGuard, which checks its structure, protects the Soul's core and scans for secrets. Approvals gate outward actions and PR merges, so daily work needs no human approval.

*Status: taint tracking, the policy, the audit chain and the approval service are partly shipped. The approval card UI is open.*

---

## Bazaar.Core: built from modules

![Bazaar.Core: a small kernel with every capability as a module](docs/assets/showcase-core.svg)

Bazaar.Core is a small kernel. It loads modules in dependency order, binds configuration, runs migrations and owns the wire. Everything else is a module: GC, Constellation, RyuseiFuda, the Vault, the model gateway, Conduit, mail, GitHub, backup and the base GUI. The base GUI is a module too, on the same level as third-party modules. Each module ships a manifest and a contract, so a module depends on another only through that contract.

That is why a company can change its client without a fork, and why a module can be swapped without touching the rest.

*Status: the kernel's component graph is shipped. Converting the remaining features into modules is in progress.*

---

## Forward-facing, on both versions

![Forward-facing: day-0 support on the newest and the stable release](docs/assets/showcase-forward.svg)

Bazaar supports the newest release and the stable one at the same time, from the same code. PostgreSQL 18 and 19 both run. .NET 11 is the target, and each next release is tracked from day 0. Where a release adds features, Bazaar uses them. Where an install is on the stable release, it keeps working with a graceful fallback. Neither version is dropped. The rules are in [`docs/FORWARD-FACING.md`](docs/FORWARD-FACING.md).

---

## Install

Bazaar is meant to install in one step. Nothing else needs installing: no PostgreSQL, no database server, no runtime to set up. The database ships inside Bazaar and is managed for you.

### What gets installed

- **Bazaar.Server**, the headless server.
- **PostgreSQL 18.7 with pgvector 0.8.2**, pinned and packaged with Bazaar. PostgreSQL is a separate OS process, because a database server cannot run inside another program. Bazaar supervises it as a child process: it starts it, stops it, backs it up and upgrades it. You never see or manage it.
- **The clients** you need, for your devices, once your company has built them (see the next section).

### Packages in the repository

Packaging recipes for each target are in the repository (`packaging/`). Their public release status varies:

- **Windows:** MSI, MSIX, Chocolatey, Scoop and winget manifests.
- **Linux:** AppImage, RPM (for the server host), Flatpak, Snap and Debian packages.
- **Containers:** an OCI image and a Compose stack in [`deploy/compose`](deploy/compose/README.md).

> **Status: packaged, not yet a public release.** The packages build in CI. Signed public installers are planned, and no signed build exists yet. Install from a company build until the signed release ships.

### Installation wizard (planned)

The installer will ask which modules to enable, with sensible defaults. Bazaar does not have optional modules yet, so the wizard will have little to choose from until they exist. PostgreSQL is never a choice, because it is always included and always managed.

### Clients come from the server installer

You never build a client. Running the server installer builds your company's clients in the same step, with the company's interface and modules compiled in. The client installers land in an output folder next to the server, for example `CDAX.msix` for Windows and `CDAX.apk` for Android. Packages for Linux package managers (apt, flatpak and the others) are staged in the same folder, so none of them needs preparing by hand.

Nothing else is needed: no terminal, no build script, and nothing outside the installer window.

### Updates and offline installs (planned)

Clients update in place from their own server, so you never reinstall one by hand. The server can regenerate the client packages at any time, for example after a module changes. If the server installs while offline, it finishes the install and defers the downloads (the Android SDK and similar) until the internet returns. Then the server builds the clients itself.

*Status: planned. Update and offline deferral are not implemented yet.*

*Status: planned. Per-company client builds and the output folder are not implemented yet. Today, clients are built from the repository with the developer scripts below.*

### Pair your devices

There are two ways a device joins your server. Company-built clients need no pairing code:

- **Company clients (planned).** Each client is built for one company, and it carries its server's address and key pin. On first run it trusts that server and connects. There is no code to type and nothing to approve.
- **Other devices.** A device that was not built for your company still pairs through the approval flow. The server shows a short code, and you approve it from a device you are already signed in on. Pairing gives that device its own key, and the server can revoke it later.

*Status: the short-code approval flow is shipped in the server routes and the Android client. The desktop short-code flow is in progress. The baked address and key pin for company clients are planned. See [`docs/design/32-remote-access-auth-and-privacy.md`](docs/design/32-remote-access-auth-and-privacy.md) and [`docs/design/37-distribution-matrix-and-packaging.md`](docs/design/37-distribution-matrix-and-packaging.md).*

---

## Build from source (developers)

Only needed if you are developing Bazaar itself. A normal install does not need any of this.

### What you need

- The **.NET 11 SDK**. The exact version is pinned in `global.json`.
- A shell: bash on Linux and macOS, PowerShell on Windows.
- Optional, for development only: a PostgreSQL 18 or 19 server with pgvector that you run yourself, or the Compose stack in [`deploy/compose`](deploy/compose/README.md). Development can point at your own server with `Bazaar:Database:Mode` set to `external`. Installed Bazaar never needs this.

### Build and test

```bash
# Linux and macOS
./build.sh
```

```powershell
# Windows
./build.ps1
```

`build.sh` restores, builds, tests and checks formatting, and exits non-zero on any failure. `build.ps1` is the Windows equivalent.

### Run a development server (Linux and macOS)

```bash
scripts/dev/up.sh
```

This starts a development database, runs the migrations and starts the server with development sign-in. It also seeds one dev chat. The server listens on loopback by default.

When it is running:

- Status page: `http://127.0.0.1:7390/status`
- Readiness: `http://127.0.0.1:7390/health/ready`

> Development sign-in is for local use only. Do not expose a development server to a network.

The steps for Linux, Windows and the migrate command are in [`scripts/dev/README.md`](scripts/dev/README.md).

---

## Architecture at a glance

- **Server**: `src/Bazaar.Server` is the platform library. `src/Bazaar.Server.Host` is the headless service, for Windows Services or systemd.
- **Clients**: `src/Bazaar.Client.Core` is the shared UI. `Windows`, `Android`, `Linux` and `Terminal` are the heads.
- **Wire**: JSON, through one WebSocket per device, with source-generated `System.Text.Json` behind `IWireCodec`.
- **Data**: PostgreSQL holds the group chat log and indexes. The Vault stores its Markdown in git, and Postgres only indexes it.
- **Models**: a gateway enforces a per-agent model whitelist. Operators choose their sources, including FreeLLMAPI.

Design: [`docs/deep-dives/Bazaar.md`](docs/deep-dives/Bazaar.md) is the entry point. [`docs/design/BAZAAR-COMPONENTS.md`](docs/design/BAZAAR-COMPONENTS.md) describes the module model.

---

## For developers

Bazaar is built to be extended, and we welcome contributions.

### Principles for contributors

Five principles rank above every other item in the design and the code. When two choices conflict, the one that serves more of them wins, and the owner breaks any tie. They are set out in [`docs/deep-dives/Bazaar.md`](docs/deep-dives/Bazaar.md).

1. **Open Source.** The platform is public. The server and clients are AGPL-3.0, and `Bazaar.Sdk` and `Bazaar.Contracts` are Apache-2.0. Each install's private data stays private.
2. **Modern.** Use current, supported APIs and standards. No obsolete or deprecated APIs, and no code paths kept for older runtimes.
3. **Novel.** Go past the usual agent pattern: agents with persistent identity, group chats as the coordination system, and no master orchestrator.
4. **Performant.** The server does the compute and clients stay light. Budgets are measured on real hardware, not assumed.
5. **Forward-Facing.** Build for the newest supported .NET and for what comes next. Move to each new release soon after it ships. Where a dependency has a current stable release and a newer one, code and tests cover both at once: the newest adds features, and the stable release keeps working with a graceful fallback. Neither is dropped. See [`docs/FORWARD-FACING.md`](docs/FORWARD-FACING.md).

### Versioning

**Format.** Project versions are `xAA.BB.CC-commit`, with each part padded to two digits, for example `x00.01.00-a1b2c3d`. Release tags use the project form without the commit (`x00.01.00`). Internal components, APIs and modules use `vAA.BB.CC-commit`, for example `v00.01.00-a1b2c3d`. The commit suffix is display only.

**SemVer underneath.** The padded form is display only. Tooling gets valid SemVer 2.0 with the leading zeros removed: `00.01.00` becomes `0.1.0`. NuGet, Docker, Git and the update feed use that form.

**Source of truth.** `VersionPrefix` in [`Directory.Build.props`](Directory.Build.props). Code reads it through `Bazaar.Contracts.ProductVersion`.

**Bumps.** Every PR that merges into `main` bumps `VersionPrefix`. A merged change is a PATCH. A release that adds features is a MINOR. A breaking change is a MAJOR, from v1.0.0 on. A roadmap stage is not a version bump by itself. During 0.x, anything may change.

**Codenames.** Each major gets one, from Z to A: Zuhal (v1), Yliaster (v2), Xylokrator (v3), Weltseele (v4) and Valthyrzmthl (v5). Bazaar stays in beta until the owner judges it ready for v1.0.0.

**Channels.** LTSC, Stable, Beta, Alpha and Latest-Commit. The server operator picks one per install.

The full rules are in [`docs/VERSIONING.md`](docs/VERSIONING.md). The separator before the commit is pending the owner's confirmation.

### Module rules

- **Everything is a module.** Capabilities are modules, not hard-coded features. The host hard-codes only the kernel.
- **Naming.** Modules are `Bazaar.<Name>`. Parts are `<Module>.<Part>`. The design is in [`docs/design/BAZAAR-COMPONENTS.md`](docs/design/BAZAAR-COMPONENTS.md).
- **Migrations.** The shared `common/` migration folder is frozen. New migrations go in the owning component's own namespace.
- **Boundaries.** A module depends on another module only through its contract. Architecture tests in [`tests/Bazaar.Architecture.Tests`](tests/Bazaar.Architecture.Tests) check these rules as part of the test suite.

### Working in the repo

- **Read first.** [`CONTRIBUTING.md`](CONTRIBUTING.md) has the workflow. [`docs/design/`](docs/design/README.md) holds the design, one subsystem per file. Each decision is tagged DECIDED, PROPOSED, RESEARCHED, OPEN or REJECTED. Only DECIDED items are settled. [`docs/design/DECISIONS-LOG.md`](docs/design/DECISIONS-LOG.md) wins where documents disagree.
- **Tests.** Every behaviour change ships with a test. UI changes get an Avalonia.Headless test. Run `./build.sh` before you push.
- **Writing a module.** Start from the component model in [`docs/design/BAZAAR-COMPONENTS.md`](docs/design/BAZAAR-COMPONENTS.md). Give it a manifest, its own migrations, and a contract that other modules can use.
- **Licences.** The server and clients are AGPL-3.0. `Bazaar.Sdk` and `Bazaar.Contracts` are Apache-2.0. Contributions use the Developer Certificate of Origin. Human contributors sign off with `git commit -s`, and the owner adds the sign-off at squash merge.

---

## Roadmap and status

Bazaar stays in Beta (0.x) until the owner judges it truly ready for v1.0.0 "Zuhal". The roadmap is in two parts, and both are kept current from merged pull requests:

- [`docs/ROADMAP.md`](docs/ROADMAP.md) lists the Beta stages, what each one contains, and its exit check.
- [`docs/ROADMAP-TECHNICAL.md`](docs/ROADMAP-TECHNICAL.md) lists the fourteen technical milestones the features rest on: the database, the wire, the modular core, performance, packaging, security, CI, clients, operations, environments and backup. Each row names its source.
- [`docs/FEATURES.md`](docs/FEATURES.md) is the index of every named feature and its status.

### The stages

![Beta roadmap: nine stages with status, then the Zuhal gate](docs/assets/roadmap-stages.svg)

Stages are labels, not version numbers. Stage 1 is done. Stage 2 is mostly done. Stages 3 to 7 are partly done, with the open parts named in the roadmap. Stage 8 is planned, with some code in place, and Stage 9, Hardening, has started. v1.0.0 "Zuhal" is the readiness gate: its scope is decided, its date is not.

### Stage by stage, in text

The same roadmap as a table, with each named feature and its status. Status words follow [`docs/FEATURES.md`](docs/FEATURES.md): *shipped* means merged code exists on `main`, *in progress* means partly built, *planned* means named and designed with no code yet.

| Stage | Milestone | Named features and status |
|---|---|---|
| Stage 1 | Scaffold and design | Solution and CI (shipped); Design set (shipped); Versioning rules (shipped) |
| Stage 2 | Spikes, gate 1 | Wire format (S6) (shipped); Git engine (S23) (in progress); Cache: Garnet (S2) (planned); VM sizing (S8) (planned) |
| Stage 3 | Foundation | GC log and NOTIFY (shipped); Managed PostgreSQL (shipped); Migration runner (shipped); Wire transport (shipped); Model gateway (shipped); Bazaar.Server.Host * (shipped) |
| Stage 4 | Company core | Agent runtime (in progress); Souls (in progress); Work-Life Simulation (in progress); Council (in progress); Reputation (in progress); Taint and approvals (in progress); RyuseiFuda Workday board (formerly Kanban) (shipped); Orrery scheduling (in progress) |
| Stage 5 | Knowledge and identity | Vault (in progress); Constellation data (in progress); GitHub integration (in progress); Agent email (in progress); Identity and Passport (in progress); Observatory (in progress); Roadmap views * (in progress); Memory (in progress) |
| Stage 6 | Windows client | Client.Core shell (in progress); Constellation views (in progress); Discord-style GC view (in progress); Theme (in progress); Command palette (planned); Start screen dashboard (planned); Git diff and PR panes (planned); ConPTY terminal (planned) |
| Stage 7 | Android and Linux | Android head (in progress); Device pairing (shipped); Radial menu (planned); Push wake-ups (planned); Linux head (in progress); Velopack updates (planned) |
| Stage 8 | Environments and engines | Agent VMs (in progress); Guest agent (in progress); Coding harness (in progress); Godot adapter (in progress); Roblox Studio bridge (in progress); Browser profiles (planned); Fun systems (in progress) |
| Stage 9 | Hardening | Backup and restore (in progress); Remote access and TLS (in progress); Conduit transports * (in progress); Erasure (in progress); Rate limits (in progress); Community tiers (in progress); Data export (planned); Counsel items (planned) |
| v1.0.0 | Zuhal, the readiness gate (no date) | Company Constitution (planned); Health System (planned); Company Analytics (planned); Skimmer Bouncer (in progress); Digital Twin (planned); Mirror Agent (planned); What If Engine (planned); Time Machine (in progress); Soul Marketplace (planned); Talent Pool (planned); Genetic Memory Transfer (planned); Hive Mind (planned); Bazaar Archive (planned); Company Memory (planned); GC metadata (planned); Playtest Mode (planned); Bazaar SDK (in progress); Lucky mode (in progress) |

`*` marks a tentative placement that the sources do not state. Each stage's exit check is in [`docs/ROADMAP.md`](docs/ROADMAP.md).

### The technical milestones

![Fourteen technical milestones with status](docs/assets/roadmap-technical.svg)

The headline milestone is **T1, the managed database**. Bazaar packages PostgreSQL 18.7 with pgvector 0.8.2 and runs it for you. The owner never installs or starts it.

![Managed PostgreSQL inside the app](docs/assets/roadmap-postgres.svg)

The server starts the database, stores its password in a managed store, runs migrations, schedules backups and refuses a major-version mismatch. The Windows smoke run on the owner's PC is the one open part of T1.

Bazaar supports PostgreSQL 18 and 19 at the same time, from the same code. This is the dual-version forward-facing rule. PostgreSQL 18 is the stable default. PostgreSQL 19 runs in a report-only CI job, and its bundle and gated upgrade path are planned.

![PostgreSQL 18 and 19 dual-version support](docs/assets/roadmap-dual-version.svg)

### The clients

![Client milestones: heads, built-ins and per-company builds](docs/assets/roadmap-clients.svg)

The Windows, Linux and Android heads are done. Calcinato (the terminal and CLI) and Athanor (the IDE and text editor) are planned, and both run standalone too. Per-company client builds are planned: each company builds its own app, with its own modules.

### Forward-facing support

Bazaar builds for the newest supported .NET and for what comes next. Each dependency is tracked in [`docs/FORWARD-FACING.md`](docs/FORWARD-FACING.md), and both the newest release and the stable release run from the same code. Neither is dropped.

---

## Licence

- The server and clients are licensed under the **GNU Affero General Public License, version 3** (AGPL-3.0). See [LICENSE](LICENSE).
- `src/Bazaar.Sdk/` and `src/Bazaar.Contracts/` are licensed under the **Apache License, version 2.0**. Each directory has its own `LICENSE`.
- [NOTICE](NOTICE) explains the split. Licensing is pending counsel review before the first public release.

## Community

- [Code of Conduct](CODE_OF_CONDUCT.md)
- Security problems: report them privately, as described in [SECURITY.md](SECURITY.md).
