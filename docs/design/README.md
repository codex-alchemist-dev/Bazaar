# Bazaar design set (docs 01 to 36)

Status: DRAFT for owner review (2026-10-25) · 36 design docs · 487 owner questions in total · cross-cutting issues are in [CONFLICT-REGISTER.md](CONFLICT-REGISTER.md)

These drafts turn the decisions in `CLAUDE.md` and `docs/deep-dives/` into buildable designs, one subsystem per file, before any code is written. They were written in parallel from the brief in [_BRIEF.md](_BRIEF.md), so they overlap and sometimes disagree. The register lists those disagreements (45 entries: 14 BLOCKER, 21 SHAPING, 10 MINOR). Read the register's blockers before judging any single doc.

## Index

"Qs" is the number of owner questions in the doc's own "Questions for the owner" section. "Key dependencies" lists the docs whose contracts this one relies on most; the "Depends on" headers inside some drafts use placeholder names, so these columns were taken from the content.

### Core record and protocol

| # | Doc | Purpose | Qs | Key dependencies |
|---|---|---|---|---|
| 01 | [01-gc-data-model.md](01-gc-data-model.md) | The group chat as the append-only record: entries, per-GC sequence numbers, edits, DMs, mentions, trust tags, catch-up | 14 | none (base); feeds 02, 04, 06, 10, 12, 15 |
| 02 | [02-wire-protocol-and-sync.md](02-wire-protocol-and-sync.md) | One WebSocket per device: envelope, replay, heartbeats, backpressure, versioning, server-driven layout, push wake hint | 14 | 01, 04; auth from 32; push from 29 |
| 03 | [03-server-host-and-providers.md](03-server-host-and-providers.md) | `Bazaar.Server.Host`, OS providers, config, startup, shutdown, health, unattended operation | 14 | 04, 14, 16; spikes S1 to S4 (35) |
| 04 | [04-postgres-schema-and-migrations.md](04-postgres-schema-and-migrations.md) | PostgreSQL conventions, migration runner, GC append transaction, hybrid search, backup basics | 12 | 01, 03; every area table (see register C16) |

### Agents and company systems

| # | Doc | Purpose | Qs | Key dependencies |
|---|---|---|---|---|
| 05 | [05-model-gateway.md](05-model-gateway.md) | Bazaar's own model gateway: providers, per-agent policy, quota states, FreeLLMAPI setup, catalogue | 16 | 14 (secrets), 09 (quota to vacation), 23 (usage) |
| 05b | [05b-freellmapi-first-class-provider.md](05b-freellmapi-first-class-provider.md) | FreeLLMAPI as the testing source and first-class provider: base URL, key reference, `agent-default` route, limits (PROPOSED) | 4 | 05, 14, 34 |
| 06 | [06-agent-runtime.md](06-agent-runtime.md) | Agent lifecycle, wake model, turn loop, context assembly, tool dispatch, loop guards, crash recovery | 14 | 01, 05, 08, 09, 15, 24 |
| 07 | [07-soul-system.md](07-soul-system.md) | Soul record and versions, auditable judgments, roster presets, retirement, export format | 14 | 06, 08, 11, 14, 15, 24 |
| 08 | [08-memory-system.md](08-memory-system.md) | Agent memory kinds and scopes, SoulGuard validation, consolidation, trust tiers, retrieval, Hive boundary | 14 | 05, 07, 15, 18 |
| 09 | [09-work-life-simulation.md](09-work-life-simulation.md) | Shifts, breaks, vacations, curfew, quota-to-rest mapping, cover, presence | 15 | 05, 06, 24 |
| 10 | [10-council-and-voting.md](10-council-and-voting.md) | Council votes: sealed first ballot, reveal, one follow-up, escalation to the owner | 17 | 01, 06, 09, 12, 18 |
| 11 | [11-reputation-and-peer-review.md](11-reputation-and-peer-review.md) | Per-skill reputation from outcomes and peer review, anti-gaming, promotion | 15 | 12, 21, 10; Valkey cache (see C07) |
| 12 | [12-kanban-and-dependencies.md](12-kanban-and-dependencies.md) | Dependency board: transitions, derived Blocked, task service, gates | 12 | 01, 10, 15, 17, 21 |
| 13 | [13-skimmer-bouncer.md](13-skimmer-bouncer.md) | Gatekeeper that decides wake-now vs digest; defer-only, measured before enforced | 9 | 01, 05, 06, 09 |
| 24 | [24-company-systems-core.md](24-company-systems-core.md) | Constitution, Org Chart, Interrupt System, Company Calendar | 14 | 06, 07, 09, 12, 18 |
| 26 | [26-fun-systems.md](26-fun-systems.md) | Psych Ward, vent post, What If, Time Machine, Mirror Agent, Lucky mode, Playtest, Sidewalk | 17 | 01, 06, 09, 11, 13, 15, 16, 17, 18, 20 |

