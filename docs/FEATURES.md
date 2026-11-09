# Bazaar named features

Status: DRAFT (2026-11-09). A compiled index of every named feature, component, codename and module the repo, the design set and the project plans mention. It is a map, not a decision record: each row points to the doc that owns the detail.

## How to read the status column

| Status | Meaning |
|---|---|
| **Shipped** | Merged code exists on `main` for the feature. Coverage may still be partial; see the note. |
| **Partial** | Some merged code exists, but a named part is missing or still open. |
| **Planned** | Named and, usually, designed. No code on `main` yet. |
| **Rejected** | Decided against. Kept so the name is not reused by accident. |
| **Unclear** | The name, meaning, status or owner is not settled in the sources. Not guessed. |

Design-decision tags (DECIDED, PROPOSED, OPEN, REJECTED, RESEARCHED) come from `docs/deep-dives/Bazaar.md` and `docs/design/DECISIONS-LOG.md`. Only DECIDED items are settled. Where a design tag and a code status differ, the row shows both.

Code status is judged from the source tree and merged PR titles at `origin/main` (commit `b9ccc1c`, 2026-11-09). Open pull requests were not re-checked live, so a row marked Planned may already have an open PR.

---

## 1. Platform and company

| Name | What it is | Status | Owning doc or code |
|---|---|---|---|
| Bazaar | The open-source platform for a persistent AI software company. Agents with their own identity, memory and accounts coordinate in group chats, with no master orchestrator. | Shipped (partial product) | `docs/deep-dives/Bazaar.md` |
| Codex Alchemist | The first company built on Bazaar, and the developer of the Bazaar platform. The name "Codex" overlaps OpenAI's product and has not been cleared. | Shipped (as the owner's instance) | `docs/deep-dives/Bazaar.md` §1 |
| Bazaar Components | The modular model: every capability is a component with a manifest and contracts. The kernel is the only hard-coded part. Wave 0 is in code; later waves are open. | Partial | `docs/design/BAZAAR-COMPONENTS.md`, `src/Bazaar.Server/Components` |
| Bazaar Kernel | The component runtime: discovery, graph validation, ordering and lifecycle. | Partial (`Components/ComponentGraph.cs`, `IBazaarComponent.cs`) | `docs/design/BAZAAR-COMPONENTS.md` §9 |
| Bazaar.Server | The headless, OS-agnostic .NET 11 server that does all the compute. | Shipped | `docs/deep-dives/Bazaar.Server.md`, `src/Bazaar.Server` |
| Bazaar.Server.Host | The host executable: service host, options, status listener. | Shipped | `src/Bazaar.Server.Host` |
| Bazaar SDK (`Bazaar.Sdk`) | One shared API every subsystem uses. Apache-2.0. | Partial (project exists) | `src/Bazaar.Sdk`, `Bazaar.Server.md` |
| Bazaar.Contracts | Shared wire and contract types for client and server. Apache-2.0. | Shipped | `src/Bazaar.Contracts` |
| Company Constitution | The `/company` docs: culture, principles, mission and decision records that every Soul onboards with. | Planned (v1 scope). No text drafted yet. | `docs/design/24` §1 |
| Org Chart | Relationship structure for the company, not a rigid hierarchy. | Partial (`src/Bazaar.Server/Org`) | `docs/design/24` §2 |
| Interrupt System | A formal interrupt and resume stack for work. | Partial (`src/Bazaar.Server/Interrupts`; stack service merged) | `docs/design/24` §3 |
| Company Calendar | Internal planning, release and vacation calendar. | Partial (`src/Bazaar.Server/Calendar`) | `docs/design/24` §4 |
| Company Memory | Institutional memory and decision chains, kept in the Vault (`company/memory/`). | Planned (v1 scope) | `docs/design/08` §6, `docs/design/25` |
| Health System | Per-project tech debt, morale and coverage metrics. | Planned (v1 scope) | `docs/design/25` |
| Company Analytics | Per-agent metrics, framed as "improve the company". | Planned (v1 scope) | `docs/design/25` |
| Observatory | Live dashboard of what each agent is doing, plus which model lanes are near a limit. | Partial (`src/Bazaar.Server.Community/Observatory`; usage rollup in flight) | `docs/design/23` |
| Work-Life Simulation | Shifts, breaks, vacations and curfews, used instead of rate limits. Each agent has its own timezone and schedule. | Partial (`src/Bazaar.Server/WorkLife`; schedule calculator merged) | `docs/design/09` |
| Resource Economy | Would have been money budgets and a wallet. | Rejected 2026-10-24. Limits are quotas and the Work-Life Simulation. | `Bazaar.md` §4 |
| Office Spaces | A fixed floor plan. | Rejected. GCs self-organise. | `Bazaar.md` §4 |

