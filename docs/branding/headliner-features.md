# Headliner features: named glossary

Status: DRAFT (2026-11-08). Compiled from the design set (`docs/deep-dives/Bazaar.md` §4 is the master glossary), `docs/design/`, `docs/VERSIONING.md`, `docs/ROADMAP.md`, `CLAUDE.md` and project memory. Where a name's meaning or status is not yet settled, the row says so.

> **These named features must be spotlighted in the README, visuals and advertising when Bazaar becomes public-ready.** Each one below is a headliner. Visuals and copy for them are part of the public-readiness work, not an afterthought.

Status tags follow `docs/deep-dives/Bazaar.md`: DECIDED, PROPOSED, REJECTED, RESEARCHED, OPEN. Only DECIDED items are settled. "v1" means the v1.0 scope the owner set on 2026-10-24.

## 1. Platform and company

| Name | What it is | Owning doc or component | Status |
|---|---|---|---|
| Bazaar | The open-source platform for a persistent AI software studio. Agents with their own identity, memory and accounts coordinate in group chats, with no master orchestrator. | `docs/deep-dives/Bazaar.md` | DECIDED |
| Codex Alchemist | The first company built on Bazaar, and the developer of the Bazaar platform. | `docs/deep-dives/Bazaar.md` §1 | DECIDED. The name "Codex" overlaps OpenAI's product; no clearance search yet, so keep it out of public product names until decided. |
| Bazaar Components | The modular model: every capability is a component with a manifest and contracts, and the host hard-codes only the kernel. | `docs/design/BAZAAR-COMPONENTS.md` | PROPOSED |
| Bazaar SDK | One shared API that every subsystem uses. Apache-2.0 `Bazaar.Sdk`. | `Bazaar.Server.md`, `src/Bazaar.Sdk` | PROPOSED; v1 scope |
| Engine Integration API | Plugin architecture for game engines such as Godot and Roblox Studio. Roblox Studio is first-class. | `docs/design/20`, `Bazaar.md` §4 | DECIDED (direction) |
| Bazaar.Server | The headless, OS-agnostic .NET 11 server that does all the compute. | `docs/deep-dives/Bazaar.Server.md` | DECIDED |
| Bazaar.Windows, Bazaar.Android, Bazaar.Linux | Native Avalonia clients that render what the server provides. The Android client uses a radial menu whose centre is dictation. | `docs/deep-dives/Bazaar.Windows.md`, `Bazaar.Android.md`, `Bazaar.Linux.md`; `docs/design/27`–`30` | DECIDED (clients); Android radial menu is a CLAUDE.md decision |
| Company Constitution | The `/company` docs: culture, principles, mission and decision records that every Soul onboards with. | `docs/design/24` §1 | PROPOSED; v1 scope; not yet drafted |
| Org Chart | Relationship structure for the company, not a rigid hierarchy. | `docs/design/24` §2 | PROPOSED; v1 scope |
| Interrupt System | A formal interrupt and resume stack for work. | `docs/design/24` §3 | PROPOSED; v1 scope |
| Company Calendar | Internal planning, release and vacation calendar. | `docs/design/24` §4 | PROPOSED; v1 scope |
| Company Memory | Institutional memory and decision chains, kept as a Vault area (`company/memory/`). | `docs/design/08` §6 | PROPOSED; v1 scope |
| Health System | Per-project tech debt, morale and coverage metrics. | `docs/design/25` | PROPOSED; v1 scope |
| Company Analytics | Per-agent metrics, framed as "improve the company". | `docs/design/25` | PROPOSED; v1 scope |
| Observatory | Live dashboard of what every agent is doing, plus which model lanes are near a limit. | `docs/design/23` | PROPOSED; v1 scope |
| Resource Economy | Would have been money budgets and a wallet. | `Bazaar.md` §4 | REJECTED 2026-10-24. Limits are quotas and the Work-Life Simulation. |
| Office Spaces | A fixed floor plan. | `Bazaar.md` §4 | REJECTED. GCs self-organise. |

## 2. Communication

