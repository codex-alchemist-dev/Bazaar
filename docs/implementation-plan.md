# Bazaar implementation plan (draft, 2026-10-24)

Status: DRAFT for owner review. Built from the status-tagged design docs in `docs/deep-dives/` and the research in `docs/research/notes/` and `docs/research/reports/`. Only DECIDED items are built on; anything else is a question to the owner first.

## Working method (owner-approved)

- No separate v0.001. Claude builds toward v1.0 directly, with Haiku 5.5 workers directed by the main session (see `CLAUDE.md`).
- Per work package: read the design doc, write or update the doc if the design changes, write tests with the code, run build and tests, commit. Test and fix as you go.
- Independent work packages run in parallel, one git worktree and branch (`agent/<task>`) each. The main session merges, resolves conflicts and runs the full build.
- Ask the owner about any detail the docs do not settle. Do not assume.

## Fixed choices that shape the code

| Area | Choice | Source |
|---|---|---|
| Language and runtime | C# on .NET 11 (STS; see DECISIONS-LOG round 15), one codebase | owner, 2026-10-24 |
| Server | One OS-agnostic build; OS-specific parts behind providers | owner, 2026-10-24 |
| Project names | `Bazaar.Contracts`, `Bazaar.Server`, `Bazaar.Server.Host`, `Bazaar.Sdk`, `Bazaar.Client.Core`, `Bazaar.Client.Windows`, `.Android`, `.Linux` | owner, 2026-10-24 |
| Database | PostgreSQL 18 + pgvector. Raw Npgsql 10 with one shared `NpgsqlDataSource`, SQL-first repositories, `NpgsqlBatch`, embedded versioned SQL migrations with a small runner (OS-specific extension steps). EF Core only for CRUD-shaped tables if ever needed. Marten is not the core store | delegated to Claude by owner ("modern, performant"); research: `docs/research/notes/Bazaar build tooling research/data_access.md` |
| GC log | Append-only table; append and `NOTIFY` in one transaction with a small `{gc_id, seq}` payload; one `LISTEN` connection per process; clients replay from the log on reconnect | research (PostgreSQL NOTIFY docs verified) |
| Wire format | JSON via source-generated System.Text.Json behind `IWireCodec` (owner, 2026-10-25, spike S6). Start with raw WebSocket and an envelope with per-GC sequence numbers | owner: decide at first spike |
| Cache and pub/sub | Interface; Valkey on Linux, Garnet on Windows (compatibility to be spiked) | owner, 2026-10-24 |
| Search | Interface; `pg_search` on Linux, Postgres full-text on Windows | owner, 2026-10-24 |
| Model gateway | Bazaar's own gateway enforces per-agent model policy. Providers are pluggable; FreeLLMAPI is first-class (guided setup, dashboard usage and health, default free-lane preset, manual and custom dynamic routing). Claude lane uses Anthropic API keys | owner, 2026-10-24 |
| Vault | Markdown with `[[wiki-links]]` in a git repo; Postgres indexes it | owner, 2026-10-24 |
| Tests | xUnit v3 first; `Avalonia.Headless` for UI | owner, 2026-10-24 |

## Waves

### Wave 0 — scaffold (main session, sequential)

`global.json`, solution file, `Directory.Build.props`, `Directory.Packages.props` (central package management), `.editorconfig`, nullable and warnings-as-errors, empty projects with test projects, `build.sh` and `build.ps1` entry points, GitHub Actions workflow on hosted runners. Exact recipe comes from `docs/research/notes/Bazaar build tooling research/`. Done when `dotnet build` and `dotnet test` pass on the empty solution.

### Wave 1 — foundation (parallel work packages)

| WP | Scope | Depends on |
|---|---|---|
| 1 | `Bazaar.Contracts`: core types (GC, message, agent reference, task, vote), the envelope with per-GC sequence numbers, JSON source generation | Wave 0 |
| 2 | Postgres schema, migration runner, append-only GC log, NOTIFY fan-out, real-Postgres test fixture | Wave 0 |
| 3 | Provider interfaces (service host, VM backend, cache and pub/sub, search) with Windows and Linux stubs and a fake provider for tests | Wave 0 |
| 4 | `Bazaar.Server.Host`: Worker Service, WebSocket endpoint, reconnect and replay, configuration, logging | WP 1, 2 |
| 5 | Gateway layer: provider interface, per-agent model policy, OpenAI-compatible provider, FreeLLMAPI provider, fake provider | Wave 0 |

### Wave 2 — the company core

Agent runtime (Souls, private and shared memory with pgvector, presence, work-life simulation, async inboxes, loop guards), Kanban with dependency (blocked-by) enforcement, Council voting, reputation, GC nesting and @mentions. The Skimmer Bouncer is built only after measuring on real GC transcripts.

### Wave 3 — knowledge and identity

Vault (Markdown in git through LibGit2Sharp, wiki-link indexer, search provider), Constellation data model, GitHub Apps integration (webhooks, installation tokens; run the two throwaway-repo tests first), email subsystem (domain-agnostic, four back ends), Soul Passport and Identity Service (envelope encryption, audit log).

### Wave 4 — clients

`Bazaar.Client.Core` shell: theme system (one theme file drives Avalonia and Monaco), command palette, dashboard, GC view with Discord-style messages, Kanban and Constellation views, Vim-style navigation. Windows head first, then Android, then Linux. Spikes before committing: Monaco in a WebView, terminal control, Android IME, foldable layout, 5k-node Skia graph.

### Wave 5 — environments, engines, release

Hyper-V VM backend after the topology benchmark; QEMU/KVM backend later. Engine adapters (Godot headless first; Roblox on the human desktop). CI/CD engine, installers and updates, signing, hardening, licence and history clean-up before the first public release.

## Spikes and checks (not product code)

1. Garnet on Windows: StackExchange.Redis compatibility and persistence.
2. GitHub App tests on a throwaway public repo: issue creation on an uninstalled repo; fork then PR.
3. Agent VM sizing benchmark on the owner's PC: RAM, CPU and boot cost of the Windows + Linux VM pair per agent, and how many pairs fit.
4. Avalonia: Monaco inside a WebView on each platform; terminal control; Android IME on a real foldable; Skia graph benchmark.
5. FreeLLMAPI: confirm routing modes and what usage and health data it exposes (read its API and code).

## Open questions tracked in the docs

See each doc's "Open Questions" section and `docs/research/reports/Bazaar stack feasibility research.md`.
