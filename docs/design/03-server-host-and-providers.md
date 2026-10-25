# Bazaar.Server.Host and OS Providers

Status: DRAFT for owner review (2026-10-25) · Owner questions: 14
Depends on: docs/deep-dives/Bazaar.Server.md, docs/implementation-plan.md (no other design doc exists yet) · Used by: Bazaar.Soul.md (VMs), Bazaar.Constellation.md (search), gateway design (not yet written)

## Purpose
Bazaar.Server.Host is the one executable that runs the company server, as a Worker Service on Windows (first supported host) or Linux. This doc covers how it composes the platform-neutral `Bazaar.Server` library with per-OS providers, how it starts, stops and stays up unattended, and how it reports health. The company must keep working when no client is connected.

## Decided inputs
- One OS-agnostic .NET 10 build; Windows is the first supported and tested host. (CLAUDE.md; Bazaar.Server.md §1)
- Service host, VM backend, cache/pub-sub and search are per-OS providers. No Windows-only API outside a provider. (CLAUDE.md; Bazaar.Server.md §3)
- No web client. A read-only status page is allowed and shows no chat, code or Vault content. Admin panel lives in the apps. (Bazaar.Server.md §1, §10)
- If nobody connects for 48 hours, the company keeps functioning. (Bazaar.Server.md §1)
- GCs are append-only and never purged. (Bazaar.md §2)
- PostgreSQL 18 with pgvector; raw Npgsql 10 with one shared `NpgsqlDataSource`; embedded versioned SQL migrations. (implementation-plan.md)
- Background work: `BackgroundService` plus Postgres tables; Quartz.NET only if cron is needed. A hard kill can skip `StopAsync`. (Bazaar.Server.md §3, RESEARCHED)
- Claude lane uses Anthropic API keys only. (CLAUDE.md)

## Design
**Composition root [PROPOSED].** Only `Bazaar.Server.Host` builds the host. It calls `AddBazaarServer(options)` from `Bazaar.Server`, then registers each provider module whose `Supports(os)` returns true. Modules are an explicit list, not assembly scanning, which keeps Native AOT open.

**Project split [PROPOSED; names need approval].**
- `Bazaar.Server`: contracts, provider interfaces, services, Npgsql repositories, and the OS-neutral Postgres FTS search and NOTIFY bus. No `Microsoft.Win32`, `System.Management` or `[SupportedOSPlatform]`; CA1416 is an error here.
- `Bazaar.Server.Providers.Windows` and `.Linux`: Windows Service, Hyper-V, DPAPI, Garnet if adopted; systemd, QEMU/KVM, Valkey, pg_search. Host references both; the Windows assembly never loads on Linux.

**Provider selection [PROPOSED].** Roles: `ServiceHost`, `Vm`, `Bus`, `Search`. Each config value is `auto` or an explicit kind. `auto` picks the highest-priority candidate whose `Probe()` is usable. An explicit kind that is unusable fails startup; there is no silent fallback. Selection is by capability (extension installed, service reachable); OS filters only OS-bound providers.

**Capability reporting [PROPOSED].** Each provider returns a `ProviderStatus`. Host logs one line per role at startup; the status page and admin CLI show the same report.

**Configuration and secrets [PROPOSED].** Later layers win: defaults, `appsettings.json` beside the executable, then `BAZAAR__...` environment variables. The data root is set in config (`%ProgramData%\Bazaar` on Windows, a fixed path on Linux). Options use `ValidateOnStart`. Secrets are references only: `env:NAME` and `file:<path>` in `Bazaar.Server`; `dpapi:<path>` and `systemd-creds:<name>` from the providers. Values never reach logs, config dumps or the status page.

**Startup [PROPOSED].** (1) Validate config. (2) Start logging. (3) Take the instance lock (Postgres advisory lock; a second instance exits). (4) Connect and migrate under that lock. (5) Probe providers; refuse to start if a required role is unusable. (6) Run the recovery sweep. (7) Start hosted services; readiness turns true.

**Shutdown [PROPOSED].** (1) Stop accepting new turns. (2) Cancel running turns; each checkpoints at its next tool boundary. (3) Wait up to `ShutdownTimeout`. (4) Release the lock. After a hard kill, step 6 on the next start does the cleanup.

**Unattended operation [PROPOSED].** No dialogs or interactive session. Logs are capped. Disk and memory thresholds degrade health; they do not stop the host. Time-based work (shifts, inboxes) lives in database rows. Model quota exhaustion is a gateway state (agents queue), not a host failure. Windows Update reboots run the shutdown path, and Automatic start brings the host back.

**Health and status [PROPOSED].** `/health/live` (process up) and `/health/ready` (Postgres reachable, migrations applied, lock held, required providers usable). The status listener is GET-only and shows version, uptime, health, agents online, queue depth and the provider report. It binds to loopback by default; LAN or Tailscale access is an operator choice. It is separate from any sign-in page.