## 2. Communication

| Name | What it is | Status | Owning doc or code |
|---|---|---|---|
| GC (Group Chat) | The coordination system. Append-only and never purged; edits keep every version. | Shipped (log, read, replay, edits) | `docs/design/01`, `src/Bazaar.Server/Gc` |
| GC log and NOTIFY fan-out | The GC message log in its own tables, with Postgres NOTIFY for fan-out. | Shipped | `docs/design/04`, `src/Bazaar.Server/Data` |
| Thinking GC | A nested reasoning GC for each agent inside each GC it works in. The owner always reads it. | Planned | `docs/design/01` (round 3) |
| Sidewalk | The casual, non-code GC mode. | Planned (name decided; behaviour proposed) | `docs/design/26` §8 |
| DM | A two-member GC. The owner reads all of them. | Partial (two-member GCs) | `Bazaar.md` §2 |
| Skimmer Bouncer | A cheap gatekeeper model that skims GC messages and wakes the full agent only when relevant. Starts defer-only, so it never drops a message. | Partial (`src/Bazaar.Server/Skimmer`: rules, scoring, digest, labels) | `docs/design/13` |
| Nigerian Prince Protocol | An optional scam-baiting protocol behind a setup wizard. Without it, scam emails are quarantined. | Partial (`MailBackends/ScamBait`); optional, not yet behind the wizard | `Bazaar.md` §2 |
| Agent email | Each agent's external email address, on a domain the operator configures. External only. | Partial (`src/Bazaar.Server.MailBackends`: Cloudflare, generic, scam-bait; built-in SMTP/IMAP server blocked on doc 22 Q2) | `docs/design/22` |
| Nervous System | An internal event bus whose events drive software reactions. It never replaces GCs. | Planned | `Bazaar.md` §2 |
| Inbox | Per-agent inbox with priority. | Shipped (`src/Bazaar.Server/Agents/Inbox`) | `docs/design/06` |

## 3. Agents, identity and governance

