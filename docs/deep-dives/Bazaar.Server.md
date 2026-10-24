# Bazaar.Server — Backend, GitHub Presence, Repo & Infra Stack

Part of the Bazaar document family. Siblings: **Bazaar** · **Bazaar.Windows** · **Bazaar.Android** · **Bazaar.Linux** · **Bazaar.Soul** · **Bazaar.Constellation**

Status legend: **[DECIDED]** / **[PROPOSED]** / **[REJECTED/CHANGED]** / **[RESEARCHED]** / **[OPEN]** — see Bazaar.md for definitions.

---

## 1. Product Identity

- **[DECIDED]** Philosophy: **"Run the company."** The living organism. All real compute happens here: Git, Godot/engine headless ops, all agent logic, OmniRoute, DB, and every agent's VM/environment (see **Bazaar.Soul**). Runs on your NUC as a headless service — **[DECIDED 2026-10-24]** one OS-agnostic .NET build that runs on Windows or Linux, with every OS-specific piece (service host, VM backend, cache, search) behind a provider interface. First supported and tested host: Windows. This refines the 2026-08-07 "Bazaar.Server.Windows for now" note, which scoped the first platform, not the architecture. If nobody connects for 48 hours, the company should still be functioning — discussing, researching, reviewing, planning.
- **[DECIDED]** No web interface, ever, on any client. **Server does everything** — Windows/Android/Linux are thin-but-rich renderers over WebSocket state sync, except for syncing your currently-viewed branch to a local folder for live playtesting.

---

## 2. GitHub & Community Presence Architecture

*(Superseded from an earlier "machine accounts" plan — corrected after research.)*

