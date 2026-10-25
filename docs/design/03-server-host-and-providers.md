# Bazaar.Server.Host and OS Providers

Status: RECONCILED with DECISIONS-LOG (2026-10-25), cross-doc pass done · Owner questions: 2
Depends on: 02-wire-protocol-and-sync.md (status format, protocol version), 04-postgres-schema-and-migrations.md (schema, `migrate`, NOTIFY), 06-agent-runtime.md (turn and lease tables, recovery), 14-identity-service-and-passport.md (`ISecretStore`), 16-agent-vms-and-environments.md (VM contract), 23-observatory-and-usage.md (status page contents) · Used by: 02, 04, 06, 16, 23, 30, 33, 35

## Purpose
Bazaar.Server.Host is the one executable that runs the company server, as a Worker Service on Windows (first supported host) or Linux. This doc covers how it composes the platform-neutral `Bazaar.Server` library with per-OS providers, how it starts, stops and stays up unattended, how it reports health, and where its operator-configurable values live. The company must keep working when no client is connected.

## Decided inputs
- One OS-agnostic .NET 10 build; Windows is the first supported and tested host. (CLAUDE.md; Bazaar.Server.md §1)
- Service host, VM backend, cache and search are per-OS providers. No Windows-only API outside a provider. (CLAUDE.md; Bazaar.Server.md §3)
- Pub/sub (C07, round 1): GC fan-out is Postgres NOTIFY, owned by doc 04 and not a provider. Valkey (Linux) or Garnet (Windows) serve cache and presence only. Garnet waits for spike S2.
- VM topology (C03, round 1, owner 2026-10-24): two VMs per agent, a Windows VM and a Linux VM. Spike S8 is for sizing only. The VM contract is doc 16's.
- Secrets (C05, round 2): doc 14's `ISecretStore` holds provider and GitHub App keys. This doc's resolver only bootstraps the key-encryption key (KEK) and the database credential.
- Migrations (C44, round 11): an explicit `migrate` command. Startup only checks the schema version and refuses on mismatch.
- Status (C40, round 12): doc 02 defines the status format and protocol version, this doc the live and ready probes, doc 23 the status page contents.
- Table ownership (C04, C16): doc 06 owns agent, turn, step, lease and `inbox_item`. This doc owns only `job`, the host's background-work table.
- Ingress (C30, round 8): no inbound internet connections by default; the host polls.
- Distribution (C23, round 4): signed builds contain OSI-only components; Postgres full-text search on Windows.
- Configurability (round 13): operator-chosen values are settings in the registry below, with safe defaults. DECIDED items and security floors are not settings.
- Project names (C37, round 10): `Bazaar.Server.Providers.Windows` and `.Linux` are approved.
- GCs are append-only and never purged (Bazaar.md §2). No web client; a read-only status page is allowed.
- PostgreSQL 18 with pgvector; raw Npgsql 10; embedded versioned SQL migrations; `BackgroundService` plus Postgres tables (implementation-plan.md; Bazaar.Server.md §3).
- Model routing, including the Claude lane (C14), is doc 05's. This doc supplies bootstrap secrets only.

## Design
**Composition root [PROPOSED].** Only `Bazaar.Server.Host` builds the host. It calls `AddBazaarServer(options)` from `Bazaar.Server`, then registers each provider module whose `Supports(os)` returns true. Modules are an explicit list, not assembly scanning, which keeps Native AOT open.

**Project split.** `Bazaar.Contracts` holds the wire contracts shared with clients (CLAUDE.md). `Bazaar.Server` is platform-neutral: provider interfaces, services, Npgsql repositories, Postgres full-text search. No `Microsoft.Win32`, `System.Management` or `[SupportedOSPlatform]`; CA1416 is an error here. `Bazaar.Server.Providers.Windows` and `.Linux` hold the OS-bound modules; the Windows assembly never loads on Linux.

