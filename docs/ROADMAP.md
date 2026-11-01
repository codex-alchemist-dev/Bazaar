# Roadmap

Status: DRAFT (2026-10-25). **Bazaar stays in Beta (v0.x) for a long while** (owner, 2026-10-25): the owner will run the first few real projects on it during Beta, and v1.0.0 ships only when it is truly ready. The roadmap therefore plans Beta milestones, not a v1.0.0 date. The v1.0.0 scope is DECIDED (CLAUDE.md, "v1.0 scope") and serves as the readiness target. The milestone split is [PROPOSED] until the owner confirms. Numbering and codenames: `docs/VERSIONING.md`. Build order and work packages: `docs/implementation-plan.md` and each design doc's "Work packages".

Each milestone ships when its exit check passes on the server's clean build (C37: agent self-reports never count). Stages are roadmap labels and set no version numbers. Version bumps follow `docs/VERSIONING.md` section 1; the display form is zero-padded (`v00.MM.PP`).

## Beta: stages

The list below is a starting plan, not a countdown. Milestones can be added, split or reordered as the first projects show what matters, and Beta can run past Stage 9 (Stage 10, Stage 11 and so on). Projects the owner runs on Bazaar during Beta feed back into the next milestone.

| Stage | Milestone | Contents | Exit check |
|---|---|---|---|
| **Stage 1** | Scaffold and design | Solution skeleton, CI (Windows and Ubuntu), repo hygiene, licences, the 36 design docs reconciled with DECISIONS-LOG, versioning (this PR) | `./build.sh` green on both OSes; all docs carry the reconciled status |
| Stage 2 | Spikes, gate 1 | Spikes that block foundation work (doc 35 wave A/B): S2 Garnet, S3 Postgres GC log at scale, S6 wire format, S8 VM sizing, S22 GitHub fork-and-PR, S23 git engine | Each spike has a written result; the wire format and git engine decisions are recorded in DECISIONS-LOG |
| Stage 3 | Foundation (implementation-plan wave 1) | `Bazaar.Contracts` core types and envelope; Postgres schema, `migrate` command, GC log with NOTIFY; provider interfaces with Windows and Linux stubs; settings registry (doc 03); `Bazaar.Server.Host` with WebSocket, reconnect and replay; gateway with per-agent policy, OpenAI-compatible and FreeLLMAPI providers, fake gateway | A fake agent posts to a GC, a client replays after a dropped socket, a model call is denied by policy, all on real Postgres in CI |
| Stage 4 | Company core (wave 2) | Agent runtime and wake model, Souls, memory in the Vault, work-life simulation and presence, Kanban with dependencies, Council, reputation, loop guards and Psych Ward, interrupts and calendar, security taint and approvals | Scripted multi-agent day in replay: shifts, a vote, a blocked task, a loop-guard trip and an approval, with no human input |
| Stage 5 | Knowledge and identity (wave 3) | Vault (VaultWriter, index, access layer), Constellation data, GitHub Apps and the PR flow with owner merge, email back ends and quarantine, Identity Service and Passport, Observatory and usage | An agent opens a PR from a task, the owner approves a signed merge, and the card moves to Done |
| Stage 6 | Windows client (wave 4) | `Bazaar.Client.Core` shell, theme, command palette, dashboard, Discord-style GC view, Kanban and Constellation views; Windows head with editor (after the Monaco gate, S12), terminal and diff/PR UI | Avalonia.Headless suite green; the owner runs a day from the Windows client |
| Stage 7 | Android and Linux clients | Android head (fold layouts, radial menu with dictation, push, native diff surface after S27); Linux head; Velopack updates and channels | Google Play closed-test build; Linux package installs and updates on its channel |
| Stage 8 | Environments and engines (wave 5) | Agent VM pair on Hyper-V (QEMU/KVM later), guest agent, browser profiles; coding harness; Godot headless adapter, Roblox Studio bridge; fun systems | An agent builds and tests a Godot sample in its own VMs |
| Stage 9 | Hardening | Backup, restore and export drills; remote access and privacy settings; community posting tiers; security review; performance budgets; counsel items; history purge (with the owner's go-ahead) | Restore drill passes; counsel sign-off recorded; zero open blocking questions in the design docs |

Stage numbers are roadmap labels, not release versions. Releases are SemVer tags on `main` (`docs/VERSIONING.md`). Milestones can overlap: a later milestone's spikes and docs may start early. Order changes need the owner.

## v1.0.0 "Zuhal": the readiness gate [scope DECIDED, date none]

v1.0.0 is not a milestone on this list. It ships only when the owner judges Bazaar truly ready: the full v1.0 scope below works, the first real projects have run on it, the open design questions and counsel items are closed, and the owner says go. Until then every build stays 0.x "Beta". Scope: the full v1.0 scope from CLAUDE.md. Server, Windows, Android and Linux clients. GCs, Kanban, Council, reputation, memory and the Vault, Constellation, gateway with FreeLLMAPI, GitHub and email. Observatory, Company Constitution, Org Chart, Interrupt System, Company Calendar, Health and Analytics, Digital Twin, Skimmer Bouncer (starting defer-only), Psych Ward and vent post, What If and Time Machine, Mirror Agent, Lucky mode and Playtest. Identity Service, Bazaar Archive, Talent Pool, Genetic Memory Transfer, Hive Mind, Company Memory, Bazaar SDK and GC metadata. Nigerian Prince Protocol stays optional behind the setup wizard. First supported host: Windows (the NUC). Ships on Stable; v1.x minors add features compatibly.

## After v1 [PROPOSED candidates, not scheduled]

Nothing here is scheduled, and none of it starts before v1.0.0. A major ships only for a breaking change (`docs/VERSIONING.md` section 1). These are the items the design set leaves outside v1 or OPEN. The owner decides which go into which major.

| Candidate | Source | Likely version |
|---|---|---|
| Soul Marketplace (v1 scope OPEN) | doc 07 | v1.x if additive, else v2 |
| TUI status client (OPEN) | CLAUDE.md | v1.x |
| QEMU/KVM VM backend tested to the Hyper-V level; Linux as a first-class tested host | docs 03, 16 | v1.x |
| Wire-format change after S6, if Protobuf is adopted later | doc 02 | v2.0.0 "Yliaster" |
| Multi-owner instances, more human members, privacy-law profiles beyond v1 | doc 32 | v2 |
| LTSC line for v1 | `docs/VERSIONING.md` section 4 | when v2 ships |

Codenames for v2 and later are proposals in `docs/VERSIONING.md` section 2.