### Security, identity and external

| # | Doc | Purpose | Qs | Key dependencies |
|---|---|---|---|---|
| 14 | [14-identity-service-and-passport.md](14-identity-service-and-passport.md) | Identity Service and Soul Passport: credential custody, leases, GitHub App tokens, kill switch | 12 | 07, 15, 03, 21, 33 |
| 15 | [15-security-taint-and-approvals.md](15-security-taint-and-approvals.md) | Trust tags, session taint, approval tiers and flow, egress, audit, quarantine, rate limits | 14 | 01, 06, 14, 16, 17 |
| 21 | [21-github-integration.md](21-github-integration.md) | One GitHub App per agent, webhooks, PR GCs, PR lifecycle, rate limits | 13 | 14, 15, 18, 12, 11, 17 |
| 22 | [22-email-subsystem.md](22-email-subsystem.md) | External-only mail: four back ends, inbound screening, quarantine, outbound tiers, optional bait protocol | 16 | 01, 14, 15, 05 |
| 32 | [32-remote-access-auth-and-privacy.md](32-remote-access-auth-and-privacy.md) | Remote access options, device pairing, tokens, sign-in page, roles, privacy settings | 12 | 01, 02, 14, 15, 04 |
| 36 | [36-community-and-external-platforms.md](36-community-and-external-platforms.md) | Disclosed bot participation in outside communities: policy matrix, tiers, quotas, kill switch | 13 | 14, 15, 21, 22 |

### Environments and engines

| # | Doc | Purpose | Qs | Key dependencies |
|---|---|---|---|---|
| 16 | [16-agent-vms-and-environments.md](16-agent-vms-and-environments.md) | Two VMs per agent, golden image, guest protocol, mounts, egress, resources, licensing | 13 | 03, 07, 14, 15, 17 |
| 17 | [17-coding-agent-harness.md](17-coding-agent-harness.md) | Coding tools, edit formats, server-side verification, git workflow, review flow | 12 | 16, 15, 12, 21, 18 |
| 20 | [20-engine-integration-api.md](20-engine-integration-api.md) | Godot and Roblox adapters, capabilities, pane streaming, Studio bridge | 13 | 15, 16, 27, 28 |

### Knowledge

| # | Doc | Purpose | Qs | Key dependencies |
|---|---|---|---|---|
| 18 | [18-vault.md](18-vault.md) | Markdown Vault in a private git repo: links, writer, indexer, search, decisions | 12 | 01, 04, 05, 21 |
| 19 | [19-constellation-and-git-solar-system.md](19-constellation-and-git-solar-system.md) | Graph view and git visualisation: server layout, client Skia renderer, budgets | 10 | 04, 12, 18, 21, 27 |
| 25 | [25-company-systems-knowledge.md](25-company-systems-knowledge.md) | Health and Analytics, Digital Twin, Archive, Company Memory, Hive Mind, Talent Pool, Genetic Memory Transfer | 15 | 07, 08, 11, 18, 21, 23 |

### Clients and packaging

| # | Doc | Purpose | Qs | Key dependencies |
|---|---|---|---|---|
| 27 | [27-client-architecture.md](27-client-architecture.md) | `Bazaar.Client.Core`: stores, theme, keyboard model, layout, GC message pipeline | 14 | 01, 02, 20, 31, 32 |
| 28 | [28-windows-client.md](28-windows-client.md) | Windows head: IDE, Monaco, terminal, PR UI, Studio bridge, default apps, installer | 14 | 27, 02, 15, 20, 21, 31 |
| 29 | [29-android-client.md](29-android-client.md) | Development Console: fold layouts, radial menu, dictation, approvals, push | 13 | 27, 02, 15, 31, 32 |
| 30 | [30-linux-client-and-packaging.md](30-linux-client-and-packaging.md) | Linux client, packaging, versioning, server upgrade and rollback, signing, release pipeline | 14 | 27, 28, 29, 02, 03, 04, 33 |
| 31 | [31-local-coworker-npu.md](31-local-coworker-npu.md) | On-device inference: Intel and Qualcomm NPU paths, chips, voice cleanup, guards | 13 | 02, 05, 15, 34 |