**Provider roles [PROPOSED].**

| Role | Windows | Linux | Readiness |
|---|---|---|---|
| `ServiceHost` | Windows Service | systemd | required when run as a service |
| `Vm` | Hyper-V | QEMU/KVM | per doc 16; optional by default |
| `Cache` | Garnet, only after S2 | Valkey | optional; presence and cache only |
| `Search` | Postgres FTS plus pgvector index | `pg_search` (WP14), Postgres FTS fallback | Postgres FTS always usable |

**Provider selection [PROPOSED].** Each `host.provider.<role>` setting is `auto` or an explicit kind. `auto` is a capability probe: it picks the highest-priority candidate whose `Probe()` is usable, so FTS works on both OSes and the Vm and Cache roles differ per OS. An explicit kind that is unusable fails startup; there is no silent fallback.

**Capability reporting [PROPOSED].** Each provider returns a `ProviderStatus`. The host logs one line per role at startup. The admin CLI and the status page draw on the same report; doc 23 decides what the page shows.

**Configuration and secrets [PROPOSED].** Later layers win: defaults, `appsettings.json` beside the executable, then `BAZAAR__...` environment variables. The data root is set in config (`%ProgramData%\Bazaar` on Windows, a fixed path on Linux). Options use `ValidateOnStart`. Bootstrap values (KEK reference, database connection) are the only values kept in host config. Bootstrap secrets are references only: `env:NAME` and `file:<path>` in `Bazaar.Server`; `dpapi:<path>` and `systemd-creds:<name>` from the providers. Every other secret goes through `ISecretStore` (doc 14). Values never reach logs, config dumps or the status page.

**Startup [PROPOSED].** (1) Validate config. (2) Start logging. (3) Take the instance lock (Postgres advisory lock; a second instance exits). (4) Connect and check the schema version against the build; refuse to start on mismatch in either direction. Migrations run only through the explicit `migrate` command, under the same lock (C44). (5) Probe providers; refuse to start if a required role is unusable. (6) Run the recovery sweep, which calls doc 06. (7) Start hosted services; readiness turns true.

**Shutdown [PROPOSED].** (1) Stop accepting new turns. (2) Cancel running turns; each checkpoints at its next tool boundary (doc 06). (3) Wait up to `host.shutdown_timeout_seconds`. (4) Release the lock. After a hard kill, step 6 on the next start does the cleanup.