| Name | What it is | Status | Owning doc or code |
|---|---|---|---|
| Soul System | An agent's complete identity: VM, personality, memory, reputation and accounts. | Partial (`src/Bazaar.Server/Souls`: contracts, versions, versioning service) | `docs/design/07`, `docs/deep-dives/Bazaar.Soul.md` |
| Soul Passport | The credential registry inside a Soul. Mountable per agent, scoped to that agent only. | Partial (`src/Bazaar.Server/Identity`) | `docs/design/14` |
| Identity Service | The single chokepoint for every credential and access request. Holds all provider and GitHub App keys. | Partial (`src/Bazaar.Server/Identity`, `Secrets`) | `docs/design/14` |
| Soul Marketplace | An optional marketplace for sharing preset Souls. Behavioural presets only, never personal data. | Planned. Nature decided; v1 scope OPEN. | `docs/design/07` |
| Talent Pool | A library of reusable subagent templates. Hiring runs as a Council vote. | Planned. Blocked on a Council motion-class follow-up (C32). | `docs/design/25`, `docs/design/10` |
| Genetic Memory Transfer | Temporary subagents pass candidate lessons back before they end. Candidates need review before entering a Soul. | Planned | `docs/design/07`, `docs/design/25` |
| Council | Whole-team voting and debate. Votes are independent first, then revealed together. Ties go to the owner. | Partial (`src/Bazaar.Server/Council`: lifecycle, tally, eligibility, pilot) | `docs/design/10` |
| Reputation | Scores that combine peer review with hard outcomes (CI, reverts, owner accept or reject). | Partial (`src/Bazaar.Server/Reputation`; weights OPEN) | `docs/design/11` |
| Psych Ward (The Asylum) | The recovery workflow for stuck or spiralling agents. Loop-guard trips go there. | Partial (`src/Bazaar.Components.Fun/Ward`; workflow semi-serious and PROPOSED) | `docs/design/26` §1, `docs/design/06` |
| Vent post | A mandatory, public reflective post after a failure. | Partial (`Fun/Vent`) | `docs/design/26` §2 |
| Loop guards | Server-side counters that stop agents looping, tripping into the Psych Ward. | Partial (`src/Bazaar.Server/Agents/LoopGuard`) | `docs/design/06`, `docs/design/09` |
| Agent runtime and turn loop | The wake model, turn path and agent tools. | Partial | `docs/design/06`, `src/Bazaar.Server/Agents` |
| SoulGuard | The validator that runs on every memory write: structure, Soul core protection and a secrets scan. | Partial (`src/Bazaar.Server/Memory`, `Security`) | `docs/design/08` §6 |
| Mirror Agent | Learns your style and filters agent suggestions. Its profile is never exported. | Planned (v1 scope) | `docs/design/26` §5 |
| Digital Twin | A continuously updated, per-project self-understanding. | Planned (v1 scope). No dedicated design doc; location Unclear. | `Bazaar.md` §4 |
| Taint and approvals | Server-side taint tracking. Once a session has seen untrusted input, high-impact actions need owner approval. Reader sessions handle untrusted input. | Partial (`src/Bazaar.Server/Security`: rules, policy, audit chain, approval service; card UI open) | `docs/design/15` |
| Owner emergency bypass | Owner emergency rule for vacation, recorded as DECIDED in doc 24. | Decided (rule recorded in docs, PR #336); runtime enforcement not checked | `docs/design/24` WP6 |

## 4. Knowledge and planning

| Name | What it is | Status | Owning doc or code |
|---|---|---|---|
| Vault | The company wiki: Markdown files with `[[wiki-links]]` in a git repo, written through one VaultWriter. Postgres only indexes it. | Partial (index tables, repositories, front matter; writer and access layer open) | `docs/design/18`, `src/Bazaar.Server/Vault`, `Knowledge` |
| Constellation | The whole knowledge workspace (Obsidian and Milanote replacement), formerly "Mood Board". Vault is its document store. Vulkan renderer, Skia fallback. | Partial (`src/Bazaar.Server/Constellation`, `Bazaar.Constellation.Rendering`, `.Vulkan`) | `docs/deep-dives/Bazaar.Constellation.md`, `docs/design/19` |
| Reverse Gravity Layout | Force-directed layout the Constellation runs on. The focused document becomes the centre. | Partial (Louvain clustering and layout in flight) | `Bazaar.Constellation.md` §1 |
| Level of detail (LOD) | Graph level-of-detail for large Constellation scenes. | Shipped (graph LOD served from the shared source, #312) | `docs/design/constellation-scale-and-lod.md` |
| Git Solar System | A visual Git interface. Branches orbit like planets, commits are bodies and merges are comets. | Planned (naming decided) | `Bazaar.Constellation.md` §2, `docs/design/19` |
| RyuseiFuda (流星札) | The company planner, formerly Kanban. Three layers: Workday (company board), Orrery (per-agent scheduling) and Roadmap (tied to Constellation). Also called the "shooting star ticket". | Partial (triggers, scheduler, event bridge, audit, agent tools, board pane; see layer rows) | `docs/design/38` |
| RyuseiFuda: Workday (company board) | The macro board and schedule, previously Kanban. | Partial (Kanban tables, transitions, blocker graph shipped) | `docs/design/12`, `src/Bazaar.Server.Components.Kanban` |
| RyuseiFuda: Orrery (per-agent scheduling) | Per-agent timers, intervals, watchers and events that re-invoke an agent. Earlier wording called it "Micro". | Partial (trigger repository, scheduler tick, fire recording, per-agent wake cap, audit) | `docs/design/38` |
| RyuseiFuda: Roadmap | Objectives and phases in the Vault, shown in the Constellation. Earlier wording called it "Objective". | Partial (layer 3 views and front-matter contract, `src/Bazaar.Server/Roadmap`) | `docs/design/38` WP-R12 |
| Kanban | The dependency board: Backlog, Ready, In progress, Review, Done, with Blocked shown automatically. Done means the PR merged. Being renamed RyuseiFuda. | Shipped (columns, transitions, blocker graph; rename in progress) | `docs/design/12` |
| Macro and Micro (old names) | Earlier names for RyuseiFuda's Workday and Orrery layers. | Superseded. Do not use in new code or docs. | project memory, `docs/design/38` |
| GC metadata | Typed GCs with structured metadata. Types live in a registry table. | Planned (types decided; concept proposed) | `docs/design/01` |
| Bazaar Archive | An auto-generated project history book. | Planned (v1 scope). No design doc found; Unclear. | `Bazaar.md` §4 |
| Hive Mind (Cross-Project) | Controlled knowledge sharing across projects, as copy notes in the `hive/` Vault area. | Planned (v1 scope) | `docs/design/08` §6, `docs/design/25` |
| Memory | Agent memory: notes, access, write path, RRF retrieval. | Partial (`src/Bazaar.Server/Memory`) | `docs/design/08` |
| Retrieval and token efficiency | Retrieval design and token-budget work. | Planned (design reference) | `docs/design/retrieval-and-token-efficiency.md` |
| Graph index (`Bazaar.Graph`) | A Graphify-style code symbol graph. Built in a closed PR (#118), not on `main`. | Planned (reference only). Pending doc 17 repo-map decision. | `docs/design/graph-index.md` |

## 5. Fun and experimental

| Name | What it is | Status | Owning doc or code |
|---|---|---|---|
| What If Engine | Alternate-timeline sandboxes for comparing decisions. | Planned (v1 scope; needs Hyper-V sandbox VM) | `docs/design/26` §3 |
| Time Machine (Historical Replay) | Replay or fork a project's full history. | Partial (`Fun/Lucky`, replay pieces; full design planned) | `docs/design/26` §4 |
| I'm Feeling Lucky | A chaotic all-hands brainstorm mode. | Partial (`Fun/Lucky`). Run override blocked on an owner question. | `docs/design/26` §6 |
| Playtest Mode | AI personas play games and give UX feedback. More plausible human-assisted on the Windows client. | Planned (v1 scope) | `docs/design/26` §7 |

## 6. Clients

| Name | What it is | Status | Owning doc or code |
|---|---|---|---|
| Bazaar.Client.Core | Shared Avalonia UI library: shell, theme model, connection client, key router, panes. | Partial | `src/Bazaar.Client.Core`, `docs/design/27` |
| Command palette | Fuzzy palette opened by a mnemonic. No Ctrl chords. | Planned | `docs/design/27`, CLAUDE.md |
| Theme | One theme file drives Avalonia and Monaco. | Partial (theme model merged) | `docs/design/27` |
| Discord-style GC view | GC messages with avatar, role badge, model badge, cost chip, presence, timestamp, profile cards. | Partial (`Bazaar.Client.Components.Chat`) | CLAUDE.md, `docs/design/27` |
| Start screen dashboard | What changed while away: unread GCs and mentions, PRs and decisions awaiting you, agents and board, build health and usage. | Planned | CLAUDE.md |
| Bazaar.Windows | The Windows desktop head. Runs Roblox Studio for the owner. Avalonia. | Partial (`src/Bazaar.Client.Windows`; `--panes` window headless tests merged) | `docs/deep-dives/Bazaar.Windows.md`, `docs/design/28` |
| Bazaar.Linux | The Linux desktop head and packaging. | Partial (`src/Bazaar.Client.Linux`) | `docs/deep-dives/Bazaar.Linux.md`, `docs/design/30` |
| Bazaar.Android | Native Avalonia client, the "development console". Radial menu whose centre is dictation. Ships first as a Google Play closed test. | Partial (`src/Bazaar.Client.Android`; device pairing and GC pane merged; push and Play track open) | `docs/deep-dives/Bazaar.Android.md`, `docs/design/29` |
| Radial menu | Android navigation whose centre is dictation. | Planned | CLAUDE.md |
| Terminal head | Terminal client. | Partial (`src/Bazaar.Client.Terminal`) | `docs/design/27` |
| Launcher and Chat components | Client component libraries. | Partial (`Bazaar.Client.Components.Launcher`, `.Chat`) | `src/` |
| Local Co-Worker (NPU) | On-device inference on the client. A named client-side exception to "server does the compute". | Partial (`src/Bazaar.Client.LocalInference`; NPU probe blocked on hardware S21) | `docs/design/31` |
| Monaco editor | Editor component hosted in the Windows head. Gated by spike S12. | Planned (gate open) | `docs/design/27`, `docs/design/28` |
| Git diff and PR panes | Diff and PR UI in the Windows head. | Planned (blocked on S12) | `docs/design/28` |
| ConPTY terminal pane | Windows terminal pane. | Planned (blocked on spike S11 and widget choice) | `docs/design/28` |
| Pane registry (client) | Client panes registered per head and rendered by a generic renderer. | Partial (`src/Bazaar.Client.Core/Panes`) | `docs/design/27`, `docs/design/20` |
| Bazaar.Guest.Agent | Guest-side agent baked into VM images. | Partial (project exists) | `src/Bazaar.Guest.Agent` |

## 7. Connectivity and remote access

| Name | What it is | Status | Owning doc or code |
|---|---|---|---|
| Conduit | The connectivity feature: reachability and transport modules. Sub-parts named in project memory: Conduit.Cloudflared, Conduit.P2P, Conduit.Lan. | In flight. Doc 39 (PR #358) defines `IReachabilityProvider` and `ITransportModule`; it is not on `main` yet. | project memory; doc 39 (PR #358) |
| Remote access and auth | Remote access, authentication, authorization and privacy settings. | Partial (`src/Bazaar.Server/Remote`, `Tls`, `RateLimits`) | `docs/design/32` |
| Pairing | Device authorization and pairing for clients (`/pair/*` routes). | Shipped (state machine, device-authorization client, HTTP routes) | `docs/design/32` §3 |
| Privacy audit | Audit of privacy-relevant events. | Partial (`src/Bazaar.Server/Components/PrivacyAudit`) | `docs/design/32` |
| Erasure | Per-person erasure mechanics. | Partial (`Components/Erasure`); mechanics OPEN | `docs/design/14`, CLAUDE.md |
| Tls | TLS termination and key pinning. | Partial (`Components/Tls`) | project memory, `docs/design/32` |
| Rate limits | Server rate limiting. | Partial (`Components/RateLimits`) | `docs/design/32` |
| Status listener | Read-only server status page and listener. | Partial (`Bazaar.Server.Host`) | `docs/design/03` |

## 8. Models and gateway

| Name | What it is | Status | Owning doc or code |
|---|---|---|---|
| Model Gateway | Bazaar.Server's own gateway: per-agent model policy, one interface for all providers. | Shipped (contracts, fakes, three adapters, policy, health, usage) | `docs/design/05`, `src/Bazaar.Server/Gateway` |
| Adapters (`openai_chat`, `anthropic_messages`, `gemini_generate_content`) | Wire-family adapters the gateway looks up by name. | Shipped | `docs/design/05` |
| FreeLLMAPI provider | First-class FreeLLMAPI provider with priority support. Live key check (S17) pending. | Partial | `docs/design/05b` |
| Routing methods (`custom/*`) | Automatic switching, custom rules and a whitelist of cheaper code-capable models. No unbounded global auto. | Planned (rules OPEN) | CLAUDE.md |
| Claude lane | Claude member uses the owner's Anthropic key and free tokens via FreeLLMAPI, by availability. Subscription logins never route through the gateway. | Partial (API-key path; Anthropic key cap parked for the owner) | CLAUDE.md |
| Usage and cost | Usage records, cost chips and quota surfacing. | Partial (`src/Bazaar.Server/Gateway` usage; Observatory rollup in flight) | `docs/design/23` |

## 9. Environments, engines and coding

| Name | What it is | Status | Owning doc or code |
|---|---|---|---|
| Agent VMs | Each agent gets a Windows VM and a Linux VM, headless by default. | Partial (`src/Bazaar.Server.Vms`: environments, images, lifecycle, reconciliation; Hyper-V provider blocked on spike S8) | `docs/design/16` |
| Browser profiles | Full Chromium with an anonymous profile and Passport-backed account profiles. | Planned (credential leases in flight) | `docs/design/16`, `docs/design/14` |
| Coding-Agent Harness | The coding harness agents use to build and test. | Partial (`src/Bazaar.Server/Coding`) | `docs/design/17` |
| Engine Integration API | Plugin architecture for game engines. | Partial (`src/Bazaar.Engines`: registry, grants, adapters) | `docs/design/20`, `Bazaar.md` §4 |
| Godot adapter | Godot headless adapter. | Partial (`src/Bazaar.Engines/Godot`) | `docs/design/20` |
| Roblox Studio bridge | Roblox Studio, first-class. Runs only on the owner's Bazaar.Windows. Agents do headless Luau checks and file sync. | Partial (`src/Bazaar.Engines/Roblox`) | `docs/design/20`, CLAUDE.md |
| GitHub integration | GitHub Apps (org `codex-alchemist-dev`), PR flow and owner merge. | Partial (`src/Bazaar.Server.GitHub`: adapter, auth, events, workspace) | `docs/design/21` |
| Git engine | The git engine behind the Vault and Constellation. | Partial (spike S23 merged) | `docs/design/35` |

## 10. Storage, backup and platform packaging

| Name | What it is | Status | Owning doc or code |
|---|---|---|---|
| PostgreSQL (managed) | PostgreSQL 18 plus pgvector, packaged and managed by Bazaar. The owner never touches it. | Shipped (managed connection, Windows bundle pinned; P5 and P7 merged; P8 smoke and P9 pg_upgrade open) | `src/Bazaar.Components.ManagedPostgres`, `docs/design/04` |
| Migration runner | Embedded, versioned SQL migrations; `migrate` command. | Shipped | `docs/design/04`, `src/Bazaar.Server/Data` |
| Backup | Backup engine and restore drill. | Partial (`src/Bazaar.Server/Backup`, `Bazaar.Server.Components.Backup`) | `docs/design/33` |
| Export | Data export. | Planned (part of doc 33) | `docs/design/33` |
| Search providers | Postgres full-text on Windows, `pg_search` on Linux (blocked on counsel review). | Partial (FTS merged; `pg_search` blocked) | `docs/design/03`, `src/Bazaar.Server/Providers/Search` |
| Cache and presence | Garnet on Windows, Valkey on Linux; GC fan-out via Postgres NOTIFY. | Planned (Garnet blocked on spike S2) | `docs/design/03` |
| Windows provider | Windows Service host and Windows providers. | Partial (`src/Bazaar.Server.Providers.Windows`) | `docs/design/03` |
| Linux provider | systemd host and Linux providers. | Partial (`src/Bazaar.Server/Providers/Linux`) | `docs/design/03` |
| Bazaar.Server.MSI, Chocolatey, MSIX, AppImage, RPM, OCI image | Packaging artifacts for the server and clients. | Shipped (scripts and packaging merged; signing open) | `docs/design/37`, `packaging/` |
| Release workflow and channels | Version-tag release workflow. Channels: LTSC, Stable, Beta, Alpha, Latest-Commit, chosen by the operator. | Shipped (workflow); LTSC planned | `docs/VERSIONING.md`, `docs/release.md` |
| Community and external platforms | Community posting tiers and external-platform adapters. | Partial (`src/Bazaar.Server.Community`); forum, Discord and Reddit adapters blocked | `docs/design/36` |
| Testing harness | Bazaar.Testing, PgFixture, protocol harness, architecture tests, live-eval harness. | Shipped | `docs/design/34`, `src/Bazaar.Testing`, `tests/Bazaar.Architecture.Tests` |

## 11. Wire, data and contracts

| Name | What it is | Status | Owning doc or code |
|---|---|---|---|
| Wire protocol | JSON via source-generated `System.Text.Json` behind `IWireCodec`. Non-.NET clients are a Rust fallback only. | Shipped | `docs/design/02`, `docs/spikes/S6.md` |
| Wire transport and sync | WebSocket transport, reconnect, replay, idempotent requests. | Shipped | `docs/design/02` |
| Protobuf codec | Rejected by the S6 decision. | Rejected | `docs/design/02` WP13 |
| Bazaar.Architecture.Tests | Architecture rules: migration namespace freeze, boundary checks. | Shipped | `tests/Bazaar.Architecture.Tests` |

## 12. Modules (planned naming)

Owner-set naming, 2026-11-09, recorded in project memory. The code does not yet use these names; the rows describe the target.

| Name | What it is | Status | Owning doc or code |
|---|---|---|---|
| Bazaar.Core | The core module every other module slots into. | Planned (target name; `Bazaar.Server/Components` is today's kernel) | `docs/design/MODULARITY-AUDIT.md`, `BAZAAR-COMPONENTS.md` |
| Bazaar.GC, Bazaar.Constellation, Bazaar.RyuseiFuda | Server modules that slot into Bazaar.Core. | Planned (naming final; code split is wave 3) | `BAZAAR-COMPONENTS.md` §10 |
| Bazaar.Client.<Feature> | Client modules, one per feature area. | Planned | `BAZAAR-COMPONENTS.md` §10 |
| Parts: `<Module>.<Part>` | Parts of a module, such as RyuseiFuda.Workday. | Planned | project memory |

## 13. Codenames and versions

Versions are SemVer. Pre-release builds are "Beta", displayed zero-padded as `v00.MM.PP`. Each major gets a codename, Z to A, with an alchemy-and-stars theme. Source: `docs/VERSIONING.md`.

| Version | Codename | Status |
|---|---|---|
| v1.0.0 | Zuhal | Owner-picked. Ships only when the owner judges Bazaar ready. |
| v2.0.0 | Yliaster | Owner-accepted |
| v3.0.0 | Xylokrator | Owner pick, 2026-11-08. Replaces Xerion. |
| v4.0.0 | Weltseele | Owner pick, 2026-11-08. Replaces Wormwood. |
| v5.0.0 | Valthyrzmthl | Owner pick, 2026-11-08. Replaces Venus. The owner's rationale, linking alchemy and stars, is not recorded yet. |

Rejected or replaced names, not to reuse: Xerion, Wormwood, Venus, Wandering Star, Wolf, Zodiac.

Stage labels (roadmap, not versions): Stage 1 to Stage 9, see `docs/ROADMAP.md`.

## 14. Unclear, to settle with the owner

These items are named in the sources but their meaning, status or home is not settled. They are listed here rather than guessed.

- **Digital Twin** and **Bazaar Archive**: no dedicated design doc. Their wording comes from `Bazaar.md` §4 alone.
- **RyuseiFuda spelling**: owner messages used variant spellings; the canonical form is `RyuseiFuda` and has not been confirmed by the owner.
- **Valthyrzmthl**: meaning and an alchemy or star link are still missing.
- **"Codex"**: no clearance search has been done. Keep it out of public names and package IDs.
- **Mood Board** and **Macro / Micro / Objective**: earlier names, superseded by Constellation and RyuseiFuda layers.
- **Soul Marketplace** and **Talent Pool** v1 scope: OPEN or blocked on a Council motion-class follow-up.
- **Company Constitution**: v1 scope, no text drafted yet.
- **Bazaar.Core** versus today's `Bazaar.Server/Components` kernel folder: the target split is set; the migration order is wave 3.

## 15. Open-item blockers that gate named features

From the wave scan (2026-10-30). Listed so the status above is not read as more finished than it is.

- Hyper-V VM provider, HvSocket transport and VM benchmark: blocked on owner-hardware spike S8.
- Garnet cache on Windows: blocked on spike S2.
- Monaco editor, diff and PR panes, editor pane: blocked on the Monaco gate S12.
- ConPTY terminal pane: blocked on S11 and the terminal-widget choice.
- Android push and Play closed test: blocked on the owner's tester list and device spike S7.
- NPU path (Local Co-Worker): blocked on owner hardware S21.
- GitHub fork paths and live harness: blocked on S22 and owner throwaway repos.
- Anthropic live runs and coding benchmark: blocked on S18, the owner's API key and cap.
- `pg_search` provider: blocked on counsel review.