- **[DECIDED — corrected]** **Machine accounts are a dead end.** GitHub's ToS caps one free machine account per person/legal entity, no paid path to buy more — confirmed, no account-level workaround exists.
- **[DECIDED]** **The real answer: GitHub Apps, not accounts.** A GitHub App is an application *registration*, not a user account — create as many for free as you want, all owned by the `codex-alchemist-dev` org. Plan: one App per agent (`claude-bazaar[bot]`, `deepseek-bazaar[bot]`, `gemi-bazaar[bot]`, `chatgpt-bazaar[bot]`), each with its own name, avatar, description — genuinely distinct identities, disclosed as bots via GitHub's own `[bot]` suffix.
- **[DECIDED]** Install the App(s) on the **org** — gives write access to org-owned repos (where forks live), and combined with GitHub's baseline public-repo read access, unlocks the standard **fork-and-PR flow on any public repo**: fork into the org, push a branch, open a PR against upstream. No installation on the upstream repo required — same mechanism any human contributor uses.
- **[RESEARCHED — unverified]** One community-confirmed report suggests a GitHub App's installation token can open *issues* directly on a public repo it was never installed on, since public-repo writes for issues may follow the same "no special permission needed" rule as public reads. Not primary documentation. **Action item: test empirically on a throwaway low-stakes public repo before designing anything load-bearing around it.**
- **[DECIDED]** GitHub Apps have **no native email inbox or notification system** — user-account-only feature. Apps get **webhooks** instead (real-time HTTP events). Want the *feel* of "Claude got an email about GitHub activity"? Build it: webhook → Bazaar server → routed into the agent's GC and/or its configured-domain email inbox. Arguably better than native GitHub notifications since Bazaar decides how each agent experiences the event.
- **[DECIDED]** Git operations on the server (clone/fetch/branch/commit): **libgit2** via **LibGit2Sharp** (C# binding), not shelling out to the `git` CLI — structured, fast.
- **[DECIDED]** Diff rendering for the custom GitHub interface reuses **Monaco's built-in diff-editor mode** (see Bazaar.Windows) rather than a separate component.

---

## 3. Tech Stack

| Layer | Decision | Status |
|---|---|---|
| **Database** | PostgreSQL + pgvector | **DECIDED.** Relational data and vector search in one service. **[RESEARCHED]** Confirmed capable of Discord-style GC chat at your actual scale (nowhere near the billions-of-messages scale that caused Discord's own DB problems) — standard indexing on `gc_id` + `timestamp` is sufficient; monthly partitioning is the future scaling lever if ever needed. |
| **Vault / document search** | ParadeDB's `pg_search` | **DECIDED** — replaces the earlier Meilisearch/Typesense proposal. Full detail in **Bazaar.Constellation**. **[DECIDED 2026-10-24 — per-OS provider behind one search interface: `pg_search` on Linux, Postgres full-text search on Windows]** No documented Windows build of `pg_search` exists (prebuilt binaries: Debian, Ubuntu, RHEL, macOS). Windows-native fallback: Postgres full-text search + pgvector hybrid ranking. AGPL scope if an installer bundles it needs counsel. |
| **Cache / Queue** | **Valkey**, not Redis | **DECIDED — changed.** Redis relicensed away from permissive open source in 2024 (SSPL/RSAL), back to AGPLv3 in 2025 — AGPL's network-service copyleft clause is a real concern for something explicitly being open-sourced for self-hosting. Valkey (Linux Foundation fork, BSD-3-Clause, wire-compatible) avoids the license question and currently benchmarks ahead on throughput. Purpose: **cache** = hot copies of frequently-read data (reputation scores, Kanban state) so Postgres isn't hit every read; **queue/pub-sub** = event fan-out (a message reaching every client watching a GC) and scheduled jobs (Work-Life Simulation shift changes, async inbox delivery). **[DECIDED 2026-10-24 — per-OS provider behind one cache/pub-sub interface: Valkey on Linux, Garnet (candidate) on Windows]** Valkey is not officially supported on Windows (WSL2, Docker or a Linux VM only; upstream has no plans for Windows binaries). Microsoft Garnet (MIT, Redis-protocol, documented as tested on Windows) is the native candidate; its StackExchange.Redis compatibility and Windows persistence are unverified. See `reports/Bazaar stack feasibility research.md`. |
| **Client↔Server transport** | **WebSocket** (raw, not Socket.io), Protobuf message schemas | **DECIDED (raw WebSocket) — Protobuf part reopened.** Socket.io dropped — exists to smooth browser inconsistencies, irrelevant with no web client, and forces native clients to speak a nonstandard framing protocol for nothing. **[RESEARCHED]** WebTransport (HTTP/3/QUIC) hit browser "Baseline" support March 2026, but non-browser/native library maturity (Node, .NET) is still catching up — revisit for v1.0, stay on WebSocket now. Message format: Protobuf schemas over the WebSocket, not raw JSON — one schema now generates typed bindings for the backend and for C# covering all Avalonia clients (see Bazaar.Windows), enforcing the Bazaar SDK contract at the compiler level. **[PROPOSED — reopened 2026-10-24]** With .NET on both sides, a shared C# `Bazaar.Contracts` project (source-generated System.Text.Json) is the simpler option; Protobuf was an assistant proposal and was never ratified. **Owner (2026-10-24): decide at the first client-server spike.** Reconnect design [RESEARCHED]: small envelope with per-GC sequence numbers and replay-from-seq. |
| **AI routing/gateway** | OmniRoute | **DECIDED.** Infrastructure ("like electricity"), not part of Bazaar's identity. RTK+Caveman token compression should be verified empirically on real code/diffs before being trusted for source files. |
| **DeepSeek supply** | NVIDIA NIM free tier via OmniRoute | **DECIDED** — "practically infinite" in practice, not literally (~40 RPM / ~1,000 req/day per free endpoint before fallback). |
| **Server backend language** | **.NET (C#), target .NET 10 LTS** | **DECIDED 2026-10-24** (owner): .NET everywhere — server and all Avalonia clients. See Section 4. |
| **Repo structure** | **Two repos**: public `bazaar` (open-source platform) + a private company-specific repo (real credentials, Soul data, Vault content) | **DECIDED — reconciled.** Supersedes an earlier single-monorepo proposal — open-sourcing the platform while keeping operational data private requires two repos with two visibility settings. Internal layout still follows the general shape `/server`, `/client`, `/sdk`, `/plugins`, `/docs`, not finalized in detail — now also needs to account for the shared-UI project structure defined in **Bazaar.Windows**. |
| **GitHub org** | `codex-alchemist-dev` | **DECIDED** (created). |

---

## 4. Backend language — decided: .NET

- **[DECIDED 2026-10-24]** .NET (C#) for the server and every client. Target **.NET 10 LTS** (supported until November 2028; .NET 8 LTS and .NET 9 STS end November 2026) **[RESEARCHED, primary source]**. One language across server and clients, one shared contracts project, LibGit2Sharp for Git, Npgsql for PostgreSQL, Hyper-V management through WMI/PowerShell, Avalonia for UI.
- **Hosting [RESEARCHED]:** Worker Service with `AddWindowsService()` on Windows (systemd on Linux — standard .NET pattern, not re-verified). Keep mutable data under `%ProgramData%`. A hard kill can skip `StopAsync`, so in-flight agent turns must persist their own state. Crash-restart recovery settings still need verification.
- **Transport [RESEARCHED]:** raw ASP.NET Core WebSockets; SignalR is the fallback; gRPC is not recommended for v1.
- **Admin console:** none inside the service process. Use a CLI or the Avalonia client over the same API. Terminal.Gui v2 is viable as a *separate* TUI client attached over the WebSocket API. Whether to build a TUI client at all is an **[OPEN]** question for the owner.
- **Background work [RESEARCHED]:** `BackgroundService` plus durable Postgres tables; Quartz.NET if cron triggers are needed. Orleans (virtual actors, one per agent) is worth a one-agent spike. Native AOT: not for v1.
- **Considered and not chosen:** Bun, Go, Elixir/Phoenix (the BEAM supervision model fits agents well), and Rust. Rust offers no advantage here: the workload is I/O-bound on model APIs, VMs and Postgres, and Windows/Hyper-V management plus shared C# contracts favor .NET. Keep every boundary a process or protocol boundary so a non-.NET component can still be added later.
- **Exceptions:** code that must run *inside* an engine (Luau for Roblox Studio, GDScript or C# for Godot). Engine adapters are out-of-process and talk to the server over a protocol.

---

## Open Questions (Server)

1. **Wire contracts** — shared C# JSON contracts vs Protobuf (Section 3).
2. **Bazaar SDK — actual shape** — agreed in principle, no concrete API surface designed.
3. **CI/CD actual engine** — concept is clear (build+test on every push/PR, auto-ship on pass) but no implementation chosen: self-built minimal task-runner vs. an existing tool like Woodpecker.
4. **Repo internal folder structure** — the two-repo split (public/private) is decided; detailed internal layout of each, including the shared-UI project from Bazaar.Windows, is not finalized.
5. **Provider interfaces** — DECIDED direction (2026-10-24): define cache/pub-sub, search, VM-backend and service-host interfaces so one build runs on Windows and Linux. The interface shapes are OPEN. Garnet's StackExchange.Redis compatibility and Windows persistence still need a spike.
6. **Model supply policy** — **[DECIDED 2026-10-24]** the Claude lane uses Anthropic API keys only, with a Haiku-heavy budget. Routing Pro/Max subscription credentials through a gateway is not permitted; routing Pro/Max subscription credentials through a gateway is not permitted by Anthropic's current terms (primary source, fetched 2026-10-24). The NIM "~1,000 requests/day" figure has no source.
7. **Admin console** — superseded by item 10.
8. **Licence** — **[DECIDED direction 2026-10-24, owner]** AGPL-3.0 for the server and clients, Apache-2.0 for the SDK and contracts. DCO vs CLA is OPEN. Counsel review is required before the first public release (including the `pg_search` bundling question).
9. **Email subsystem** — **[DECIDED 2026-10-24]** domain-agnostic, with pluggable account back ends including a Bazaar.Server-integrated email client. The back-end list and the built-in client's scope are OPEN.
10. **Server admin surface** — **[DECIDED 2026-10-24]** an admin panel inside the apps (Windows, Android, ...). A browser or TUI status panel was requested too; an HTML page conflicts with "no web interface, ever", so it is OPEN pending an exception decision.
