# Bazaar named features

Status: DRAFT (refreshed 2026-11-16 against `origin/main` at `5b69cf7`). A compiled index of every named feature, component, codename and module the repo, the design set and the project plans mention. It is a map, not a decision record: each row points to the doc that owns the detail.

## How to read the status column

| Status | Meaning |
|---|---|
| **Shipped** | Merged code exists on `main` for the feature. Coverage may still be partial; see the note. |
| **Partial** | Some merged code exists, but a named part is missing or still open. |
| **Planned** | Named and, usually, designed. No code on `main` yet. |
| **Rejected** | Decided against. Kept so the name is not reused by accident. |
| **Unclear** | The name, meaning, status or owner is not settled in the sources. Not guessed. |

Design-decision tags (DECIDED, PROPOSED, OPEN, REJECTED, RESEARCHED) come from `docs/deep-dives/Bazaar.md` and `docs/design/DECISIONS-LOG.md`. Only DECIDED items are settled. Where a design tag and a code status differ, the row shows both.

Code status is judged from the source tree and merged PR titles at `origin/main` (commit `5b69cf7`, 2026-11-16). The first draft used `b9ccc1c` (2026-11-09); 119 PRs merged after it and this refresh covers them. Open pull requests were not re-checked live, so a row marked Planned may already have an open PR.

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
| Bazaar SDK (`Bazaar.Sdk`) | One shared API every subsystem uses. AGPL-3.0 (owner card, 2026-11-13). | Partial (project exists) | `src/Bazaar.Sdk`, `Bazaar.Server.md` |
| Bazaar.Contracts | Shared wire and contract types for client and server. AGPL-3.0 (owner card, 2026-11-13). | Shipped | `src/Bazaar.Contracts` |
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
| Psych Ward (The Asylum) | The recovery workflow for stuck or spiralling agents. Loop-guard trips go there, including RyuseiFuda event-fire guard trips (#469). | Partial (`src/Bazaar.Server.Components.Fun`; workflow semi-serious and PROPOSED) | `docs/design/26` §1, `docs/design/06` |
| Vent post | A mandatory, public reflective post after a failure. | Partial (`Fun/Vent`) | `docs/design/26` §2 |
| Loop guards | Server-side counters that stop agents looping, tripping into the Psych Ward. | Partial (`src/Bazaar.Server/Agents/LoopGuard`; RyuseiFuda chain-depth and single-flight guards, #467) | `docs/design/06`, `docs/design/09` |
| Agent runtime and turn loop | The wake model, turn path and agent tools. Turn adapter claims named inbox items and offers tools (#379); agent post sink over the GC log (#382). | Partial | `docs/design/06`, `src/Bazaar.Server/Agents` |
| SoulGuard | The validator that runs on every memory write: structure, Soul core protection and a secrets scan. | Partial (`src/Bazaar.Server/Memory`, `Security`) | `docs/design/08` §6 |
| Mirror Agent | Learns your style and filters agent suggestions. Its profile is never exported. | Planned (v1 scope) | `docs/design/26` §5 |
| Digital Twin | A continuously updated, per-project self-understanding. | Planned (v1 scope). No dedicated design doc; location Unclear. | `Bazaar.md` §4 |
| Taint and approvals | Server-side taint tracking. Once a session has seen untrusted input, high-impact actions need owner approval. Reader sessions handle untrusted input. | Partial (`src/Bazaar.Server/Security`: rules, policy, audit chain, approval service; Postgres-backed safety adapters merged, #566; card UI open) | `docs/design/15` |
| Kill switch | Per-agent and global stop. A global stop denies writes and egress for every agent. Engage and resume are audited, and stops persist across restarts. Release of a contained agent is owner-only and needs a signed, single-use nonce. | Shipped (engine, audit, containment, state store, #545, #566) | `docs/design/48` §11, `src/Bazaar.Server/Security`, `Bazaar.Server` safety adapters |
| Agent token budgets | Per-agent token quotas per hour, day and week. The check and record are atomic in Postgres. Cost never gates work. Limits are provisional (OPEN 11.1). | Shipped (#545, #566); the settings key is proposed | `docs/design/48` §11, `docs/design/15` |
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
| RyuseiFuda: Workday (company board) | The macro board and schedule, previously Kanban. | Partial (Kanban tables, transitions, blocker graph shipped) | `docs/design/12`, `src/Bazaar.RyuseiFuda/Kanban`, `src/Bazaar.Client.RyuseiFuda/Workday` |
| RyuseiFuda: Orrery (per-agent scheduling) | Per-agent timers, intervals, watchers and events that re-invoke an agent. Earlier wording called it "Micro". | Partial (trigger repository, scheduler tick, fire recording, per-agent wake cap, audit, chain depth carried into tool calls and fire-woken turns, #359, #370) | `docs/design/38` |
| RyuseiFuda: Roadmap | Objectives and phases in the Vault, shown in the Constellation. Earlier wording called it "Objective". | Partial (layer 3 views and front-matter contract, pure objective roadmap read model #364, `src/Bazaar.RyuseiFuda/Roadmap`) | `docs/design/38` WP-R12 |
| Kanban | The dependency board: Backlog, Ready, In progress, Review, Done, with Blocked shown automatically. Done means the PR merged. Folded into RyuseiFuda (Workday layer) by owner decision 2026-11-16; the `kanban` component id stays. | Shipped (columns, transitions, blocker graph) | `docs/design/12` |
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
| Bazaar.Client.Core | Shared Avalonia UI library: shell, theme model, connection client, key router, panes, module manifest validator (#521), self-update core (#552). | Partial | `src/Bazaar.Client.Core`, `docs/design/27` |
| Command palette (Ex) | Fuzzy palette opened by a mnemonic. No Ctrl chords. Client-wide mnemonic navigation is `Bazaar.Ex` (#511); the palette filters mnemonic rows (#553). | Partial (`src/Bazaar.Ex`); remapping and the full palette UI open | `docs/design/27`, CLAUDE.md |
| Theme | One theme file drives Avalonia and Monaco. | Partial (theme model merged) | `docs/design/27` |
| Discord-style GC view | GC messages with avatar, role badge, model badge, cost chip, presence, timestamp, profile cards. | Partial (`Bazaar.Client.Components.Chat`) | CLAUDE.md, `docs/design/27` |
| Start screen dashboard | What changed while away: unread GCs and mentions, PRs and decisions awaiting you, agents and board, build health and usage. | Planned | CLAUDE.md |
| Bazaar.Windows | The Windows desktop head. Runs Roblox Studio for the owner. Avalonia. | Partial (`src/Bazaar.Client.Windows`; `--panes` window headless tests merged) | `docs/deep-dives/Bazaar.Windows.md`, `docs/design/28` |
| Calcinato | The built-in terminal and CLI of the Bazaar client. It also runs standalone as a replacement for the default system terminal app. Written as CaLcInato for the alternative styling. | Planned (name final; the ConPTY pane is planned) | `docs/design/28` (local terminal), `docs/design/27` |
| Athanor | The built-in IDE and text editor of the Bazaar client. It also runs standalone as a replacement for the default system text editor. | Planned (name final) | `docs/design/28` (editor host), `docs/design/27` |
| Per-install client builds | Each company builds its own clients, with its own interface, layout and modules (for example CDAX.msix and CDAX.apk). The repo ships server builds only. | Planned (policy decided by owner, 2026-11-12) | `docs/design/37`, `docs/FEATURES.md` section 6 |
| Bazaar.Linux | The Linux desktop head and packaging. | Partial (`src/Bazaar.Client.Linux`) | `docs/deep-dives/Bazaar.Linux.md`, `docs/design/30` |
| Bazaar.Android | Native Avalonia client, the "development console". Radial menu whose centre is dictation. Ships first as a Google Play closed test. | Partial (`src/Bazaar.Client.Android`; device pairing and GC pane merged; push and Play track open) | `docs/deep-dives/Bazaar.Android.md`, `docs/design/29` |
| Radial menu | Android navigation whose centre is dictation. | Planned | CLAUDE.md |
| Terminal head | Terminal client. | Partial (`src/Bazaar.Client.Terminal`) | `docs/design/27` |
| Launcher and Chat components | Client component libraries. | Partial (`Bazaar.Client.Components.Launcher`, `.Chat`) | `src/` |
| Local Co-Worker (NPU) | On-device inference on the client. A named client-side exception to "server does the compute". | Partial (`src/Bazaar.Client.LocalInference`; NPU probe blocked on hardware S21) | `docs/design/31` |
| Monaco editor | Editor component hosted in the Windows head. Gated by spike S12. | Planned (gate open) | `docs/design/27`, `docs/design/28` |
| Git diff and PR panes | Diff and PR UI in Bazaar.Client.GitGui, shared by the heads. | Planned (Monaco part blocked on S12) | `docs/design/41` section 4.14, `28`, `29` |
| ConPTY terminal pane | Windows terminal pane. | Planned (blocked on spike S11 and widget choice) | `docs/design/28` |
| Pane registry (client) | Client panes registered per head and rendered by a generic renderer. Pane modules register their own views (#455). | Partial (`src/Bazaar.Client.Core/Panes`) | `docs/design/27`, `docs/design/20` |
| Bazaar.Guest.Agent | Guest-side agent baked into VM images. | Partial (project exists) | `src/Bazaar.Guest.Agent` |

## 7. Connectivity and remote access

| Name | What it is | Status | Owning doc or code |
|---|---|---|---|
| Conduit | The connectivity feature: reachability and transport modules. Sub-parts named in project memory: Conduit.Cloudflared, Conduit.P2P, Conduit.Lan. | Partial (`src/Bazaar.Conduit`: doc 39 contracts merged, #358; per-listener rate limits and auth lockout, #545). Cloudflared, P2P and LAN transports are Planned. | `docs/design/39`, `src/Bazaar.Conduit` |
| Remote access and auth | Remote access, authentication, authorization and privacy settings. | Partial (`src/Bazaar.Server/Remote`, `Tls`, `RateLimits`) | `docs/design/32` |
| Pairing | Device authorization and pairing for clients (`/pair/*` routes). Linux keeps the refresh credential in the Secret Service keyring (#509, #518). | Shipped (state machine, device-authorization client, HTTP routes) | `docs/design/32` §3 |
| Privacy audit | Audit of privacy-relevant events. | Partial (`src/Bazaar.Server/Components/PrivacyAudit`) | `docs/design/32` |
| Erasure | Per-person erasure mechanics. | Partial (`Components/Erasure`); mechanics OPEN | `docs/design/14`, CLAUDE.md |
| Tls | TLS termination and key pinning. | Partial (`Components/Tls`) | project memory, `docs/design/32` |
| Rate limits | Server rate limiting. Conduit auth lockout uses exponential backoff with shipped floors (#545). | Partial (`Components/RateLimits`) | `docs/design/32` |
| Status listener | Read-only server status page and listener. | Partial (`Bazaar.Server.Host`) | `docs/design/03` |

## 8. Models and gateway

| Name | What it is | Status | Owning doc or code |
|---|---|---|---|
| Model Gateway | Bazaar.Server's own gateway: per-agent model policy, one interface for all providers. | Shipped (contracts, fakes, three adapters, policy, health, usage) | `docs/design/05`, `src/Bazaar.Server/Gateway` |
| Adapters (`openai_chat`, `anthropic_messages`, `gemini_generate_content`) | Wire-family adapters the gateway looks up by name. | Shipped | `docs/design/05` |
| FreeLLMAPI provider | First-class FreeLLMAPI provider with priority support. Live key check (S17) pending. | Partial | `docs/design/05b` |
| Routing methods (`custom/*`) | Automatic switching, custom rules and a whitelist of cheaper code-capable models. No unbounded global auto. | Planned (rules OPEN) | CLAUDE.md |
| Claude lane | Claude member uses the owner's Anthropic key and free tokens via FreeLLMAPI, by availability. Subscription logins never route through the gateway. | Partial (owner's Anthropic key is a config-defined gateway source, #451; dev responder agents get a Haiku route, #456; the $30 cap is parked for the owner) | CLAUDE.md |
| Usage and cost | Usage records, cost chips and quota surfacing. | Partial (`src/Bazaar.Server/Gateway` usage; Observatory rollup in flight) | `docs/design/23` |

## 9. Environments, engines and coding

| Name | What it is | Status | Owning doc or code |
|---|---|---|---|
| Agent VMs | Each agent gets a Windows VM and a Linux VM, headless by default. | Partial (`src/Bazaar.Server.Vms`: environments, images, lifecycle, reconciliation; guest agent project `Bazaar.Guest.Agent`; Hyper-V provider blocked on spike S8) | `docs/design/16` |
| Browser profiles | Full Chromium with an anonymous profile and Passport-backed account profiles. | Planned (credential leases in flight) | `docs/design/16`, `docs/design/14` |
| Coding-Agent Harness | The coding harness agents use to build and test. | Partial (`src/Bazaar.Server.Coding`) | `docs/design/17` |
| Engine Integration API | Plugin architecture for game engines. | Partial (`src/Bazaar.Engines`: registry, grants, adapters) | `docs/design/20`, `Bazaar.md` §4 |
| Godot adapter | Godot headless adapter. | Partial (`src/Bazaar.Engines/Godot`) | `docs/design/20` |
| Roblox Studio bridge | Roblox Studio, first-class. Runs only on the owner's Bazaar.Windows. Agents do headless Luau checks and file sync. | Partial (`src/Bazaar.Engines/Roblox`) | `docs/design/20`, CLAUDE.md |
| GitHub integration | GitHub Apps (org `codex-alchemist-dev`), PR flow and owner merge. | Partial (`src/Bazaar.Server.GitHub`: adapter, auth, events, workspace) | `docs/design/21` |
| Git engine | The git engine behind the Vault and Constellation. | Partial (spike S23 merged) | `docs/design/35` |
| Git modules | Bazaar.Server.Git (engine and provider contract), Bazaar.Client.GitGui, Bazaar.Client.GitGui.Github and Bazaar.Client.GitGui.Gitlab. Each has its own manifest and version. | Planned (module split decided 2026-11-16) | `docs/design/41` section 4.14, `21`, `52` (PROPOSED) |
| Git Solar System client | Bazaar.Client.GitSolarSystem (+ .Abstractions): branch orbits, commit bodies and merge comets, from the server's git DAG and orbit layout. | Planned (in progress, 2026-11-18) | `docs/design/19` (Git Solar System client module) |
| GitLab integration | GitLab as a second git provider on the GitHub contract. | Planned (v1 scope, owner 2026-11-16) | `docs/design/21`, `41` section 4.14 |

## 10. Storage, backup and platform packaging

| Name | What it is | Status | Owning doc or code |
|---|---|---|---|
| PostgreSQL (managed) | PostgreSQL 18 plus pgvector, packaged and managed by Bazaar. The owner never touches it. | Shipped (managed connection; bundle at PostgreSQL 18.7, #494; pgvector 0.8.2 pinned by SHA-256, #480; PG 19 RC CI leg and upgrade test, #539, #501; scheduled logical dumps, #460; `bazaar db backup` and `upgrade` CLI, #482) | `src/Bazaar.Components.ManagedPostgres`, `docs/design/04` |
| Migration runner | Embedded, versioned SQL migrations; `migrate` command. | Shipped | `docs/design/04`, `src/Bazaar.Server/Data` |
| Backup | Backup engine and restore drill. | Partial (`src/Bazaar.Server/Backup`, `Bazaar.Server.Components.Backup`) | `docs/design/33` |
| Export | Data export. | Planned (part of doc 33) | `docs/design/33` |
| Search providers | Postgres full-text on Windows, `pg_search` on Linux (blocked on counsel review). | Partial (FTS merged; `pg_search` blocked) | `docs/design/03`, `src/Bazaar.Server/Providers/Search` |
| Cache and presence | Garnet on Windows, Valkey on Linux; GC fan-out via Postgres NOTIFY. | Planned (Garnet blocked on spike S2) | `docs/design/03` |
| Windows provider | Windows Service host and Windows providers. | Partial (`src/Bazaar.Server.Providers.Windows`) | `docs/design/03` |
| Linux provider | systemd host and Linux providers. | Partial (`src/Bazaar.Server/Providers/Linux`) | `docs/design/03` |
| Bazaar.Server.MSI, Chocolatey, MSIX, AppImage, RPM, OCI image | Packaging artifacts for the server and clients. | Shipped (scripts and packaging merged; MSIX signed with the company server certificate, #510 and #551; CycloneDX SBOM for tagged releases, #538) | `docs/design/37`, `packaging/` |
| Release workflow and channels | Version-tag release workflow. Channels: LTSC, Stable, Beta, Alpha, Latest-Commit, chosen by the operator. Plain SemVer with the commit appended (#505). | Shipped (workflow); LTSC planned | `docs/VERSIONING.md`, `docs/release.md` |
| Community and external platforms | Community posting tiers and external-platform adapters. | Partial (`src/Bazaar.Server.Community`); forum, Discord and Reddit adapters blocked | `docs/design/36` |
| Testing harness | Bazaar.Testing, PgFixture, protocol harness, architecture tests, live-eval harness. | Shipped | `docs/design/34`, `src/Bazaar.Testing`, `tests/Bazaar.Architecture.Tests` |

## 11. Wire, data and contracts

| Name | What it is | Status | Owning doc or code |
|---|---|---|---|
| Wire protocol | JSON via source-generated `System.Text.Json` behind `IWireCodec`. Non-.NET clients are a Rust fallback only. | Shipped | `docs/design/02`, `docs/spikes/S6.md` |
| Wire transport and sync | WebSocket transport, reconnect, replay, idempotent requests. | Shipped | `docs/design/02` |
| Protobuf codec | Rejected by the S6 decision. | Rejected | `docs/design/02` WP13 |
| Bazaar.Architecture.Tests | Architecture rules: migration namespace freeze, boundary checks. | Shipped | `tests/Bazaar.Architecture.Tests` |

## 12. Modules (naming)

Owner-set naming, 2026-11-09, recorded in project memory. The code now uses most of these names: RyuseiFuda moved to `Bazaar.RyuseiFuda` (#452, #454), and `Bazaar.Client.<Feature>` projects exist. `Bazaar.Core` is still a target name.

| Name | What it is | Status | Owning doc or code |
|---|---|---|---|
| Bazaar.Core | The core module every other module slots into. | Planned (target name; the kernel still lives in `Bazaar.Server/Components`, see doc 40 section 4) | `docs/design/MODULARITY-AUDIT.md`, `BAZAAR-COMPONENTS.md` |
| Bazaar.GC, Bazaar.Constellation, Bazaar.RyuseiFuda | Server modules that slot into Bazaar.Core. | Partial (`Bazaar.GC.Abstractions`, `Bazaar.Constellation.*`, `Bazaar.RyuseiFuda`; the full code split is wave 3) | `BAZAAR-COMPONENTS.md` §10 |
| Bazaar.Client.<Feature> | Client modules, one per feature area. | Partial (`Bazaar.Client.Layout`, `.Kodo`, `.RyuseiFuda`, `.Components.Chat`, `.Components.Launcher`, `.LocalInference`, `.Terminal`, plus the platform heads) | `BAZAAR-COMPONENTS.md` §10 |
| Parts: `<Module>.<Part>` | Parts of a module, such as RyuseiFuda.Workday. | Planned | project memory |

## 12a. Named features added since the first draft

These merged after the 2026-11-09 baseline. Each row names the PRs that landed it. Statuses are from merged code only.

| Name | What it is | Status | Owning doc or code |
|---|---|---|---|
| Rubedo | The declarative UI DSL. Its own module with a pure engine library inside, used by the server and every head. Covers the expression engine with a typed checker, a golden corpus (#527), and view document schemas with a structural validator (#557). | Partial (engine, checker and validator merged; the view reader and the Avalonia mapping are in review, #561 and #559) | `docs/design/41a`, `41c`, `src/Bazaar.Rubedo` |
| Layout (Client) | Foldable, per-device layout documents with a validator, a resolver, scope merging, company locks for nodes and settings, and fail-closed fallbacks. | Partial (#512, #520, #576, #579, #580 merged; the Avalonia mapping is in review) | `docs/design/41`, `src/Bazaar.Client.Layout` |
| Ex (mnemonic navigation) | Client-wide mnemonic navigation and the palette filter over mnemonic rows. | Partial (#511, #553) | `src/Bazaar.Ex` |
| Michi (first-run tour) | The skippable first-run tour, moved out of the client into its own module. | Partial (#542) | `src/Bazaar.Michi` |
| Kodo (client) | The client-side health header and status feed for the Kodo runtime health module. | Partial (#515, #525, #531, #533). The server ring health gate (`Bazaar.Server.Modules/Releases`) reads Kodo health; the wider Kodo runtime is Planned (doc 46). | `docs/design/46`, `src/Bazaar.Client.Kodo` |
| Accessibility | Accessible names for controls, a CI gate for them, spoken mnemonic names and command announcements. | Partial (#560, #564, #572, #574, #585, #587) | `src/Bazaar.Accessibility`, `src/Bazaar.Accessibility.Avalonia` |
| Localisation | Localisation and accessible-name modules. | Partial (#543) | `src/Bazaar.Localisation` |
| Secrets | A per-agent secret store with scopes and audit-first gating (#546), a per-purpose policy table that denies by default (#568), a Secret Service key-encryption provider on Linux (#569) and a Windows key-encryption provider on CNG, using TPM where available (#570). | Partial (store, policy and both key providers merged; the agent credential issuer is still a fail-closed default) | `src/Bazaar.Secrets`, `src/Bazaar.Secrets.Windows`, `docs/design/14` |
| Audit | Audit rows with a hash chain, signed checkpoints persisted in Postgres (#554), Merkle checkpoints (#532), an inclusion-proof file with an offline verifier (#582) and external anchoring of checkpoints (#573). | Partial (checkpoints and proofs shipped; anchoring slice 2 merged) | `docs/design/48` §10, `src/Bazaar.Server/Security` |
| Recovery | A sealed recovery bundle with file export and import (#583), a backup-owner quorum policy and a recovery case state machine (#571), and a FIDO2 hardware-key assertion verifier (#584). | Partial (bundle, quorum and verifier merged; applying a restore is out of scope for now) | `docs/design/48`, `docs/design/49` |
| Module manager | Update policy, channels, dependencies, trust and line moves (#526, #549); auto-restore after a failed line-move switch and bounded retries (#558); operator-configurable retry policy (#567); owner policy document. | Partial (foundations, rollback, dry-run steps and release rings with a health gate are in code; full canary rollout and dry-run migrations still open) | `src/Bazaar.Server.Modules` (implementation, the settings contribution `ModuleManagerSettingContribution` and the SQL under `Sql/modules`) and `src/Bazaar.Server.Modules.Abstractions` (health contract, retry policy, setting names); `docs/design/42`, `docs/design/41b` |
| Setup plan | Headless setup plan, canonical hash and `bazaar-setup.json` validation (#548). | Partial (plan and validation merged; the wizard is open) | `src/Bazaar.Server/Setup`, `docs/design/45` |
| Client self-update | A platform-neutral self-update core (#552). | Partial (core merged; per-platform install open) | `src/Bazaar.Client.Core`, `docs/design/37` |
| Telemetry and crash reporting | Wave 1 (error codes, redaction, fingerprints, #519) and wave 2 (crash DTOs, capture core, consent model, #550). | Partial (waves 1 and 2 merged as contracts and capture core; server-side GitHub filing and LLM diagnosis are Planned, not in code) | `docs/design/43` |
| Portability | Importer and exporter module core (#540). | Partial (core merged; the Milanote, Miro, Trello and Obsidian importers are Planned) | `src/Bazaar.Server.Portability`, `src/Bazaar.Client.Portability`, `src/Bazaar.Client.Portability.Avalonia`, `docs/design/44` |
| Diagnostics | Diagnostics module and tests. | Partial | `src/Bazaar.Diagnostics` |
| Token budgets and kill switch | See sections 3 and 9 for the safety rows (#545, #566). | Partial | `src/Bazaar.Server/Security` |
| Linux and Windows heads | Linux display backend chosen from the session environment (#534), headless smoke tests (#541), and a single-instance forwarder for the warm process (#458). | Partial | `src/Bazaar.Client.Linux`, `src/Bazaar.Client.Windows` |
| Android device posture | Posture source contract with a fake and shell binder (#536); the Android head builds on every PR (#513). | Partial | `src/Bazaar.Client.Android` |
| Graph kinds (Constellation) | Typed node and edge kinds for the roadmap graph (#555) and Vault graph kinds (#562). | Partial | `docs/design/19`, `src/Bazaar.Constellation.Abstractions` |
| RyuseiFuda panes and Orrery | Panes moved into `Bazaar.Client.RyuseiFuda` (#547); the Orrery scheduling namespace is `Bazaar.RyuseiFuda.Orrery` (#452); pane pause and resume over the wire (#476, #478); owner trigger list (#477). | Partial | `docs/design/38`, `src/Bazaar.Client.RyuseiFuda` |

## 13. Codenames and versions

Versions are SemVer. Pre-release builds are "Beta", shown as plain SemVer, `0.MM.PP`, with the commit appended. Each major gets a codename, Z to A, with an alchemy-and-stars theme. Source: `docs/VERSIONING.md`.

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
- Rubedo view reader (#561) and the Avalonia view mapping (#559): both in review, and #559 is stacked on #561.
- Secrets agent credential issuer: a fail-closed default until a real issuer is configured.
- Telemetry server-side filing and diagnosis, and the Kodo server runtime (doc 46): Planned.