### Operations and quality

| # | Doc | Purpose | Qs | Key dependencies |
|---|---|---|---|---|
| 23 | [23-observatory-and-usage.md](23-observatory-and-usage.md) | Live agent activity, presence, usage accounting, quota surfacing, telemetry | 14 | 05, 06, 09, 04, 02 |
| 33 | [33-backup-dr-and-export.md](33-backup-dr-and-export.md) | What is restored, recreated or escrowed; PITR; keys; rollback rule; instance migration | 14 | 01, 03, 04, 08, 14, 16, 18 |
| 34 | [34-testing-strategy.md](34-testing-strategy.md) | Test layers, real-Postgres fixtures, fake gateway and cassettes, verification gate, evals | 13 | 02, 04, 05, 06, 15, 17 |
| 35 | [35-spikes-plan.md](35-spikes-plan.md) | 25 time-boxed spikes (S1 to S25) with pass criteria, fallbacks and run locations | 12 | spans 02 to 33 |

## How to review these docs

1. **Start with the register.** [CONFLICT-REGISTER.md](CONFLICT-REGISTER.md) lists 45 cross-cutting issues, each with a multiple-choice question and a recommendation. Its 14 BLOCKER entries must be answered before the affected code is written; answering them settles many per-doc questions at once.
2. **Then read each doc's "Questions for the owner".** Every question has a recommended default and a reason. Answering "default" is a valid answer. Ask yourself only where you disagree.
3. **Check the tags.** Only DECIDED items are requirements. Anything tagged PROPOSED, OPEN or RESEARCHED is a proposal or a question. Where a draft contradicts a DECIDED item, it says so in its own "Conflicts" section; the register merges those.
4. **Look at "Decided inputs" if a design seems odd.** It lists the decisions the draft treated as fixed, with their source. If an input is wrong or stale (see the last section of the register), the draft built on it is affected.
5. **Evidence labels are kept as the research gave them.** verified means a primary source was read (often through a summary), secondary means a snippet or news report, unverified means not read. Legal, licence and privacy points are research, not advice; "needs counsel" marks the ones that must go to a lawyer before a public release.
6. **Do not code from a draft yet.** The drafts are for owner review. Per `CLAUDE.md`, do not build on PROPOSED or OPEN items without asking; after your answers, the docs and the deep-dives need the edits listed under "Stale lines" in the register.

## Status legend

| Tag | Meaning |
|---|---|
| DRAFT | This design doc is written for review and is not approved. Every doc here is DRAFT. |
| DECIDED | The owner decided it (see `docs/deep-dives/Bazaar.md` for definitions). A requirement. |
| PROPOSED | A recommendation not yet decided. Do not build on it without asking. |
| OPEN | A question the owner has not answered. |
| RESEARCHED | A finding from the research notes; informs but does not decide. |
| REJECTED / CHANGED | Superseded or turned down; kept for the reasoning. |
| verified / secondary / unverified | Evidence status of a factual claim, as labelled in the research notes. |
| BLOCKER / SHAPING / MINOR | Register severity: decide before code / changes a design materially / wording, defaults or naming. |

## Recommended review order

Each wave builds on the previous one. Within a wave, the order is the suggested reading order.

1. **Register blockers C01 to C14.** They cut across the waves below.
2. **Record, protocol and host:** 01, 02, 04, 03. These fix the log, the wire and the server shell everything else attaches to.
3. **Model access and the agent loop:** 05, 06, 09, 13, 08, 07. The gateway, the runtime, work-life, wake rules, memory and the Soul.
4. **Security and identity:** 15, 14, 16, 17, 21. Taint and approvals first, then credentials, the VMs and the coding harness that run under them.
5. **Company systems:** 10, 11, 12, 24. Council, reputation, Kanban, and the Constitution and calendar that unblock Soul onboarding (07 cannot activate a Soul without an approved Constitution).
6. **Knowledge:** 18, 19, 25. The Vault first, then its graph and the derived systems.
7. **External-facing:** 22, 36, 20, 32. Mail, community platforms, engines, remote access.
8. **Clients and packaging:** 27, 31, 28, 29, 30. Shared core, on-device inference, then the three heads.
9. **Operations and fun:** 23, 33, 34, 26.
10. **Spikes last, but plan them early:** 35. Several register blockers (C07, C12, C13) are settled by a spike (S2, S23, S22); the cloud spikes can start before the owner has answered everything.
