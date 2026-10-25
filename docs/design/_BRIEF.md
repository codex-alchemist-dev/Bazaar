# Bazaar design doc brief (read this first)

You are drafting ONE detailed design doc for Bazaar, an open-source platform where persistent AI agents work as a software company, coordinating in group chats (GCs) with no master orchestrator. These docs are for the owner to review BEFORE any code is written. Your job is to turn the owner's decisions and the research into a clear, buildable design for one subsystem, and to surface every question the owner still needs to answer.

## What to read

1. `/home/user/Bazaar/CLAUDE.md` — decisions in force. Never contradict a DECIDED item.
2. `/home/user/Bazaar/docs/deep-dives/Bazaar.md` and the deep-dive doc(s) for your area (`Bazaar.Server.md`, `Bazaar.Soul.md`, `Bazaar.Windows.md`, `Bazaar.Android.md`, `Bazaar.Constellation.md`, `Bazaar.Linux.md`). Status tags: DECIDED, PROPOSED, REJECTED/CHANGED, RESEARCHED, OPEN.
3. `/home/user/Bazaar/docs/implementation-plan.md`.
4. The research that bears on your area: `/home/user/Bazaar/docs/research/reports/` (two synthesized reports) and the relevant notes under `/home/user/Bazaar/docs/research/notes/`. Notes carry verified / secondary / unverified labels; keep them, never upgrade a claim.
5. Your assignment (below) names the most relevant files; open others if you need them.

## Rules

- **Only DECIDED items are requirements.** Anything PROPOSED or OPEN is a proposal or a question; label it that way. Do not invent product decisions. When the sources do not settle something, write a proposal marked **[PROPOSED]** and add it to Questions for the owner.
- Keep to the stack in CLAUDE.md: C#, .NET 10, OS-agnostic server with providers per OS, raw Npgsql with SQL-first repositories (no ORM for the core), Avalonia clients, xUnit v3.
- No code beyond short interface or schema sketches (C# signatures, SQL table outlines, JSON examples). No implementation.
- No secrets, credentials, real hostnames, personal data or private-company material. Placeholder values only.
- Legal, licensing, terms and privacy-law points are research, not advice; mark them "needs counsel" where relevant.
- Be concrete and compact: roughly 500 to 1,300 words. Prefer tables and short lists. No marketing language, no padding.
- If your area collides with another subsystem, name the other subsystem and the exact contract between you (what you call, what you emit), but do not design the other side.
- If you find a contradiction between decisions or between a decision and the research, report it in "Conflicts" instead of resolving it silently.

## Output

Write exactly one file at the path in your assignment, using this skeleton (omit a section only if it truly does not apply, and say so):

```
# <Subsystem name>

Status: DRAFT for owner review (<today's date>) · Owner questions: <count>
Depends on: <other design docs by file name> · Used by: <other design docs>

## Purpose
Two or three sentences: what this is and why it exists.

## Decided inputs
Bullet list of the DECIDED items (with source doc) that constrain this design.

## Design
The proposed design. Components, responsibilities, flows. Mark each non-DECIDED element [PROPOSED].

## Data and interfaces
Tables, record shapes, interface signatures, message shapes (sketches only).

## Behaviour and edge cases
Failure modes, limits, ordering, concurrency, offline/reconnect, security-relevant behaviour.

## Testing
How this is tested without real models or displays (fake gateway, replay, Avalonia.Headless, real Postgres). Key test cases.

## Work packages
A numbered list of small, independent implementation tasks (each one a few files plus tests) that parallel Haiku agents could build later, with dependencies between them.

## Conflicts
Contradictions or risks found between decisions and research.

## Questions for the owner
Numbered, specific, answerable questions, each with a recommended default and why. Ask about small details too (names, defaults, limits, menus, labels) rather than assuming.
```

## When you finish

Reply with a short report: the file path, the word count, the number of owner questions, and the three most important conflicts or risks you found. Do not commit, do not push, do not edit any other file.

## Design doc index (use these exact file names in Depends on / Used by)

| # | File | Subject |
|---|---|---|
| 01 | 01-gc-data-model.md | Group chats, message log, DMs, edits |
| 02 | 02-wire-protocol-and-sync.md | WebSocket envelope, sequence numbers, replay |
| 03 | 03-server-host-and-providers.md | Server.Host, OS providers, config, status page |
| 04 | 04-postgres-schema-and-migrations.md | PostgreSQL schema, migrations, NOTIFY |
| 05 | 05-model-gateway.md | Own gateway, providers, FreeLLMAPI, policy |
| 06 | 06-agent-runtime.md | Agent loop, wake model, loop guards |
| 07 | 07-soul-system.md | Souls, roster presets, marketplace format |
| 08 | 08-memory-system.md | Agent memory, pgvector, consolidation |
| 09 | 09-work-life-simulation.md | Shifts, breaks, vacations, presence |
| 10 | 10-council-and-voting.md | Council votes |
| 11 | 11-reputation-and-peer-review.md | Reputation |
| 12 | 12-kanban-and-dependencies.md | Kanban dependency board |
| 13 | 13-skimmer-bouncer.md | Gatekeeper |
| 14 | 14-identity-service-and-passport.md | Identity Service, Passport |
| 15 | 15-security-taint-and-approvals.md | Taint tracking, approvals, audit |
| 16 | 16-agent-vms-and-environments.md | Two VMs per agent, guest agent |
| 17 | 17-coding-agent-harness.md | Coding tools and verification |
| 18 | 18-vault.md | Markdown Vault in git |
| 19 | 19-constellation-and-git-solar-system.md | Graph view |
| 20 | 20-engine-integration-api.md | Godot and Roblox adapters |
| 21 | 21-github-integration.md | GitHub Apps, webhooks, PR flow |
| 22 | 22-email-subsystem.md | Email, quarantine, scam-baiting protocol |
| 23 | 23-observatory-and-usage.md | Observatory, telemetry, usage and cost |
| 24 | 24-company-systems-core.md | Constitution, Org Chart, Interrupts, Calendar |
| 25 | 25-company-systems-knowledge.md | Health and Analytics, Digital Twin, Archive, Company Memory, Hive Mind, Talent Pool, Genetic Memory Transfer |
| 26 | 26-fun-systems.md | Psych Ward, What If and Time Machine, Mirror Agent, Lucky mode, Playtest |
| 27 | 27-client-architecture.md | Client.Core, MVVM, theming, command palette, layout |
| 28 | 28-windows-client.md | IDE, terminal, Monaco, diff and PR UI, default apps |
| 29 | 29-android-client.md | Fold layouts, radial menu, dictation, push |
| 30 | 30-linux-client-and-packaging.md | Linux client, packaging, updates, signing |
| 31 | 31-local-coworker-npu.md | On-device inference |
| 32 | 32-remote-access-auth-and-privacy.md | Remote access, pairing, sign-in page, roles, privacy compliance |
| 33 | 33-backup-dr-and-export.md | Backup, restore, export |
| 34 | 34-testing-strategy.md | Test layers, fake gateway, replay, evals |
| 35 | 35-spikes-plan.md | Spikes and success criteria |
| 36 | 36-community-and-external-platforms.md | Platform policies, posting tiers |

Length: aim for roughly 1,000 to 1,800 words; do not exceed 2,000. Trim sketches before trimming the Questions section.