**Logging [PROPOSED].** `ILogger` with a JSON file sink under the data root (daily rotation, 14-day and 1 GB caps). Windows Event Log carries warnings and lifecycle events only; the installer creates the source. Linux logs go to journald via stdout.

**Background jobs [PROPOSED].** A `BackgroundService` claims due rows with `FOR UPDATE SKIP LOCKED`, sets a lease and runs the handler. Work-Life Simulation schedules are per-agent rows, stored in UTC.

## Data and interfaces
Sketches only. [PROPOSED] unless noted.

```csharp
public interface IProvider { string Kind { get; } ProviderStatus Probe(); }
public sealed record ProviderStatus(string Kind, bool Usable, IReadOnlyList<string> Features, string? Reason);

public interface IServiceHost : IProvider {        // runtime only; install and recovery live in the admin CLI
    void ConfigureHost(IHostBuilder b);            // AddWindowsService() | UseSystemd()
    Task<ServiceRecoveryReport> RecoveryAsync(CancellationToken ct); // reads back SCM or unit restart policy
}
public interface IRealtimeBus : IProvider {        // wake-up hints only; Postgres is the source of truth
    Task PublishAsync(string channel, string gcIdAndSeq, CancellationToken ct);
    Task<IAsyncDisposable> SubscribeAsync(string channel, Func<string, Task> onMessage, CancellationToken ct);
}
public interface ISearchProvider : IProvider {
    Task IndexAsync(SearchDocument doc, CancellationToken ct);
    Task<IReadOnlyList<SearchHit>> QueryAsync(SearchQuery q, CancellationToken ct);
}
public interface IVmBackend : IProvider {          // OPEN: shape depends on topology (Q1)
    Task<VmHandle> StartAsync(VmSpec spec, CancellationToken ct);
    Task StopAsync(VmHandle vm, TimeSpan grace, CancellationToken ct); // graceful, then force
}
public interface ISecretResolver {
    bool CanResolve(string reference);
    ValueTask<string> ResolveAsync(string reference, CancellationToken ct);
}
```

The agent runtime owns these tables (not yet written). This doc needs only the lease columns:

```sql
CREATE TABLE agent_turn (
  id uuid PRIMARY KEY, gc_id uuid NOT NULL, agent_id uuid NOT NULL,
  state text NOT NULL,                         -- queued | running | interrupted | done | failed
  lease_owner text, lease_until timestamptz,   -- set from DB now(), not host clock
  checkpoint jsonb NOT NULL DEFAULT '{}'
);
CREATE TABLE job (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  kind text NOT NULL, dedupe_key text UNIQUE, run_at timestamptz NOT NULL,
  state text NOT NULL, lease_owner text, lease_until timestamptz,
  attempts int NOT NULL DEFAULT 0, payload jsonb NOT NULL
);
```

## Behaviour and edge cases
- **Recovery sweep.** Turns `running` with `lease_until < now()` become `interrupted`; the agent runtime re-queues them. Leases heartbeat every 15 s with a 60 s lease [PROPOSED]. Side-effecting tool calls carry idempotency keys so a replay cannot double-post.
- **Lost notifications.** NOTIFY is a hint. A 30 s poll catches missed rows. Clients resume from `(gcId, seq)`.
- **Postgres down.** At start: capped-backoff retry, readiness false, no exit (Q14). At runtime: turns pause at their next checkpoint.
- **Two instances.** The advisory lock blocks the second, which exits with one log line.
- **Clock skew.** Leases use database `now()`; the host clock affects only logs.
- **Provider fails at runtime.** Reported unusable until restart [PROPOSED].
- **Security.** Status listener is GET-only and loopback by default. Secret errors name the reference, never the value. The service account gets the data root and one Postgres role.

## Testing
- Fake providers per role. Generic-host tests cover startup order, the selection matrix and "explicit unusable fails start".
- Real Postgres: a CI service container on Linux; local Windows Postgres on the NUC.
- Kill test: Host as a child process, killed mid-turn (SIGKILL or TerminateProcess). On restart the turn resumes once with no duplicate side effect.
- Stop test: `sc stop` or SIGTERM during a long fake turn; checkpoint lands within `ShutdownTimeout`.
- Architecture test: `Bazaar.Server` has no Windows-only reference and no `SupportedOSPlatform` attribute.
- Status test: every route is GET; output never contains chat, Vault text or secret values.
- Windows provider tests on the hosted Windows runner (admin for service install). Hosted runners only in the public repo (stack report, decision 10).
- Soak: compressed simulated clock in CI; real 48-hour run on the NUC as a manual step.