| Name | What it is | Owning doc or component | Status |
|---|---|---|---|
| GC (Group Chat) | The coordination system. Append-only and never purged; edits keep every version. The conversation is the coordination. | `docs/design/01-gc-data-model.md`, `Bazaar.md` §2 | DECIDED |
| Sidewalk | The casual, non-code GC mode. | `docs/design/26` §8 | Name DECIDED; behaviour PROPOSED |
| Thinking GC | A nested reasoning GC for each agent inside each GC it works in. The owner always reads it; peers read on request. | `docs/design/01` | DECIDED (round 3) |
| DM | A two-member GC. The owner reads all of them. | `Bazaar.md` §2 | DECIDED |
| Skimmer Bouncer | A cheap gatekeeper model that skims GC messages and wakes the full agent only when relevant. Starts defer-only, so it never drops a message. | `docs/design/13-skimmer-bouncer.md` | PROPOSED; v1 scope |
| Nigerian Prince Protocol | An optional scam-baiting protocol behind a setup wizard. Without it, scam emails are quarantined. | `Bazaar.md` §2 | PROPOSED; v1 scope, optional |
| Agent email | Each agent's external email address, on whatever domain the operator configures. External only; internal coordination stays in GCs. | `docs/design/22-email-subsystem.md` | DECIDED |
| Nervous System | An internal event bus whose events drive software reactions. It never replaces GCs. | `Bazaar.md` §2 | PROPOSED |

## 3. Agents and identity

| Name | What it is | Owning doc or component | Status |
|---|---|---|---|
| Soul System | An agent's complete identity: VM, personality, memory, reputation and accounts. | `docs/design/07-soul-system.md`, `docs/deep-dives/Bazaar.Soul.md` | DECIDED |
| Soul Passport | The credential registry inside a Soul. It can be mounted per agent, scoped to that agent only. | `docs/design/14-identity-service-and-passport.md` | DECIDED |
| Identity Service | The single chokepoint for every credential and access request. Holds all provider and GitHub App keys. | `docs/design/14` | PROPOSED; v1 scope |
| Soul Marketplace | An optional marketplace for sharing preset Souls. Behavioural presets only, never personal data. | `docs/design/07` | Nature DECIDED; v1 scope OPEN |
| Talent Pool | A library of reusable subagent templates. Hiring runs as a Council vote. | `docs/design/25`, `docs/design/10` | PROPOSED; v1 scope |
| Genetic Memory Transfer | Temporary subagents pass candidate lessons back before they end. Candidates need review before entering a Soul. | `docs/design/07` §Genetic, `docs/design/25` | PROPOSED; v1 scope |
| Council | Whole-team voting and debate. Votes are independent first, then revealed together. Ties go to the owner. | `docs/design/10-council-and-voting.md` | DECIDED |
| Reputation | Scores that combine peer review with hard outcomes such as CI, reverts and owner accept or reject. | `docs/design/11-reputation-and-peer-review.md` | DECIDED (weights OPEN) |
| Work-Life Simulation | Shifts, breaks, vacations and curfews, used instead of rate limits. Each agent has its own timezone and schedule. | `docs/design/09-work-life-simulation.md` | DECIDED |
| Psych Ward (The Asylum) | The recovery workflow for stuck or spiralling agents. A loop-guard trip sends the agents involved there. | `docs/design/26` §1, `docs/design/06` | Routing DECIDED (round 9, C34); workflow PROPOSED, semi-serious |
| Vent post | A mandatory, public reflective post after a failure. | `docs/design/26` §2 | PROPOSED; v1 scope |
| Mirror Agent | Learns your style and filters agent suggestions. Its profile is never exported. | `docs/design/26` §5 | PROPOSED; v1 scope |
| Digital Twin | A continuously updated, per-project self-understanding. | `Bazaar.md` §4 | PROPOSED; v1 scope. No dedicated design doc found in `docs/design/`; location unconfirmed. |
| SoulGuard | The validator that runs on every memory write: structure, Soul core protection and a secrets scan. | `docs/design/08-memory-system.md` §6 | Named in design; not in the `Bazaar.md` glossary. Meaning taken from doc 08. |
| Loop guards | Server-side counters that stop agents looping, and trip into the Psych Ward. | `docs/design/06`, `docs/design/09` | DECIDED (routing); PROPOSED for the detail |

## 4. Knowledge and planning

