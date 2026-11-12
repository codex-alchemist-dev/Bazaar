# Roadmap

Status: DRAFT (2026-10-25, status column updated 2026-11-12). **Bazaar stays in Beta (v0.x) for a long while** (owner, 2026-10-25): the owner will run the first few real projects on it during Beta, and v1.0.0 ships only when it is truly ready. The roadmap therefore plans Beta milestones, not a v1.0.0 date. The v1.0.0 scope is DECIDED (CLAUDE.md, "v1.0 scope") and serves as the readiness target. The milestone split is [PROPOSED] until the owner confirms. Numbering and codenames: `docs/VERSIONING.md`. Build order and work packages: `docs/implementation-plan.md` and each design doc's "Work packages".

- The **technical milestones** (database, wire, modular core, performance, packaging, security, CI, clients) are in [`ROADMAP-TECHNICAL.md`](ROADMAP-TECHNICAL.md). Each has a status and a source (PR number or doc).
- The **named features** and their status are in [`FEATURES.md`](FEATURES.md).
- The **dependency audit** and the dual-version rule are in [`FORWARD-FACING.md`](FORWARD-FACING.md).

Each milestone ships when its exit check passes on the server's clean build (C37: agent self-reports never count). Stages are roadmap labels and set no version numbers. Version bumps follow `docs/VERSIONING.md` section 1; the display form is zero-padded (`v00.MM.PP`).

## Where we are (2026-11-12)

Done on `main`, from merged PRs:

- **The database is inside the app.** PostgreSQL 18.7 and pgvector 0.8.2 are packaged, pinned and managed by Bazaar. The owner never installs or runs it. The server starts it, stores its password through the managed password store, runs versioned migrations, schedules backups, and has a `bazaar db backup` and `bazaar db upgrade` CLI. The Windows PC smoke run (crash, reboot, upgrade refusal) is the open part. PostgreSQL 19 runs in CI as a report-only job while its GA is scheduled.
- **The GC log runs on Postgres**, with per-GC sequence numbers, `NOTIFY` fan-out and encrypted message bodies.
- **The agent loop is live in code**: the production turn runner, tool dispatch, loop guards with the Psych Ward, the RyuseiFuda scheduler, triggers, pause and resume, and the board panes.
- **Clients build and run**: Windows and Linux heads (ReadyToRun, preloader), Android with device pairing and API 37, and the Constellation renderer core.
- **Packaging**: MSI, MSIX, Chocolatey, AppImage, RPM, OCI image and Compose.

Still open: the Windows P8 smoke run on the owner's PC, the Conduit design merge, the Vault writer, Monaco and the diff and PR panes, Android push and the Play closed test, the Hyper-V VM provider, and the gateway's FreeLLMAPI live check. The full list is in the technical roadmap.

## Beta: stages

The list below is a starting plan, not a countdown. Milestones can be added, split or reordered as the first projects show what matters, and Beta can run past Stage 9 (Stage 10, Stage 11 and so on). Projects the owner runs on Bazaar during Beta feed back into the next milestone.