**Unattended operation [PROPOSED].** No dialogs or interactive session. Logs are capped. Disk and memory thresholds degrade health; they do not stop the host. Time-based work lives in database rows owned by their area docs (for example doc 09's schedules), picked up by the job poller. Model quota exhaustion is a gateway state (agents queue), not a host failure. Windows Update reboots are scheduled into `host.maintenance_window`, run the shutdown path, and Automatic start brings the host back.

**Health and probes [PROPOSED].** `/health/live` reports that the process is up. `/health/ready` reports that Postgres is reachable, the schema version matches, the instance lock is held and the readiness roles (setting) are usable. The status listener is GET-only, bound per `host.status.bind` (loopback by default), and separate from `/ws` and from doc 32's sign-in routes. Its route, format and contents belong to docs 02 and 23; this doc only supplies the version, uptime and provider data. There is no `/healthz`.

**Logging [PROPOSED].** `ILogger` with a JSON file sink under the data root, daily rotation, capped by `host.log.retention_days` and `host.log.max_total_mb`. No Serilog in v1. Windows Event Log carries warnings and lifecycle events only; the installer creates the source. Linux logs go to journald via stdout.

**Background jobs [PROPOSED].** A `BackgroundService` claims due `job` rows with `FOR UPDATE SKIP LOCKED`, sets a lease and runs the handler. Job leases stay as the inline columns below. Doc 06's `agent_lease` is for agent work claims and is not reused here. Jobs do not touch turn leases, which are doc 06's.

**Settings registry [PROPOSED].** Round 13 makes each open value an operator setting. Every setting is a typed key registered in code with a default, a scope (install, project or agent; this doc's keys are all install scope), a "who may change" role and an approval tier. Values are stored in Postgres (a `setting` table; name and shape PROPOSED, migration owned by doc 04). Each change writes an entry to doc 15's `audit_log`. The setup wizard and advanced settings (docs 27, 32) surface the registry. Settings may tighten a floor, never loosen it: the floors are that the service account is never Administrator, that the status listener is never bound beyond loopback without approval, and that Postgres is always a readiness role. Validation rejects a value that breaks a floor, and a change that needs approval is held until the owner approves it (Tier 2, doc 15).

## Data and interfaces
Sketches only. [PROPOSED] unless noted.

```csharp
public interface IProvider { string Kind { get; } ProviderStatus Probe(); }
public sealed record ProviderStatus(string Kind, bool Usable, IReadOnlyList<string> Features, string? Reason);

public interface IServiceHost : IProvider {        // runtime only; install and recovery live in the admin CLI
    void ConfigureHost(IHostBuilder b);            // AddWindowsService() | UseSystemd()
    Task<ServiceRecoveryReport> RecoveryAsync(CancellationToken ct); // reads back SCM or unit restart policy
}
public interface ICacheProvider : IProvider {      // cache and presence only; never GC fan-out (C07)
    Task SetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan ttl, CancellationToken ct);
    Task<ReadOnlyMemory<byte>?> GetAsync(string key, CancellationToken ct);
}
public interface ISearchProvider : IProvider {
    Task IndexAsync(SearchDocument doc, CancellationToken ct);
    Task<IReadOnlyList<SearchHit>> QueryAsync(SearchQuery q, CancellationToken ct);
}
public interface ISecretResolver {                 // bootstrap only: KEK and database credential (C05)
    bool CanResolve(string reference);
    ValueTask<string> ResolveAsync(string reference, CancellationToken ct);
}
```

The `Vm` role registers a provider that implements doc 16's `IVmBackend`. This doc fixes no VM shape. There is no pub/sub interface here: GC notification is doc 04's NOTIFY.

Doc 06 owns the turn and lease tables. This doc needs only the `job` table:

```sql
CREATE TABLE job (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  kind text NOT NULL, dedupe_key text UNIQUE, run_at timestamptz NOT NULL,
  state text NOT NULL, lease_owner text, lease_until timestamptz,   -- set from DB now()
  attempts int NOT NULL DEFAULT 0, payload jsonb NOT NULL
);
```

### Settings
Registry rows for this doc (DECISIONS-LOG round 13). Keys are dotted and prefixed by area. Changes are audited (doc 15 `audit_log`). "Operator" is the install's operator, set in the setup wizard or advanced settings.

| Key | Type | Default | Scope | Who may change | Change needs approval? |
|---|---|---|---|---|---|
| `host.provider.service_host` | `auto`, `windows_service`, `systemd` | `auto` | install | operator | no |
| `host.provider.vm` | `auto`, `hyperv`, `qemu_kvm`, `none` | `auto` | install | operator | no |
| `host.provider.cache` | `auto`, `garnet`, `valkey` | `auto` (Garnet only after S2) | install | operator | no |
| `host.provider.search` | `auto`, `postgres_fts`, `pg_search` | `auto` | install | operator | no |
| `host.readiness.required_roles` | list of roles | `search`, `service_host` | install | operator | no |
| `host.shutdown_timeout_seconds` | int, 5 to 120 | 20 | install | operator | no |
| `host.service.restart_delays_minutes` | list of int | 1, 1, 5 | install | operator | no |
| `host.service.failure_reset_hours` | int | 24 | install | operator | no |
| `host.maintenance_window` | local time HH:mm | 04:00 | install | operator | no (drain first) |
| `host.db.startup_give_up_minutes` | int; 0 = never exit | 0 | install | operator | no |
| `host.status.bind` | `loopback`, `lan`, `tailscale` | `loopback` | install | operator | Tier 2 for any non-loopback value |
| `host.status.port` | int | chosen by the setup wizard | install | operator | no |
| `host.service_account` | account name; never Administrator | dedicated local account | install | operator | Tier 2 |
| `host.log.retention_days` | int | 14 | install | operator | no |
| `host.log.max_total_mb` | int | 1024 | install | operator | no |
| `host.health.disk_used_warn_percent` | int | 80 | install | operator | no |
| `host.health.memory_growth_warn_percent` | int | 10 | install | operator | no |

## Behaviour and edge cases
- **Recovery sweep.** Startup step 6 calls doc 06's sweep, which marks turns with expired leases `interrupted` and re-queues them. Lease heartbeat and length are doc 06's values. Side-effecting tool calls carry idempotency keys so a replay cannot double-post (doc 06).
- **Job leases.** Claimed with `now()` from the database. An expired job lease becomes claimable again, so handlers must be idempotent [PROPOSED].
- **Postgres down.** At start: capped-backoff retry and readiness false. If `host.db.startup_give_up_minutes` is 0 (default) the host never exits; otherwise it exits after that many minutes and the service manager restarts it. At runtime: turns pause at their next checkpoint.
- **Cache down.** Presence and cache degrade. GC delivery and turns do not depend on the cache [PROPOSED].
- **Two instances.** The advisory lock blocks the second, which exits with one log line. The lock is per database and does not fence two hosts; cutover fencing is doc 33's.
- **Clock skew.** Leases and due times use database `now()`. The host clock affects only logs.
- **Provider fails at runtime.** Reported unusable until restart [PROPOSED].
- **Security.** The status listener is GET-only and loopback by default. Secret errors name the reference, never the value. The service account gets the data root and one Postgres role.

## Testing
- Fake providers per role. Generic-host tests cover startup order, the selection matrix, "explicit unusable fails start" and "schema mismatch refuses start".
- `migrate` applies once under the instance lock; startup against an older or newer schema refuses.
- Settings: every key has a default and a type check; a value that breaks a floor (Administrator account, non-loopback bind without approval, Postgres removed from readiness) is rejected; each change writes an audit row.
- Real Postgres: CI service container on Linux (doc 34 L2); local Windows Postgres on the NUC, pending doc 34's Windows-job decision.
- Kill test: Host as a child process, killed mid-turn. On restart the turn resumes once with no duplicate side effect (doc 06 contract).
- Stop test: `sc stop` or SIGTERM during a long fake turn; checkpoint lands within `host.shutdown_timeout_seconds`.
- Cache test: with no cache provider, presence degrades and GC fan-out still delivers.
- Architecture test: `Bazaar.Server` has no Windows-only reference or `SupportedOSPlatform` attribute.
- Status test: every status route is GET; output never contains chat, Vault text or secret values.
- Windows provider tests on a hosted Windows runner; soak with a compressed simulated clock in CI, and a real 48-hour run on the NUC as a manual step.

**NUC acceptance checklist (first supported host).** PostgreSQL 18 runs as a service with pgvector and survives reboot; the host is installed with Automatic start and read-back recovery actions; the data-root ACL is limited to the service account; after reboot the host is ready with no manual step; a kill mid-turn recovers the turn once; `sc stop` drains within budget; file logs rotate; the status page is reachable on loopback and blocked from LAN; search reports pg_search unavailable without error and uses Postgres FTS; the 48-hour soak runs with no manual step, no turn lost, memory growth under `host.health.memory_growth_warn_percent` and disk under `host.health.disk_used_warn_percent`, with the licence answers (Conflict 2) recorded first.

## Work packages
1. Host skeleton: Worker Service, validated options, logging. [Wave 0]
2. Provider contracts, `ProviderStatus`, selector, capability report, fakes. [Wave 0]
3. Bootstrap secret resolver: env and file schemes. [1]
4. Instance lock, schema-version check, startup sequence and the `migrate` subcommand (runner from doc 04). [1, 2, 04]
5. Recovery hook: startup sweep and shutdown checkpoint wiring, calling doc 06. [4, 06]
6. `job` table and poller. [4]
7. Health probes and GET-only status listener; contents per doc 23. [2, 02, 23]
8. Windows module: Windows Service, recovery read-back, DPAPI bootstrap, Event Log. [2, 3]
9. Linux module: systemd, journald, systemd-creds. [2, 3]
10. Postgres FTS search (OS-neutral). [2, 04]
11. Kill and stop rig; soak harness. [5, 6, 06]
12. Cache provider: Valkey on Linux; Garnet on Windows only after spike S2. [2]
13. `Vm` provider registration against doc 16's contract; S8 sizing. [2, 16]. Unblocked by C03.
14. `pg_search` provider. Blocked on counsel review.
15. Settings registry: typed keys, floors and validation, `setting` table, audit on change. [1, 04, 15]

## Conflicts
1. **Windows host licence (carried).** Whether Windows 11 Pro may run 24/7 as a server, and whether agent VMs need CALs, are unverified. The first supported host cannot be declared until written answers exist. The edition (Server or Windows 11 Pro) is an operator install fact recorded in the install documentation, not a setting; the default is Server pending the answer. Licence terms need counsel.
2. **Platform facts unverified (carried).** Windows recovery actions, SCM stop timeouts and the .NET shutdown default lack primary sources. Restart policy depends on them.
3. **Windows KEK custody (carried, doc 33 C1).** A DPAPI machine-scope KEK conflicts with recovery on new hardware. Windows custody stays open until doc 33 closes it.
4. **Probe and status names (resolved, C40).** This doc uses `/health/live` and `/health/ready`; there is no `/healthz`. Doc 02 owns the `/status` format and doc 23 cites it.
5. **Startup migrations (must fix in 04).** Doc 04 still runs its migration runner at startup. C44 requires the explicit `migrate` command.
6. **Turn state (must fix in 24).** Doc 24 says the runtime "persists the current turn as `interrupted` (03)". Turn state is doc 06's (C04).
7. **CLAUDE.md and deep-dives (partly open).** CLAUDE.md now states two VMs per agent (C03) and the Claude lane on both sources (C14), which agree with the log. Still open: CLAUDE.md's "Decisions in force" line says the cache and pub/sub use Garnet or Valkey, while C07 makes Postgres NOTIFY carry GC fan-out. Bazaar.Server.md §3 now carries the NOTIFY note (resolved).

## Questions for the owner
ANSWERED: VM topology is two VMs per agent; S8 is for sizing only (DECISIONS-LOG round 1, C03).
ANSWERED: GC fan-out is Postgres NOTIFY; Valkey or Garnet serve cache and presence only (DECISIONS-LOG round 1, C07).

1. **Windows KEK store.** Default: DPAPI-protected file. Alternative: Windows Credential Manager. This is key custody, so it stays a question. Blocked on doc 33 C1 (recovery on new hardware).
2. **Admin CLI in v1.** Default: a small non-TUI `bazaar-admin` (status, providers, recovery check). The TUI stays OPEN. This is product scope.

MOVED TO SETTINGS: host.provider.service_host, host.provider.vm, host.provider.cache, host.provider.search, host.readiness.required_roles, host.shutdown_timeout_seconds, host.service.restart_delays_minutes, host.service.failure_reset_hours, host.maintenance_window, host.db.startup_give_up_minutes, host.status.bind, host.status.port, host.service_account, host.log.retention_days, host.log.max_total_mb, host.health.disk_used_warn_percent, host.health.memory_growth_warn_percent (DECISIONS-LOG round 13)