| Name | What it is | Owning doc or component | Status |
|---|---|---|---|
| Vault | The company wiki: Markdown files with `[[wiki-links]]` in a git repo, written through one VaultWriter. Postgres only indexes it. | `docs/design/18-vault.md` | DECIDED |
| Constellation | The whole knowledge workspace (Obsidian and Milanote replacement), formerly the "Mood Board". The Vault is its document store. Rendered with Vulkan, with Skia as the fallback. | `docs/deep-dives/Bazaar.Constellation.md`, `docs/design/19` | DECIDED (naming and renderer) |
| Reverse Gravity Layout | The force-directed layout the Constellation runs on. The focused document becomes the centre. | `Bazaar.Constellation.md` §1 | DECIDED |
| Git Solar System | A visual Git interface. Branches orbit like planets, commits are bodies and merges are comets. | `Bazaar.Constellation.md` §2 | DECIDED (naming) |
| RyuseiFuda | The company planner, formerly Kanban. Three layers: Macro (the company board and schedule), Micro (per-agent timers, intervals and watchers that re-invoke an agent), and Objective or Roadmap (tied to the Constellation). | `docs/design/12` for the current Kanban; design doc 38 is in draft PR #327 and is not on main | Owner naming 2026-11-08 (project memory). Doc 12 still says Kanban. Meaning confirmed by the owner; the doc is not yet merged. |
| Kanban | The dependency board: Backlog, Ready, In progress, Review, Done, with Blocked shown automatically. Done means the PR merged. | `docs/design/12-kanban-and-dependencies.md` | DECIDED (columns); being renamed RyuseiFuda |
| GC metadata | Typed GCs with structured metadata. The types live in a registry table. | `docs/design/01` | Types DECIDED (C19); concept PROPOSED |
| Bazaar Archive | An auto-generated project history book. | `Bazaar.md` §4 | PROPOSED; v1 scope. No design doc found; unconfirmed. |
| Cross-Project Hive Mind | Controlled knowledge sharing across projects, as copy notes in the `hive/` Vault area. | `docs/design/08` §6, `docs/design/25` | PROPOSED; v1 scope |

## 5. Fun and experimental

| Name | What it is | Owning doc or component | Status |
|---|---|---|---|
| What If Engine | Alternate-timeline sandboxes for comparing decisions. | `docs/design/26` §3 | PROPOSED; v1 scope |
| Time Machine (Historical Replay) | Replay or fork a project's full history. | `docs/design/26` §4 | PROPOSED; v1 scope |
| I'm Feeling Lucky | A chaotic all-hands brainstorm mode. | `docs/design/26` §6 | PROPOSED; v1 scope |
| Playtest Mode | AI personas play games and give UX feedback. More plausible human-assisted on the Windows client. | `docs/design/26` §7 | PROPOSED; v1 scope |

## 6. Release codenames

Versions are SemVer. Pre-release builds are "Beta", displayed as `v00.MM.PP`. Each major gets a codename.

| Version | Codename | Status |
|---|---|---|
| v1.0.0 | Zuhal | DECIDED (owner-picked). Ships only when the owner judges Bazaar ready. |
| v2.0.0 | Yliaster | DECIDED (owner-accepted) |
| v3.0.0 | Xylokrator | DECIDED (owner pick, 2026-11-08; replaces Xerion). Meaning: "ruler of the wood" (Greek *xylon*, wood, and *kratos*, rule). The philosophers' tree of the Great Work, made cosmic. |
| v4.0.0 | Weltseele | DECIDED (owner pick, 2026-11-08; replaces Wormwood). Meaning: anima mundi, the world soul. Plato's *Timaeus* gave it to the heavens, and Renaissance writers tied it to the spirit that binds matter and sky. |
| v5.0.0 | Valthyrzmthl | DECIDED (owner pick, 2026-11-08; replaces Venus). Meaning: coined by the owner, from the Valthyr root, in an exotic consonant-cluster style. No alchemy or star link is recorded yet. |

Release channels: LTSC, Stable, Beta, Alpha and Latest-Commit, chosen by the operator. Source: `docs/VERSIONING.md`.

## 7. Open items before the public launch

- **Confirm RyuseiFuda's design doc** (doc 38) once PR #327 merges. Then update doc 12 and the glossary.
- **Confirm the meanings of unconfirmed rows**: Digital Twin and Bazaar Archive have no design doc in `docs/design/`. Their wording here comes from `Bazaar.md` §4 alone.
- **Give Valthyrzmthl an alchemy and star link** (owner's rationale). The theme needs both halves, and the v5 row says none is recorded yet.
- **Clear the "Codex" name** before it appears in any public product name or package ID.
- **Draft the Company Constitution.** It is v1 scope and still has no text.
- **Counsel review** of the licences, and the NOTICE file, before the first public release.