| Stage | Milestone | Status | Contents | Exit check |
|---|---|---|---|---|
| **Stage 1** | Scaffold and design | Done | Solution skeleton, CI (Windows and Ubuntu), repo hygiene, licences, the design docs reconciled with DECISIONS-LOG, versioning | `./build.sh` green on both OSes; all docs carry the reconciled status |
| Stage 2 | Spikes, gate 1 | Mostly done | Spikes that block foundation work (doc 35 wave A/B): S3 GC log at scale, S6 wire format, S23 git engine, S25 restore drill, S2 Garnet, S8 VM sizing, S22 GitHub fork-and-PR. S3, S6, S23 and S25 have written results; S2, S8 and S22 wait on owner hardware or owner setup | Each spike has a written result; the wire format and git engine decisions are recorded in DECISIONS-LOG |
| Stage 3 | Foundation (implementation-plan wave 1) | Partly done | `Bazaar.Contracts` core types; Postgres schema, `migrate` command, GC log with NOTIFY; managed PostgreSQL; `Bazaar.Server.Host`; pairing routes; gateway adapters and policy. Still open: the settings registry, the full protocol replay test, and the fake agent end-to-end check | A fake agent posts to a GC, a client replays after a dropped socket, a model call is denied by policy, all on real Postgres in CI |
| Stage 4 | Company core (wave 2) | Partly done | Agent runtime and turn loop, RyuseiFuda (Workday board, Orrery scheduling and triggers, Roadmap read model), loop guards and Psych Ward, interrupt stack, company calendar and org services, Council and reputation (partial), taint and approval rules. Open: Souls and Vault-backed memory in full, work-life simulation in the loop, Council votes end to end | Scripted multi-agent day in replay: shifts, a vote, a blocked task, a loop-guard trip and an approval, with no human input |
| Stage 5 | Knowledge and identity (wave 3) | Partly done | Constellation renderer and LOD, git DAG indexer, mail back ends (Cloudflare, generic, scam-bait quarantine), GitHub adapter, Identity Service (partial). Open: Vault writer and access layer, PR flow with owner merge, Passport | An agent opens a PR from a task, the owner approves a signed merge, and the card moves to Done |
| Stage 6 | Windows client (wave 4) | Partly done | `Bazaar.Client.Core` shell and panes, theme model, `--panes` window with headless tests, RyuseiFuda panes, Constellation renderer. Open: Monaco (gate S12), ConPTY terminal (S11), diff and PR UI, the command palette and the start-screen dashboard | Avalonia.Headless suite green; the owner runs a day from the Windows client |
| Stage 7 | Android and Linux clients | Partly done | Android pairing, GC pane, API 37, Keystore credentials; Linux head with single-instance forwarding; packaging. Open: Android push and Play closed test (S7), native diff surface (S27), Velopack updates and channels | Google Play closed-test build; Linux package installs and updates on its channel |
| Stage 8 | Environments and engines (wave 5) | Planned (partial code) | Agent VM pair on Hyper-V (QEMU/KVM later), guest agent, browser profiles; coding harness; Godot headless adapter, Roblox Studio bridge; fun systems (Psych Ward, Vent). Hyper-V is blocked on S8 | An agent builds and tests a Godot sample in its own VMs |
| Stage 9 | Hardening | Started | Backup (scheduled base backups and `pg_dump`, restore and upgrade runners, S25 restore drill passed on Linux and Windows); remote access and TLS work. Open: export, the Windows restore drill as a gate, privacy settings, community posting tiers, security review, performance budgets, counsel items, history purge (with the owner's go-ahead) | Restore drill passes; counsel sign-off recorded; zero open blocking questions in the design docs |

Stage numbers are roadmap labels, not release versions. Releases are SemVer tags on `main` (`docs/VERSIONING.md`). Milestones can overlap: a later milestone's spikes and docs may start early. Order changes need the owner.

## v1.0.0 "Zuhal": the readiness gate [scope DECIDED, date none]

v1.0.0 is not a milestone on this list. It ships only when the owner judges Bazaar truly ready: the full v1.0 scope below works, the first real projects have run on it, the open design questions and counsel items are closed, and the owner says go. Until then every build stays 0.x "Beta". Scope: the full v1.0 scope from CLAUDE.md. Server, Windows, Android and Linux clients. GCs, Kanban (RyuseiFuda), Council, reputation, memory and the Vault, Constellation, gateway with FreeLLMAPI, GitHub and email. Observatory, Company Constitution, Org Chart, Interrupt System, Company Calendar, Health and Analytics, Digital Twin, Skimmer Bouncer (starting defer-only), Psych Ward and vent post, What If and Time Machine, Mirror Agent, Lucky mode and Playtest. Identity Service, Bazaar Archive, Talent Pool, Genetic Memory Transfer, Hive Mind, Company Memory, Bazaar SDK and GC metadata. Nigerian Prince Protocol stays optional behind the setup wizard. First supported host: Windows (the owner's PC). Ships on Stable; v1.x minors add features compatibly.

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