**NUC acceptance checklist (first supported host).**
1. PostgreSQL 18 (EDB installer) runs as a service, survives reboot, loads the pgvector build.
2. Host installed with `--contentRoot` and Automatic start; recovery actions set and read back.
3. Data-root ACL limited to the service account.
4. After reboot the host is ready with no manual step.
5. Kill mid-turn: the turn recovers once.
6. `sc stop`: drain completes within budget; time recorded.
7. Event Log source exists; file logs rotate.
8. Status page reachable on loopback, blocked from LAN.
9. Search reports pg_search unavailable without error and uses Postgres FTS.
10. 48-hour soak meets Q12; licence answers (Q5) recorded first.

## Work packages
1. Host skeleton: Worker Service, validated options, logging. [Wave 0]
2. Provider contracts, `ProviderStatus`, selector, capability report, fakes. [Wave 0]
3. Secret resolver: env and file schemes. [1]
4. Instance lock and startup sequence. [1, 2, migrations]
5. Lease columns, recovery sweep, checkpoint API. [4]; coordinate with the agent runtime.
6. Job table and poller. [4]
7. Health and GET-only status listener. [2]
8. Windows module: Windows Service, recovery read-back, DPAPI, Event Log. [2, 3]
9. Linux module: systemd, journald, systemd-creds. [2, 3]
10. Postgres NOTIFY bus and FTS search (OS-neutral). [2]
11. Kill and stop rig; soak harness. [5, 6]
12. Garnet spike, only if Q2 asks. [2]
13. VM backend providers. **Blocked on Q1.**
14. pg_search provider. **Blocked on counsel review.**

## Conflicts
1. **VM topology is decided in one doc and open in others.** `Bazaar.Soul.md` says "decided 2026-10-24: two VMs per agent". CLAUDE.md, implementation-plan (Wave 5) and the stack report (decision 6) keep it OPEN, pending a benchmark. `IVmBackend` cannot be fixed until this is resolved. This doc treats it as OPEN.
2. **Garnet or Postgres NOTIFY for Windows pub/sub.** CLAUDE.md and Bazaar.Server.md §3 name Garnet. The stack report (decision 1) recommends Postgres LISTEN/NOTIFY, with Garnet only if a benchmark demands it. Garnet's StackExchange.Redis compatibility and Windows persistence are unverified.
3. **Windows host licensing.** Whether Windows 11 Pro may run 24/7 as a server, and whether agent VMs need CALs, are unverified (stack report, decision 7). "First supported host" should not be declared before written answers exist.
4. **Platform facts are unverified.** Windows recovery actions (`sc failure`), SCM stop timeouts and the .NET `ShutdownTimeout` default are not confirmed from primary sources. Leases do not depend on them; the restart policy does.
5. **Smaller items.** `Bazaar.Soul.md` lists Cloud Hypervisor for Windows guests, but the research finds no Windows-host path, so it is excluded. Bazaar.Server.md §3 lists Protobuf as both DECIDED and reopened; the APIs here are JSON until the first spike.

## Questions for the owner
1. **VM topology.** OPEN (benchmark both), or the two-VM decision in Bazaar.Soul.md? Default: OPEN, because the Hyper-V backend depends on it.
2. **Windows pub/sub at v1.** NOTIFY only, or Garnet now? Default: NOTIFY only; spike Garnet only if a benchmark needs it.
3. **Provider selection.** Capability probe or fixed OS map? Default: probe, since NOTIFY and FTS work on both OSes.
4. **Status page bind and port.** Default: loopback only, port set at install, LAN or Tailscale opt-in.
5. **Windows host edition.** Server Standard or Windows 11 Pro? Default: Server, pending the licence answer.
6. **Service account.** Default: dedicated local account, not Administrator, with Hyper-V rights by group (exact rights unverified).
7. **Logging.** Default: built-in `ILogger` with JSON files and 14-day and 1 GB caps; no Serilog for v1.
8. **Windows secret store.** Default: DPAPI-protected file. Alternative: Windows Credential Manager.
9. **Stop budget and leases.** Default: 20 s `ShutdownTimeout`, 15 s heartbeat, 60 s lease, checkpoint at every tool call. Measure on the NUC.
10. **Restart policy.** Default: restarts after 1, 1 and 5 minutes; failure counter resets after 24 hours.
11. **Windows Update reboot window.** Default: 04:00 local, after a drain. Confirm your timezone.
12. **48-hour soak criteria.** Default: no manual step, no turn lost, memory growth under 10%, disk under 80% used.
13. **Admin CLI in v1.** Default: a small non-TUI `bazaar-admin` (status, providers, recovery check). The TUI stays OPEN.
14. **Postgres down at boot.** Default: capped-backoff retry, never exit. Alternative: exit after N minutes and let the service manager restart.
