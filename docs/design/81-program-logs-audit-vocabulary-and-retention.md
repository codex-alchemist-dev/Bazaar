# 81. Program logs, audit vocabulary and retention (Annales and Tabula additions from Resibo)

Status: PROPOSED. Nothing in this document is built. The owner's direction of 2026-11-19 is DECIDED and is the only decided input: adopt every Resibo feature that benefits Bazaar, as separate modules where it conflicts; Annales (program logs) and Tabula (audit log) are shared contract-and-logic libraries with providers staying in Bazaar; `Principal` replaces AgentId-style actors; the Tabula contract carries three optional extras (retention window, registered operation vocabulary, reason-tagged access-grant event type; owner card, 2026-11-19, `CONVERGENCE.md` row A3). Every design detail below those inputs is PROPOSED or OPEN.
Depends on: 03-server-host-and-providers.md (settings registry, provider seam), 04-postgres-schema-and-migrations.md (per-component migration namespaces, roles), 14-identity-service-and-passport.md (secret handles, audit signing key), 15-security-taint-and-approvals.md (taint, audit paragraph, tiers), 23-observatory-and-usage.md (OpenTelemetry pipeline), 43-telemetry-and-crash-reporting.md (ring buffer, redaction), 46-kodo-health-module.md (module health contract, breakers), 48-gap-decisions-security-identity.md (audit chain, checkpoints, retention, roles, biometrics), BAZAAR-COMPONENTS.md (sections 2 to 6, 4.2 Principal).
Used by: 46 (Kodo reads log and audit health), 43 (server-side re-check events use registered operations), 72 (access grants, same batch: the access-grant event contract here is its audit surface), 76 (Historian narrative track, same batch: referenced only), 27 to 30 (client log pane), 34 (test strategy: the hygiene rules), every module that writes an audit row or catches an exception.

## 1. Purpose

Bazaar already has a tamper-evident audit chain (doc 48 section 5, built in part), a client diagnostics library (doc 43), and Kodo for health (doc 46). Resibo, a sister program, solved four problems the Bazaar docs leave open, and found one expensive bug. This document adopts each as a module and states exactly where it reuses what already exists.

1. **Program logs.** Bazaar has OpenTelemetry `ILogger` export (doc 23) and a client-side crash ring buffer (doc 43), but no durable, rotated, per-scope server log that an operator can read after a crash, tail live, tune per module at runtime, and keep apart from the audit chain. Resibo's Logs API gives that (section 6).
2. **Retention window for the audit log.** Doc 48 section 9 says raw audit read records "have their own retention period, a setting" and the in-flight sweeper (#814, another thread) needs a policy to apply. Resibo's audit retention gives the policy shape. Its mechanism (a SQL `DELETE`) is not usable on a hash chain, so only the policy is taken (section 7).
3. **Registered operation vocabulary.** Today any module can append any `kind` string. Resibo's closed `ActionType` vocabulary caught a real gap (Supervisor operations were not registered, so calls returned `UNKNOWN_ACTION`). Bazaar is modular, so a closed enum is wrong; each module registers its own operations at startup instead (section 8).
4. **Reason-tagged access-grant event** for break-glass style access (doc 72 owns the mechanism; this doc owns the audit contract) (section 9).
5. **A CI rule against silent catches and unreachable log or audit paths.** Resibo found a log writer with zero callers, so every best-effort `except: pass` in the program wrote to nothing. Bazaar needs the same guard, applied to catch blocks, to registered audit operations and to the log provider (section 10).

The fifth item is the point of the whole document. Features 1 to 4 are only worth building if the rule in 5 keeps them wired.

## 2. Decided inputs

- Annales is the program-logs shared library and Tabula is the audit-log shared library. Both hold contracts and logic only, no third-party dependencies, as Core-level modules. Providers (file sink, live tail, Postgres storage) stay in Bazaar as separate modules (owner direction, 2026-11-19; `CONVERGENCE.md` rows 6 and 7).
- Actor attribution is the single `Principal` type in `Bazaar.Core`: kind `agent`, `user`, `service` or `system`, plus an id, canonical text `kind:id`. The audit log keeps its single `actor` text column and its hash chain, now written as canonical `kind:id`, with no migration. Existing bare values are read through a mapping (owner, 2026-11-19; DECISIONS-LOG; BAZAAR-COMPONENTS 4.2).
- The Tabula contract carries three optional extras: a configurable retention window, a registered operation vocabulary, and a reason-tagged access-grant event type (owner card, 2026-11-19).
- Every feature is its own `Bazaar.Server.<Name>` module plus `.Abstractions`, plus `Bazaar.Client.<Name>` where there is a client side. No feature code goes in `Bazaar.Server` or `Bazaar.Client.Core`. Providers are plain modules on Core with no selector (owner, 2026-11-19).
- Where Resibo and Bazaar conflict, both stay as separate modules rather than one being chosen (owner, 2026-11-19). Section 12 lists each conflict and how it is separated.
- The audit chain, signed checkpoints, anchoring and role-scoped views are DECIDED in doc 48 section 5 and are not reopened here. Retention is DECIDED in principle (doc 48 section 9: per-category periods, soft delete first, legal hold blocks removal, crypto-erase with signed proof); its periods are settings.
- Settings may tighten, never loosen below a floor. Legal and privacy settings default to the most protective option with a "needs counsel" note (DECISIONS-LOG round 13; doc 48 principles).

## 3. What this document does not decide

- The audit chain format, checkpoint, anchoring and verifier (doc 48 section 5.2 to 5.4). This doc adds registration and policy around them, not a new chain.
- The physical retention mechanism for audit rows. The in-flight retention sweeper (#814) owns it. This doc gives the policy contract and states the constraints a mechanism must meet (section 7.3).
- The access-grant mechanism itself (who may grant, durations, notification): doc 72.
- The Historian narrative track (doc 76). Referenced only (section 5).
- Crash reports and the client capture pipeline (doc 43). Reused, not changed (section 6.9).
- The log viewer's layout and mnemonics (doc 41 owns the pane registry). Section 6.8 names panes and marks mnemonics PROPOSED.

## 4. Module map

| Module id | Projects | Kind | What it is |
|---|---|---|---|
| `program-logs` | `Bazaar.Server.ProgramLogs`, `Bazaar.Server.ProgramLogs.Abstractions` | feature | Annales-side logic: `ILogger` provider, scopes, tiers, rotation rules, retention planner, redaction hook, tail fan-out, health. No file, wire or third-party code. |
| `program-logs-file` | `Bazaar.Server.ProgramLogs.FileSink` | provider | Day-rotated JSONL files, sidecar index, never-committed guard. BCL only. |
| `program-logs-tail` | `Bazaar.Server.ProgramLogs.LiveTail` | provider | In-memory ring plus the `logs.tail` wire stream. |
| `program-logs-testing` | `Bazaar.Server.ProgramLogs.Testing` | testing | In-memory sink and assertions for module tests. |
| `client-program-logs` | `Bazaar.Client.ProgramLogs` | client-pane | Log viewer pane and live tail view. |
| `audit-vocabulary` | `Bazaar.Server.AuditVocabulary`, `.Abstractions`, `.Testing` | feature | Operation registry, vocabulary-checking `IAuditLog` decorator, Principal overloads, legacy actor mapper, conformance test base. |
| `audit-retention` | `Bazaar.Server.AuditRetention`, `.Abstractions` | feature | Retention policy contract and class model (Tabula side). The sweeper (#814) consumes it. |
| `access-grant-audit` | `Bazaar.Server.AccessGrantAudit`, `.Abstractions` | feature | Typed, reason-tagged access-grant audit recorder (Tabula side). |
| hygiene rules | `tests/Bazaar.Architecture.Tests` (new files) | tests | The silent-catch, operation-caller and log-reachability rules with seeded-violation tests. Not a module. |

Dependencies (hard unless noted): `program-logs` has none beyond the kernel; it soft-depends on `settings`, `kodo`. `program-logs-file` and `program-logs-tail` depend on `program-logs`. `audit-vocabulary` depends on `audit`. `audit-retention` depends on `audit-vocabulary` and softly on `settings`. `access-grant-audit` depends on `audit-vocabulary`. All feature modules keep their SQL in their own namespace (section 11).

## 5. The Resibo four-way split, mapped to Bazaar

Resibo keeps four separate records: Logs (operational trace), Historian data-change, Historian narrative, and Audit (privileged actions) (`v3-deepdive-08` section 1, `v3-deepdive-18` section 1, `v3-deepdive-29`). The distinguishing question for audit is "would a security or compliance review need to see this specifically". Bazaar already matches this:

| Resibo | Bazaar home | Status |
|---|---|---|
| Logs | Annales: `program-logs` (this doc) | new |
| Historian data-change | Append-only versioned tables (GC versions doc 01, Vault git doc 18, Kanban); reads and writes recorded by id in audit (doc 48 section 5.1) | exists, no new module |
| Historian narrative (quantized, user-visible story of a run) | doc 76 | referenced only |
| Audit | Tabula: `audit` plus the three extras here | exists, extended |

Rule carried over unchanged: a thing is recorded in exactly one of logs or audit. An OCR timing-style fact (a turn took 4 s) is a log; a grant of access is audit. A module that needs both writes both, but never copies audit payload fields into a log line or the reverse.

## 6. Annales side: `Bazaar.Server.ProgramLogs`

### 6.1 What is reused, exactly

| Existing piece | Reuse |
|---|---|
| `Microsoft.Extensions.Logging` (`ILogger<T>`) | The only authoring API. No new call-site API. Modules keep calling `ILogger`. |
| OpenTelemetry logs export (doc 23 section on OTel in `Bazaar.Server.Host`) | Unchanged. The program-logs provider is added beside it, so OTel still sees every entry. No OTLP sink is built; a duplicate exporter would be an overlap. |
| `Bazaar.Diagnostics.Capture.RingBuffer<T>` (doc 43) | The live-tail replay ring and the queue overflow counter use this class (`DroppedCount`). Not copied. |
| `Bazaar.Diagnostics.Redaction` rules and `RedactionRules` (doc 43 section 6) | The secret-shaped pattern layer is reused for the write-path redaction in 6.6. The report-specific parts (class `Never`, 4096-character cap, residual-scan hold) are not used. |
| Kodo breakers and health contract (doc 46 sections 7.4, 10) | Sink failure isolation follows the Kodo breaker settings. Health uses `IModuleHealthContract`. |
| `Bazaar.Server.Modules.Abstractions.Health.IModuleHealthContract` | Implemented as is. |

New: structured scopes (module id, `Principal`, correlation ids), the `Attention` level, mandatory exception capture, day rotation by scope, the sidecar index, per-module runtime tiers, verbosity leases, the tail stream, retention planner, the never-committed guard.

### 6.2 Scopes and layout

Entries are written to one or more scopes. A scope is a directory under the log root; the same entry may be written to several.

```
<logs.root>/
  global/2026-11-19.jsonl              all entries at or above the global tier
  global/2026-11-19.trace.jsonl        Trace-tier and content-bearing entries (short retention)
  module/<module-id>/2026-11-19.jsonl  one module's entries
  principal/<kind>/<id>/2026-11-19.jsonl   one Principal's entries (where lawful, 6.7)
  _emergency/2026-11-19.jsonl          fallback when the normal write path is down (6.5)
  .gitignore                           contains "*" (6.4)
```

The day boundary is UTC. A file is append-only; nothing is rewritten, and rotation is a new file name, as in Resibo (`v3-deepdive-18` section 3.1). The `.trace.jsonl` sibling is taken from Resibo (`core/logs/CLAUDE.md`): Trace and content-bearing entries live in their own file so the shorter retention is a plain file delete, never an in-place rewrite. Module and principal ids become directory names only after a strict slug filter (`[A-Za-z0-9_.-]`, leading dots stripped; PROPOSED, Bazaar's design, not from Resibo), so an id can never write outside the root.

### 6.3 Contracts

```csharp
// Bazaar.Server.ProgramLogs.Abstractions
public enum ProgramLogLevel { Trace, Debug, Information, Warning, Error, Critical, Attention }

public sealed record ProgramLogEntry(
    DateTimeOffset Ts,                      // UTC, microsecond precision
    ProgramLogLevel Level,
    string ModuleId,                        // manifest id
    Principal? Actor,                       // who the work was for or by, when known
    string? CorrelationId,                  // turn id, request id, run id
    string? TraceId,                        // OpenTelemetry trace id, when present
    string Template,                        // message template, never interpolated user text
    string Message,                         // rendered, after write-path redaction
    IReadOnlyDictionary<string, string> Properties,
    ProgramLogException? Exception,         // full chain, always when an exception exists
    string? Taint,                          // none | soft | hard (doc 15), null when unknown
    bool ContentBearing);                   // prompt, response or record text; routed to the trace file

public sealed record ProgramLogException(
    string Type, string Message, string StackTrace, string FullText, IReadOnlyList<ProgramLogException> Inner);

public interface IProgramLogSink
{
    string Name { get; }
    ValueTask<SinkReceipt> EmitAsync(ProgramLogEntry entry, CancellationToken ct);
}

public interface IProgramLogReader      // implemented by the file sink provider
{
    IAsyncEnumerable<ProgramLogEntry> QueryAsync(ProgramLogQuery query, CancellationToken ct);
}

public interface IProgramLogVerbosity
{
    ProgramLogLevel LevelFor(string moduleId);
    Task<IDisposable> LeaseAsync(string moduleId, ProgramLogLevel level, TimeSpan duration, Principal requester, string reasonLabel, CancellationToken ct);
}

public static class ProgramLogLevelExtensions
{
    public static string StyleHint(this ProgramLogLevel level);   // "dim" ... "critical_persistent"
    public static LogLevel ToLogLevel(this ProgramLogLevel level); // Attention maps to Error for ILogger consumers that lack it
}
```

`Attention` is taken from Resibo (`v3-deepdive-18` section 3.4): "this process is running below its own achievable baseline", distinct from an operation that failed. In Bazaar its first writers are Kodo (capability drift, a probe fleet degraded, a hash-chain anchor gap) and the hygiene self-checks (6.10). It is exposed through `LogAttention(this ILogger, ...)`, an extension that logs at `Error` to plain providers with an `attention=true` property, so OpenTelemetry and the console still carry it. `StyleHint` is Resibo's `LEVEL_STYLE_HINT` (`v3-deepdive-18` section 3.1): one semantic token per level (`dim`, `subdued`, `default`, `caution`, `critical`, `critical_persistent`), so the client maps it onto the theme (doc 41) and no client keeps its own severity opinion. The `Critical` level is added because .NET has it and Resibo does not.

### 6.4 Never committed: the guarantee

Resibo's rule is that the log root is never inside a repository or release clone (`docs/PRINCIPLES.md` 1.6), because V2 committed data by defaulting paths inside the program directory. Bazaar needs it more: the Vault is a git repository, agent workspaces are git worktrees, and the platform repo is public. The guarantee has four layers, each independently testable:

1. **Default root** is under the install data directory, never the working directory or a repository path.
2. **Start-up guard.** The file sink walks the parents of the root; if it finds a `.git` directory or file, or the path is inside the Vault root or any registered workspace root, the sink refuses to write there, falls back to `<data>/logs`, raises an `Attention` entry and writes the audit operation `logs.root.unsafe_location`. It does not silently continue.
3. **Self-ignoring directory.** The sink writes `<root>/.gitignore` containing `*` and an `.ignore` marker on first use, so a root that is later moved under a repository still excludes itself.
4. **Repository gate.** An architecture test (section 10) asserts the repo's `.gitignore` covers `logs/`, `*.jsonl` log patterns and the default data directory, that no tracked file under `src/` or `docs/` is a `*.jsonl` log, and that `Bazaar.Server.ProgramLogs.FileSink` contains the guard. Agent VM mounts and the whole-company export (doc 44) exclude the log root by default (setting `logs.include_in_export`, section 6.11).

### 6.5 Write path: never fail the thing being logged

The write path follows Resibo's rule "logging must never be able to fail the thing being logged" (`core/logs/CLAUDE.md`).

- `ILogger` calls enqueue to a bounded channel and return. A background writer drains it and fans each entry to every enabled `IProgramLogSink`. All enabled sinks receive every accepted entry (Resibo's Provider Registry property, `v3-deepdive-18`); a failing sink is degraded on its own behind a Kodo-style breaker and never stops the others.
- Under queue pressure, Information and below are dropped first and counted (`RingBuffer<T>.DroppedCount` semantics). Entries at Warning and above, and every entry with an exception, use a reserved portion of the queue (`logs.queue.reserved_for_errors`). If that is also full, the entry is written synchronously to `_emergency/` and to standard error. An exception is never dropped for volume.
- A failure inside the logger itself is written to standard error and a counter only. It never throws into the caller and never calls `ILogger` recursively. This is the one place a broad catch is allowed without logging, and it carries the explicit marker described in section 10.3.
- Flushing: the file sink flushes each Warning-and-above entry immediately (the entries just before a crash matter most; PROPOSED, Bazaar's design, not from Resibo), and others on a short timer.

### 6.6 Full tracebacks, always, locally

Resibo's hard requirement is that a failure's full traceback is captured unconditionally and verbosity tiers control only routine volume (`v3-deepdive-18` section 3.3; `core/logs/CLAUDE.md`, where the traceback branch runs before the tier check). Resibo got this wrong in V2 (`str(e)`) and fixed it. The Bazaar rendering is:

- Any entry with a non-null `Exception` is written at every tier, to the module scope and the global scope. `ProgramLogException.FullText` is `Exception.ToString()` of the whole chain (inner exceptions, aggregate children, async stack frames), never only `Message`.
- The extension `LogFailure(this ILogger, Exception, string template, ...)` is the documented way to log a caught exception. The silent-catch rule (10.2) accepts it.
- **Redaction on the write path.** Local logs keep the full traceback text, but secrets must not reach disk. The write path runs the secret-shaped pattern layer of `Bazaar.Diagnostics` over `Message`, `Properties` values and `FullText`, with no length cap and no truncation (the 4096-character cap in `Redactor` is a crash-report rule and would cut tracebacks). The pattern list may be extended by `logs.redaction.extra_patterns`, never reduced. If the redactor throws, the entry is written with the failing field replaced by `<redacted:failed>`, never dropped and never written raw.
- Personal content is not redacted locally but is kept out of the default tiers: prompt and response bodies, GC text, Vault text and mail bodies are `ContentBearing` and go only to the `.trace.jsonl` file at Trace tier. Redaction at egress (live tail to a non-owner, an exported bundle, a crash report) is the doc 43 pipeline, unchanged.
- An exact-value layer (known secret values, as in doc 43 section 6.2 layer 3) needs the logger to hold secret material. That is OPEN (section 19, question 4).

### 6.7 Verbosity tiers at runtime, and per-Principal scopes

- **Tiers per module.** `logs.verbosity.default` and one `logs.verbosity.module.<module-id>` row per module (a dynamic key family; unset means inherit). Changes take effect without a restart. They go through the settings registry, so the existing setting audit rows (Principal of the changer, old and new value) already record them. No new audit operation is added for a plain change.
- **Verbosity leases** (additional, not in Resibo). Raising a module to Debug or Trace is the common debugging act and the common mistake to leave on. `IProgramLogVerbosity.LeaseAsync` raises a module for a bounded time and reverts automatically. The maximum lease is `logs.verbosity.lease_max_minutes`. Leases survive a restart (table `program_log_verbosity_lease`, 11.1). A raise to Trace for a module whose entries are content-bearing needs the owner (it widens what reaches disk), through the normal approval path in doc 15.
- Floors: Error, Critical, Attention and any entry with an exception are never filtered by tier. No setting lowers `Warning` below being written to the global scope.
- **Per-Principal scope, where lawful.** For `agent`, `service` and `system` Principals a per-Principal directory is on by default. For `user` Principals it is off and `logs.scope.principal.enabled_kinds` may add `user` only as an operator choice (a log keyed to a person is personal data, doc 48 section 12; needs counsel). Because log files are not hash-chained, erasure of a person's log directory is a plain delete, and the erasure proof (doc 48 section 9) lists it. This is the reason logs and audit stay apart.
- **Read access.** Reading logs follows doc 48 section 4: the capability `logs.read` with a scope (global, module, Principal). Defaults: the owner reads all; a security reviewer reads module and global scopes; agents read nothing (the same default as the audit view); a human reads their own Principal scope when it exists. Each tail subscription or query is audited as `logs.tail.subscribed` (the request, not the lines). This replaces Resibo's `check_access()` break-glass gate with Bazaar's roles; a cross-scope read by someone without the capability is an access-grant event (section 9).

### 6.8 Live tail over the wire, and the client

The stream `logs.tail` registers in the doc 02 `/ws` stream registry. Its contract types live in `Bazaar.Contracts`, namespace `ProgramLogs`.

```csharp
public sealed record LogTailRequest(
    string Scope,                  // "global" | "module:<id>" | "principal:<kind>:<id>"
    ProgramLogLevel MinLevel,
    long? ResumeAfter,             // cursor from a previous frame, null for "latest N"
    int ReplayEntries);

public abstract record LogTailFrame;
public sealed record LogTailEntry(long Cursor, ProgramLogEntry Entry) : LogTailFrame;
public sealed record LogTailGap(long FromCursor, long ToCursor, long Dropped) : LogTailFrame; // subscriber too slow
public sealed record LogTailHeartbeat(DateTimeOffset Ts) : LogTailFrame;
```

Rules: each subscriber has a bounded channel; when it is slow the oldest frames are dropped and one `LogTailGap` says how many (never silent loss). Replay comes from the in-memory ring first (`logs.tail.ring_entries`), then the files through the sidecar index when the cursor is older. Content-bearing entries are never sent to a client unless it holds `logs.read` for a scope that includes the trace file and has asked for it. The server needs no connected client for any of this to work; a tail subscription is a pure reader, matching Resibo's statement that Logs works whether or not an Interface exists (`v3-deepdive-18` section 1).

`Bazaar.Client.ProgramLogs` is the client module. It contributes a Logs pane (list, filter by module and level, follow mode, full-traceback expand, copy) through the pane registry (doc 41). It uses `StyleHint` tokens mapped to theme colours and shows `Attention` as persistent, harder to miss than Error. Mnemonic: PROPOSED `lg` for open pane, left to doc 41's table. Headless tests use `Avalonia.Headless`. The client depends only on `Bazaar.Contracts` types and the module's Abstractions.

### 6.9 Relation to doc 43 and Kodo (no duplication)

- Doc 43's ring buffer is a client-side capture (200 entries at Information and above, `telemetry.crash.log_lines`) feeding crash reports after redaction. It stays. The server's tail ring is a different buffer for a different purpose, using the same class.
- Doc 43 section 13 says server failures are not reported through the crash pipeline; Kodo owns server incidents. Program logs are Kodo's evidence store, not a second health system. Kodo's journal (doc 46, hash chain of health state) is not replaced; Kodo reads log counters through the module health contract and writes `Attention` entries, nothing more.
- OpenTelemetry keeps shipping `ILogger` data off the host (doc 23). Program logs are the local, durable, operator-readable copy.

### 6.10 Retention of logs

Taken from Resibo (`v3-deepdive-18` section 5 and 10; `core/logs/CLAUDE.md`), adjusted:

- Two windows: `logs.retention.days` (default 90) for ordinary files and `logs.retention.trace_days` (default 7) for `.trace.jsonl`, both whole-file deletes. The planner is a pure function (`IProgramLogRetentionPlanner.Expired(now)`), so "what would this sweep remove" can be inspected without trusting a dry-run flag (PROPOSED, Bazaar's design).
- The sweep never touches today's or the open day's file. It runs on the Bazaar scheduler, not in the write path.
- A file that cannot be deleted is reported and skipped, never retried in a loop.
- **The audit chain is unaffected.** Logs are not audit rows and are not hash-chained. Purging logs writes one summary audit row per sweep only when files were removed: `logs.purge.executed` (counts and horizon only). No log content ever enters audit.
- A legal hold (doc 48 section 9) on a Principal, GC or company blocks purge of the log files in that scope. A held file is skipped and counted. This is a floor, not a setting.
- Disk pressure: when free space falls below `logs.disk.min_free_mb`, Information and below stop being written, an `Attention` entry is raised and Kodo goes amber. Error-class entries keep writing. A size cap (`logs.disk.max_total_mb`, default 0 = off) may purge oldest non-held days early; doing so is itself logged.

### 6.11 Settings rows (PROPOSED defaults; registry owned by doc 03)

| Key | Type | Default | Scope | Floor and notes |
|---|---|---|---|---|
| `logs.root` | path | `<data>/logs` | install | Start-up guard (6.4) rejects repository, Vault and workspace paths. Restart. |
| `logs.verbosity.default` | enum | `Information` | install | Not below `Warning` for the global scope. |
| `logs.verbosity.module.<id>` | enum | inherit | module | Raise to Trace on a content-bearing module needs owner approval. |
| `logs.verbosity.lease_max_minutes` | int | 120 | install | Range 5 to 1440. |
| `logs.scope.module.enabled` | bool | true | install | |
| `logs.scope.principal.enabled_kinds` | list | `agent, service, system` | install | Adding `user`: operator choice, needs counsel. |
| `logs.retention.days` | int | 90 | install | At least 7. |
| `logs.retention.trace_days` | int | 7 | install | At least 1. |
| `logs.queue.capacity` | int | 8192 | install | At least 256. |
| `logs.queue.reserved_for_errors` | int | 1024 | install | At least 64. |
| `logs.tail.enabled` | bool | true | install | |
| `logs.tail.ring_entries` | int | 2000 | install | |
| `logs.tail.max_subscribers` | int | 16 | install | |
| `logs.disk.min_free_mb` | int | 1024 | install | At least 256. |
| `logs.disk.max_total_mb` | int | 0 | install | 0 = no size cap. |
| `logs.redaction.extra_patterns` | list | empty | install | Tightening only; never removes a built-in. |
| `logs.include_in_export` | bool | false | install | Operator choice; needs counsel (logs may carry personal data). |

Each row carries a `docs_ref` string naming this doc and, for retention, the legal basis the operator is choosing against, taken from Resibo's habit of citing the regulation in the settings entry itself (`v3-deepdive-08` section 5). Bazaar ships no jurisdiction citation; the field is empty unless an operator profile (doc 32) fills it.

## 7. Tabula side: `Bazaar.Server.AuditRetention`

### 7.1 What is taken, what is not

Taken from Resibo (`core/audit/CLAUDE.md`, `v3-deepdive-08` section 5): a retention policy with a fixed or indefinite mode; a rule that retention is never silently shortened (a shortening needs an explicit override with a stated reason that is itself audited); purge records are exempt from purging; the cutoff is a pure function of policy and time (`horizon`); a purge takes a policy and an actor, never a row id; and retention is the single deletion path, isolated from the append writer.

Not taken: the 10-year default (BIR RR 17-2013 is a Philippine accounting rule and is DOMAIN in `CONVERGENCE.md`; Bazaar has no such obligation, so the default is `indefinite`); the `DELETE` statement (the Bazaar audit table refuses UPDATE, DELETE and TRUNCATE for every role and by trigger, `common/0004_audit_log.sql`, and rows are hash-chained); the SQLite authorizer (Postgres roles and triggers already provide it).

### 7.2 Contract and classes

Retention applies per **audit class**, not per table, because only some rows may ever be pruned.

```csharp
// Bazaar.Server.AuditRetention.Abstractions
public enum AuditClass { Security, Admin, DataWrite, DataRead, System }
public enum RetentionMode { Indefinite, Fixed }

public sealed record AuditRetentionRule(AuditClass Class, RetentionMode Mode, int? Days);

public interface IAuditRetentionPolicy
{
    AuditRetentionRule RuleFor(AuditClass auditClass);
    /// Rows of this class older than the returned instant may be pruned, or null when the class is never pruned.
    DateTimeOffset? Horizon(AuditClass auditClass, DateTimeOffset now);
    /// Whether a legal hold (doc 48 section 9) covers this subject, scope or category.
    ValueTask<bool> IsHeldAsync(AuditRetentionSubject subject, CancellationToken ct);
}

public sealed record AuditRetentionRunRecord(
    Guid RunId, DateTimeOffset At, Principal Actor, string Mechanism,
    long RowsAffected, DateTimeOffset Horizon, long FirstSeq, long LastSeq, bool ShortenOverride);
```

Defaults and floors (PROPOSED):

- `Security` and `Admin` (sign-in, role, grant, approval, approval key, module install, setting change, erasure proof, legal hold, recovery, retention and vocabulary events themselves) are never pruned. This is a floor, not a setting.
- `DataRead` and `DataWrite` (the per-object read and write records of doc 48 section 5.1) are the only prunable classes. Default `Indefinite` for both. When an operator picks `Fixed`, floors of 365 days (`DataRead`) and 1825 days (`DataWrite`) apply. These numbers are PROPOSED drafts for the spike, not owner decisions.
- Purge run records (`audit.retention.executed`) are exempt (Bazaar's rule, PROPOSED; Resibo's `core/audit/CLAUDE.md` describes its purge as the one deletion path).
- A retention change, and especially a shortening, is a high-risk action: owner approval with the signed approval flow and the biometric check when it ships (doc 48 sections 8 and 9). A shortening carries a `reason_label` (a slug, as `PrivacyAuditRecorder` requires) and writes `audit.retention.shortened`.

### 7.3 Constraints on the mechanism (the sweeper is another thread's)

The retention sweeper (#814) owns the physical step. Whatever it does must meet these constraints, which follow from doc 48 and the existing code:

1. The chain must still verify from the nearest remaining signed checkpoint (doc 48 section 5.2: a verifier "recomputes the chain from any starting checkpoint").
2. `audit_log` privileges and triggers are not weakened for the runtime role. A separate, narrow retention path (its own role, as Resibo isolates its purge behind its own authorizer profile, `core/audit/CLAUDE.md`) is acceptable; a new `UPDATE` or `DELETE` grant to `bazaar_app` is not.
3. A held scope is never touched.
4. Two mechanisms satisfy 1 to 3 and the choice is OPEN (question 1): **(A) crypto-shred.** Payloads of `DataRead` and `DataWrite` rows are written encrypted under a per-epoch key (doc 48 section 5.5: the row hash covers fields after encryption, so destroying a key does not break the chain). Retention destroys the epoch key and writes a signed proof, as erasure does (section 9 there). No row is deleted. **(B) Segment prune.** Whole checkpoint segments that are anchored, older than the horizon and entirely made of prunable classes are detached; the last remaining checkpoint becomes the verifier's start. Mixed segments cannot be pruned under B, which is why A is preferred.
5. Every run appends one `audit.retention.executed` row after the effect, and a failure to write it is surfaced, not swallowed (record after the delete so a record never claims a purge that did not happen; PROPOSED, Bazaar's design, not from Resibo).

### 7.4 Retention data

SQL sketch, namespace `audit-retention/0001_retention_run.sql` (append-only, same grants as `audit_log`):

```sql
CREATE TABLE audit_retention_run (
    run_id        uuid        PRIMARY KEY,
    at            timestamptz NOT NULL,
    actor         text        NOT NULL,          -- Principal text form, kind:id
    mechanism     text        NOT NULL CHECK (mechanism IN ('crypto_shred','segment_prune')),
    audit_class   text        NOT NULL,
    horizon       timestamptz NOT NULL,
    first_seq     bigint      NOT NULL,
    last_seq      bigint      NOT NULL,
    rows_affected bigint      NOT NULL CHECK (rows_affected >= 0),
    shorten_override boolean  NOT NULL DEFAULT false,
    reason_label  text,
    CHECK (last_seq >= first_seq)
);
REVOKE ALL ON audit_retention_run FROM PUBLIC;
GRANT INSERT, SELECT ON audit_retention_run TO bazaar_app;
```

### 7.5 Settings rows (PROPOSED)

| Key | Type | Default | Floor and notes |
|---|---|---|---|
| `audit.retention.data_read.mode` | enum `indefinite`,`fixed` | `indefinite` | Change needs owner approval; blocked by legal hold. |
| `audit.retention.data_read.days` | int | 1095 (used only when `fixed`) | At least 365. |
| `audit.retention.data_write.mode` | enum | `indefinite` | As above. |
| `audit.retention.data_write.days` | int | 3650 (used only when `fixed`) | At least 1825. |
| `audit.retention.sweep_interval_hours` | int | 24 | At least 1. |

## 8. Tabula side: `Bazaar.Server.AuditVocabulary`

### 8.1 The operation registry

Every audit kind a module can write is declared by that module at startup. This replaces Resibo's single closed `ActionType` enum (a conflict with BAZAAR-COMPONENTS: a central enum would make every module depend on one project) while keeping its benefit: a call with an unknown operation is detected, not silently accepted.

```csharp
// Bazaar.Server.AuditVocabulary.Abstractions
public sealed record AuditOperation(
    string Kind,                      // "module.noun.verb", ^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*){1,3}$
    string ModuleId,                  // the registering module
    AuditClass Class,                 // Security | Admin | DataWrite | DataRead | System
    IReadOnlyList<string> RequiredPayloadFields,
    bool Privileged,                  // a review must see it; the coverage rule requires a caller and a test
    bool RetentionExempt,             // never pruned whatever the class
    OperationStatus Status,           // Active | Planned
    string? PlannedUntilVersion,      // required when Status = Planned
    string DocRef);                   // "doc 32 section 8"

public interface IAuditOperationSource      // each module registers one or more
{
    string ModuleId { get; }
    IEnumerable<AuditOperation> Operations { get; }
}

public interface IAuditOperationRegistry
{
    IReadOnlyList<AuditOperation> All { get; }
    bool TryGet(string kind, out AuditOperation operation);
    string RegistryHash { get; }       // SHA-256 over the sorted descriptors
}
```

Lifecycle, fitting BAZAAR-COMPONENTS section 3: during **Register** each module adds its `IAuditOperationSource`; the registry is built and frozen at **Start**, before any hosted service writes. A duplicate `Kind` registered by two modules fails startup with both module ids named, the same failure style as the component graph. A module may only register kinds beginning with its own id or an id prefix declared in its manifest, so a module cannot claim another's namespace. At start one row `audit.vocabulary.snapshot` records the registry hash and count, so the audit log proves which vocabulary was in force.

### 8.2 Enforcement: reject or flag

`audit.vocabulary.mode` has two values, `flag` (default) and `reject`.

- **`flag`:** an unregistered kind is still appended (an audit log that drops an event because of a bookkeeping error is worse than one that records it as unknown). The decorator adds `"vocab":"unregistered"` to the payload, increments a counter that Kodo reads, and writes one `audit.vocabulary.unregistered_kind` row per distinct kind per start, so the same unknown kind does not flood the chain.
- **`reject`:** the append throws `UnregisteredAuditOperationException` and nothing is written. Tests and the CI hygiene run use `reject`; production operators may tighten to it. `flag` is the floor: there is no `off`.

Resibo's behaviour was an error code `UNKNOWN_ACTION` with `recorded=False` (best-effort, surfaced honestly). Bazaar's default is stricter about evidence (record and flag) and its strict mode is stricter about vocabulary (reject).

The check is a decorator, `VocabularyCheckingAuditLog : ITransactionalAuditLog`, registered after `AuditComponent` through `Decorate`-style registration inside this module, so `IAuditLog`, `AuditEntry` and `AppendAsync` are unchanged and `PostgresAuditLog` is untouched. It forwards `AppendInTransactionAsync` too, so the transactional path (RyuseiFuda, setup, modules) is covered. It also applies the existing `AuditPayloadGuard` (from `Bazaar.PrivacyAudit`, reused, not copied) so the secret-word rule is enforced for every registered operation, not only the privacy recorder's. Note the guard rejects any key containing `key`, `code`, `auth`, `pin`, so required payload field names must avoid those words; widening the guard to allow exact-named ids such as `key_id` is OPEN (question 6).

### 8.3 Principal overloads and the legacy actor mapping (non-breaking)

`AuditEntry(DateTimeOffset Ts, string Actor, string Kind, string Payload)` and `IAuditLog.AppendAsync(AuditEntry, ct)` do not change. Additions live in `Bazaar.Server.AuditVocabulary.Abstractions`, so the audit projects that other threads are editing are not touched:

```csharp
public static class AuditLogPrincipalExtensions
{
    // New call sites use these. They write Principal.ToString() ("kind:id") into the existing Actor column.
    public static Task AppendAsync(this IAuditLog log, Principal actor, string kind, string payloadJson,
                                   TimeProvider time, CancellationToken ct = default);
    public static AuditEntry ToEntry(this Principal actor, string kind, string payloadJson, DateTimeOffset ts);
}

public static class LegacyActorMapper
{
    /// Reads a stored actor string. Canonical kind:id values parse as themselves; legacy bare values map as below.
    public static Principal Read(string storedActor);
    public static bool IsCanonical(string storedActor);
}
```

Rules:

- **Stored rows are never rewritten.** The hash covers the actor text (`AuditChain`, doc 48), so a one-time rewrite would break every hash. "One-time mapping" in BAZAAR-COMPONENTS 4.2 therefore means a read-side mapping applied by viewers, exports and reports. The stored text stays as written, and old rows remain verifiable.
- A stored actor is canonical when it splits at the first `:` into a kind in {`agent`,`user`,`service`,`system`} and a non-empty id.
- New writes use `Principal.ToString()`. Queries `WHERE actor = 'agent:charles'` keep working; queries for old values keep working against old rows.

Mapping table from the literals present in `src` today (each is a PROPOSED mapping; the owner may rename ids):

| Stored text (where written) | Read as | Note |
|---|---|---|
| `agent:<guid>` (`ToolDispatcher`) | `agent:<guid>` | Already canonical. |
| `system:memory`, `system:breach` | unchanged | Already canonical. |
| `system` (`RyuseiFireAudit`, `AuditAnchorService`) | `system:host` | Bare kind with no id. |
| `setup:plan` (`SetupConstants.PlanActorId`) | `system:setup` | Matches the example in BAZAAR-COMPONENTS 4.2. |
| `kek` (KEK audit adapter) | `service:kek` | Named in 4.2. |
| `identity` (`IdentityContracts`) | `service:identity` | |
| `vm-component` (`PgAuditWriter`) | `service:vm-component` | |
| `human:<id>` (PrivacyAudit callers) | `user:<id>` | Kind rename. |
| `device:<id>` (`TokenService`) | `service:device.<id>` on read | OPEN: Principal has no `device` kind (question 3). New writes should record the owning `user:` and put `device_id` in the payload, as `PrivacyAuditRecorder` already does. |
| `anonymous` (`PrivacyAuditRecorder.AnonymousActor`) | `system:anonymous` | An unauthenticated attempt. OPEN. |
| `OperatorIdentity` free text (`SetupApplier`) | `user:<text>` when it parses, else `system:setup` | OPEN: verify what the setup path puts here. |
| `record.AgentId` (`CommunityActionStore`) | `agent:<id>` | Bare id with no kind prefix. |
| anything else | `system:legacy.<slug of the text>` | Never throws; flagged by a counter. |

Migration of writers: each module changes its writer to a Principal overload in its own PR (WP-M1, section 18). Until then its rows are valid and readable. A seeded test lists the literals above and fails when a new bare actor literal appears outside a baseline that may only shrink.

### 8.4 Conformance for module authors

`Bazaar.Server.AuditVocabulary.Testing` provides `AuditVocabularyConformanceTests<TModule>`, parallel to `ModuleHealthContractTests<T>` (doc 46 section 10.3). It asserts: the module registers at least the operations it writes; every registered kind matches the naming pattern; no kind is registered by two modules; every `Privileged` operation has a test that observes the row; `Planned` operations have `PlannedUntilVersion` greater than the current version.

### 8.5 Audit events and Kodo

Operations registered by this module itself: `audit.vocabulary.snapshot` (System), `audit.vocabulary.unregistered_kind` (Security), `audit.retention.executed` (Admin, `RetentionExempt`), `audit.retention.shortened` (Admin), `audit.retention.blocked_by_hold` (Security), `logs.purge.executed` (System), `logs.root.unsafe_location` (Security), `logs.tail.subscribed` (Security), `logs.verbosity.lease_started` and `logs.verbosity.lease_ended` (Admin). Actors use `Principal`: `service:program-logs`, `service:audit-retention`, `system:host`, or the requesting `user:` or `agent:`.

## 9. Tabula side: `Bazaar.Server.AccessGrantAudit` (reason-tagged access-grant event)

Resibo's break-glass grant is the model: a time-boxed grant (default 60, maximum 480 minutes), a required reason, expiry enforced at check time, audited, and both the owner and the affected party notified (`v3-deepdive-05` section 6.3 and 7, `resibo-01-09` inventory; `core/audit/CLAUDE.md`). Doc 72 owns the Bazaar mechanism. This module owns only the audit shape, so the grant event is the same no matter which feature issues a grant.

```csharp
// Bazaar.Server.AccessGrantAudit.Abstractions
public enum AccessGrantReason { Support, Incident, Legal, Recovery, Maintenance, Review, Investigation, Other }

public sealed record AccessGrantEvent(
    Guid GrantId,
    Principal Grantor,                 // who authorised
    Principal Grantee,                 // who gets access
    Principal? Subject,                // whose data or scope, when personal
    string ScopeKind,                  // "gc", "agent", "vault-path", "audit-view", "logs", ...
    string ScopeId,
    AccessGrantReason ReasonTag,       // required, never null
    string ReasonLabel,                // slug, ^[a-z][a-z0-9_]{0,31}$ (same rule as PrivacyAuditRecorder labels)
    DateTimeOffset ExpiresAt,          // required: no grant without an expiry
    Guid? ApprovalId);                 // the signed approval (doc 15, IOwnerSignatureVerifier), when one was needed

public interface IAccessGrantAuditRecorder
{
    Task IssuedAsync(AccessGrantEvent e, CancellationToken ct = default);
    Task UsedAsync(Guid grantId, Principal who, string scopeKind, string scopeId, CancellationToken ct = default);
    Task DeniedAsync(Principal who, string scopeKind, string scopeId, AccessGrantReason? attemptedReason, string denialLabel, CancellationToken ct = default);
    Task RevokedAsync(Guid grantId, Principal by, string reasonLabel, CancellationToken ct = default);
    Task ExpiredAsync(Guid grantId, CancellationToken ct = default);   // system:access-grants
}
```

Operations registered (all `Security`, all `Privileged`, all `RetentionExempt`): `access_grant.issued`, `access_grant.used`, `access_grant.denied`, `access_grant.revoked`, `access_grant.expired`.

Rules: the free-text reason never enters audit. It is a doc 72 grant row field (personal fields encrypted per subject, doc 48 section 5.5); audit carries `reason_tag` and `reason_label` only, matching `AuditPayloadGuard`'s ban on personal text. A missing reason tag or expiry throws before anything is written (a grant with no reason cannot exist, as in Resibo). `Used` is recorded per scope entry, not per object (per-object reads are already `DataRead` rows under doc 48 section 5.1); it links to the object reads by `grant_id`. The owner's DM reads (`dm.owner_read`, existing) are unchanged; a DM access grant to another person is an `access_grant.*` pair on top. Notification of the affected party and the owner is doc 72 and the notice channel in doc 15, not audit.

## 10. The hygiene rules (CI)

Resibo's `check_no_silent_except.py` exists because `core/logs` had a writer with zero callers outside its own package, so every broad `except Exception: pass` wrote to nothing (`core/logs/CLAUDE.md`; 102 pre-existing violations were baselined). Bazaar's equivalent is a set of architecture tests, in `tests/Bazaar.Architecture.Tests`, in the same style as the existing rules and with a seeded-violation test for each (the pattern in `SeededViolationTests`). They are tests, so they run on every build and need no separate workflow.

### 10.1 Scanning method

Source scan over `src/**/*.cs`, excluding `obj`, `bin`, generated code and `*.Tests` projects. The scanner parses with Roslyn syntax trees (a dev-only dependency of the test project; no library or module takes it). This is more accurate than regex for catch clauses and `when` filters. If the owner prefers no Roslyn reference even in tests, a token-based scanner is the fallback and its precision limits are recorded in the test's header (question 7).

### 10.2 Rule R1: every broad catch logs or rethrows

A catch clause is **broad** when it has no type, or names `Exception`, `System.Exception` or `SystemException`. Narrow, named exception types converted into a clean result are not flagged; they are Bazaar's normal pattern, and flagging them would be noise (Resibo's `core/logs/CLAUDE.md` draws the same line). A broad clause passes when its body, searched recursively, contains any of:

- a `throw` statement;
- a call whose method name contains `Log` (case-insensitive), including `LogFailure`, `LogAttention`, `ILogger.LogError`, or a call on an `ILogger`-typed receiver;
- the explicit marker in 10.3.

Everything else is a violation. A new violation fails the build. Pre-existing violations are listed in `tests/Bazaar.Architecture.Tests/Baselines/silent-catch.txt`; the baseline may only shrink (the test fails if an entry no longer reproduces, naming it, so the list is updated in the fixing PR).

Baseline entries are keyed `relative/path.cs|EnclosingType.Member|ordinal`, not `path:line` (PROPOSED, Bazaar's design, not from Resibo), because line-number keys go stale on any edit above the catch, which makes unrelated PRs report "fixed" entries and, worse, a moved violation look new.

### 10.3 The marker

Some broad catches must not log: the logger's own failure path (recursion), a shutdown cleanup, disposal best effort. Such a catch carries a trailing comment `// bazaar:swallow-ok(<reason of at least 12 characters>)` on the `catch` line. The test lists every marker with its reason in the test output and fails if the count of markers exceeds a recorded ceiling that may only shrink without an owner-visible diff. A marker without a reason fails. `OperationCanceledException` catches are narrow and need no marker.

### 10.4 Rule R2: every registered operation has a caller

For each `AuditOperation` that is `Active`, the scanner resolves the constant or descriptor member that holds its `Kind` and requires at least one reference in `src` outside its declaring file and outside tests. Zero references fails the build ("registered but never emitted"). `Planned` operations are exempt until `PlannedUntilVersion`, then fail, so building ahead is allowed and cannot become permanent. `Privileged` operations additionally require a test that observes the row (checked by the conformance base in 8.4). This is the audit twin of Resibo's privileged-action coverage test (`v3-deepdive-08` section 7).

### 10.5 Rule R3: no appended kind is an unregistered literal

A call that builds `new AuditEntry(...)` or calls an audit append with a string-literal kind must use a kind present in the registry. A computed kind (a variable) is allowed only inside a recorder class that registers its full set. Pre-existing literals are baselined with the same shrink-only rule. Today's kinds in `PrivacyAuditKinds`, `ryusei.trigger.*`, the setting audit kinds, kill-switch kinds and others are inventoried in WP-V4 so the baseline starts as a short list.

### 10.6 Rule R4: the log provider is reachable (the Resibo bug, directly)

Two tests. (a) **Static:** the program-logs `ILoggerProvider` is registered by the host composition, and at least N first-party modules (initial N: 5) each contain at least one `ILogger` call, so the provider cannot be present and unused. (b) **Dynamic:** a host start test boots every enabled first-party component against an in-memory sink and asserts the sink saw entries from at least N distinct `ModuleId`s and that a forced failure inside a `Task.Run`-dispatched call arrives with its full chain (Resibo's traceback-capture regression test, `v3-deepdive-18` section 9). At runtime the Kodo self-test (section 13) repeats (b) cheaply.

### 10.7 Anti-vacuity

Resibo's own `MAINTENANCE.md` records that a required check which passes vacuously "reads as enforcement while verifying nothing". Each rule above therefore also asserts a positive sanity count: R1 must find at least one catch clause, R2 and R3 at least one registered operation, R4 at least one `ILogger` call. A seeded-violation test per rule proves it fires on a planted violation. The rules run unconditionally, not behind path filters.

## 11. Data (SQL sketches)

Each module's SQL lives in its own namespace folder (BAZAAR-COMPONENTS section 5). Runtime role `bazaar_app` per `common/0004_audit_log.sql`.

### 11.1 `src/Bazaar.Server.ProgramLogs/Data/Sql/program-logs/0001_verbosity_lease.sql`

```sql
CREATE TABLE program_log_verbosity_lease (
    lease_id     uuid        PRIMARY KEY,
    module_id    text        NOT NULL,
    level        text        NOT NULL CHECK (level IN ('trace','debug','information','warning')),
    requested_by text        NOT NULL,                 -- Principal text form
    reason_label text        NOT NULL,
    started_at   timestamptz NOT NULL,
    expires_at   timestamptz NOT NULL,
    ended_at     timestamptz,
    CHECK (expires_at > started_at)
);
CREATE INDEX program_log_verbosity_lease_active ON program_log_verbosity_lease (module_id) WHERE ended_at IS NULL;
```

Leases are operational state, not evidence. The audit rows `logs.verbosity.lease_started` and `lease_ended` are the evidence.

### 11.2 `src/Bazaar.Server.AuditVocabulary/Data/Sql/audit-vocabulary/0001_operation_snapshot.sql`

```sql
CREATE TABLE audit_operation_snapshot (
    snapshot_id   bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    taken_at      timestamptz NOT NULL,
    registry_hash bytea       NOT NULL CHECK (octet_length(registry_hash) = 32),
    operation_count int       NOT NULL CHECK (operation_count >= 0),
    descriptors   jsonb       NOT NULL            -- the sorted descriptor list, for later comparison
);
REVOKE ALL ON audit_operation_snapshot FROM PUBLIC;
GRANT INSERT, SELECT ON audit_operation_snapshot TO bazaar_app;
```

Its purpose is diffing the vocabulary between versions, so removing a registered kind that old rows still use is visible. The chain row `audit.vocabulary.snapshot` carries the hash.

### 11.3 `audit-retention/0001_retention_run.sql`

As in section 7.4. The retention classes are held in the registry, not in a table.

The file index for logs is a sidecar file per day file (`<day>.jsonl.idx`: one line per minute with the byte offset and, for correlation ids, a short list), rebuilt from the JSONL files alone, never a second source of truth. Resibo's design uses a SQLite-free file index (`v3-deepdive-18` section 3.2); Bazaar likewise does not use a database, because logging must keep working when Postgres is down.

## 12. What is taken from Resibo, what is changed, and why

| Resibo item (source) | Bazaar treatment | Why |
|---|---|---|
| Day-rotated JSONL per scope, pure append (`v3-deepdive-18` 3.1) | Taken, three scopes (global, module, Principal) | Same simple, streamable, crash-friendly format. |
| `.trace.jsonl` sibling for short-retention tier (`core/logs/CLAUDE.md`) | Taken; also holds content-bearing entries | Whole-file delete, no rewrite. |
| Verbosity tiers and per-service overrides (`v3-deepdive-18` 8) | Taken as per-module runtime settings, plus leases | Bazaar has hot-reload via the settings registry; leases remove the forgotten-Trace failure. |
| `ATTENTION` level and `LEVEL_STYLE_HINT` (3.1, 3.4) | Taken | Kodo and hygiene checks need a "running below baseline" signal distinct from Error. |
| Full traceback unconditionally; `guarded_call` (3.3, `core/logs/CLAUDE.md`) | Taken as `LogFailure` and unconditional write of any exception | Direct fix for Resibo V2's `str(e)` bug. |
| Live tail via a server-streaming RPC (7) | Taken as the `logs.tail` stream on `/ws`, not gRPC | Wire format is JSON on the Bazaar stream registry (doc 02). Adds gap frames. |
| SQLite index over JSONL (3.2) | Changed to a sidecar index file | Logging must not depend on Postgres. Still rebuildable and not a source of truth. |
| Log root outside the repo (PRINCIPLES 1.6) | Taken and hardened to four layers (6.4) | The Vault is a git repo; the platform repo is public. |
| Cross-user read needs break-glass (4) | Changed to capability `logs.read` with scopes; cross-scope read is an access grant | Bazaar has custom roles (doc 48 section 4). |
| Retention 90 days, TRACE 7 days (5, 10) | Taken as defaults | Pure operational-cost tradeoff, no compliance reason against. |
| Sinks as a Provider Registry, all sinks get every entry (`v3-deepdive-18`) | Taken: each sink a separate provider module, no selector | Matches owner direction on providers. |
| Audit retention policy shape, shortening needs a reason, purge records exempt (`core/audit/CLAUDE.md`) | Taken | Policy generic; mechanism excluded. |
| 10-year audit default from BIR RR 17-2013 | Not taken; default `indefinite` | DOMAIN (Philippine tax rule), not generic. |
| Audit retention by SQL `DELETE` | Not taken; mechanism constraints in 7.3 | Hash chain and append-only triggers. |
| Closed `ActionType` enum (`core/audit/CLAUDE.md`) | Changed to per-module registered vocabulary | Modular; an enum would couple every module to one project. |
| `UNKNOWN_ACTION` unrecorded result (`supervisor/CLAUDE.md`) | Changed to flag-by-default, reject-optional | An audit log should not lose events to a bookkeeping gap. |
| `PRIVILEGED_ACTIONS` coverage test (`v3-deepdive-08` 7) | Taken as `Privileged` flag plus R2 and the conformance base | Structural check, not per-action trust. |
| Reason-required, time-boxed break-glass event (`BREAK_GLASS_*`) | Taken as the `AccessGrantEvent` contract | Owner card; mechanism is doc 72. |
| `check_no_silent_except` and its shrink-only baseline (`core/logs/CLAUDE.md`) | Taken as R1 to R4; baseline keyed by member and ordinal, not line (PROPOSED) | Line keys go stale on any edit above the catch. |
| `set_authorizer` append-only enforcement (`core/audit/CLAUDE.md`) | Not taken | Postgres role grants plus triggers already exist (`common/0004_audit_log.sql`). |
| Audit sinks registry for a WORM mirror (`v3-deepdive-08`) | Not taken | Already designed as multi-sink with anchors and replica (doc 48 section 5.4). |
| FrozenDict and Python-specific contract rules | Not applicable | Records and `IReadOnlyDictionary` in C#. |
| Historian data-change and narrative tracks | Narrative: doc 76. Data-change: existing versioned tables | No new module here. |

## 13. Kodo health contract

Each module implements `IModuleHealthContract` (doc 46 section 10; `Bazaar.Server.Modules.Abstractions.Health`). Liveness does no I/O.

| Module | Liveness | Readiness | Dependencies | Self-test (`Quick` read-only; `Full` in a temp scope that is removed) |
|---|---|---|---|---|
| `program-logs` | Writer loop heartbeat tick is recent. | Queue under 80 percent; at least one sink healthy; log root not flagged unsafe. | Soft: `settings`, `kodo`. Hard: kernel only. | Quick: counters sane, configured tiers resolve. Full: write a `selftest` entry with a forced exception through the real path, read it back with its full chain, rebuild the sidecar index for that file, confirm no residue; confirm every enabled first-party module has emitted at least one entry in the last 24 hours (Warn, not Fail, naming silent modules; the runtime twin of R4). |
| `program-logs-file` | Flush task alive. | Root writable; free space above `logs.disk.min_free_mb`; guard passed. | Hard: `program-logs`. | Full: write, rotate across a faked day boundary, retention plan excludes today, index rebuild equals scan result. |
| `program-logs-tail` | Fan-out task alive. | Subscribers under limit; ring present. | Hard: `program-logs`. | Full: subscribe, force a slow consumer, assert a gap frame and bounded memory. |
| `audit-vocabulary` | Registry frozen flag set. | Registry non-empty; hash matches the last snapshot or a new snapshot was written. | Hard: `audit`. | Quick: no duplicate kinds. Full: append one registered and one unregistered kind to a sandbox log (flag mode); the second is flagged and counted. |
| `audit-retention` | Sweep scheduler alive. | Policy resolves for every class; no run stuck. | Hard: `audit-vocabulary`. Soft: `settings`. | Quick: planner on a seeded clock; Security and Admin horizons are null. Full: dry-run plan on a sandbox chain verifies from the next checkpoint. |
| `access-grant-audit` | Always true (stateless). | Registry contains the five operations. | Hard: `audit-vocabulary`. | Full: issue, use, expire in a sandbox log; a grant with no reason or no expiry throws before writing. |

Kodo reads these counters as facts: `logs.dropped`, `logs.queue_depth`, `logs.sink_failures`, `audit.unregistered_kinds`, `audit.vocabulary.mode`, `audit.retention.last_run_age`.

## 14. Interaction with existing modules

- **`audit` (`AuditComponent`, `PostgresAuditLog`, `ITransactionalAuditLog`):** unchanged. `audit-vocabulary` decorates the registration; it does not edit `Bazaar.Audit.Abstractions` or `PostgresAuditLog`. The note for the other audit threads (read-audit recorder #594, Npgsql coverage #766, KEK audit adapter): their writers keep working; each new operation they add is registered in the same PR (R2 and R3 will otherwise flag it), and new writers should use the Principal overload. The retention sweeper #814 consumes `IAuditRetentionPolicy` and must meet 7.3.
- **`Bazaar.PrivacyAudit`:** its `PrivacyAuditKinds` become the first registered source (WP-V3). Its payload guard is reused by the vocabulary decorator. Its recorder methods take `string actorId`; Principal overloads are added there by its owner.
- **Settings (`Bazaar.Server.Settings`, `SettingsComponent`):** supplies hot reload and the audit of setting changes (reused for verbosity changes). If the registry has no change-notification feed yet, `program-logs` polls on a timer (`logs.settings_poll_seconds`, default 10) until it does.
- **Doc 15:** taint tag is copied onto log entries; kill-switch and containment events stay audit rows. The doc 15 audit paragraph needs one line pointing at the vocabulary rule (main session updates it).
- **Doc 14 / Identity Service:** audit signing key unchanged. The exact-value redaction question depends on it (question 4).
- **Doc 43 / `Bazaar.Diagnostics`:** reused as in 6.1; request for an additive redaction option noted in 6.6.
- **Doc 46 Kodo:** readers of the counters above; writer of `Attention` entries. Kodo's own journal is unchanged.
- **Doc 33 and 44:** backups and exports exclude the log root unless `logs.include_in_export` is set; audit retention never removes anchored checkpoints.
- **Doc 72:** supplies the grant mechanism; calls `IAccessGrantAuditRecorder`.
- **Doc 76:** the narrative track; no dependency.
- **GC `PrincipalKind` (`Bazaar.Contracts.Gc`):** see section 16, conflict C1.
- **Existing audit writers** (settings sink, kill-switch adapter, Ryusei fire audit, module manager, setup applier, memory store, identity): unchanged until WP-M1; each registers its kinds in WP-V4.

## 15. Security and privacy

- Logs may hold personal data. Defaults: content-bearing entries only at Trace in the short-retention file; `user` Principal scopes off; export off; agents read no logs; reads are audited as subscriptions and queries.
- Secret-shaped strings are redacted on the write path with no length cap; extra patterns tighten only; redaction failure never writes the raw field.
- Tail streams honour `logs.read` scopes and are bound to the authenticated connection; replay and tail never cross scope.
- The path guard and the self-ignoring directory prevent the log root landing in git; the repo gate test makes this a build failure.
- The vocabulary decorator and rule R3 make "write an audit row with a made-up kind" a visible event. `flag` mode never loses evidence.
- Retention can only touch `DataRead` and `DataWrite` rows, never Security or Admin, never held scopes, never anchored checkpoints. A shortening is a high-risk action: owner approval, a stated reason, an audit row.
- Access-grant audit rows carry tags and slugs only; free-text reasons stay in doc 72's encrypted fields.
- Erasure: per-Principal log directories are deleted in a person's erasure plan; the signed proof lists them. Audit rows are handled by existing crypto-erase (doc 48 section 9).
- Legal: log and audit retention periods, per-Principal user logs and log export are legal-exposure settings. Shipped defaults are the most protective option (audit indefinite, user logs off, export off). Needs counsel; this is not legal advice.

## 16. Conflicts and overlaps for the owner

| # | Where | Conflict or overlap | Treatment |
|---|---|---|---|
| C1 | `Bazaar.Contracts.Gc.PrincipalKind` (owner, human, agent, ...) vs the decided `Principal` kinds (agent, user, service, system) | Two "principal kind" concepts with similar names, different values. `Principal` is not yet in `Bazaar.Core`. | Not resolved here. A distinct type name is needed in Core (for example `ActorKind`) or the GC type must be renamed. Owner to decide. |
| C2 | Resibo's closed `ActionType` vs modular registration | A central enum cannot exist in Bazaar. | Per-module registry (section 8). Both ideas kept: the registry is the vocabulary. |
| C3 | Resibo audit retention by DELETE vs hash-chained append-only audit | Cannot delete rows in a chain. | Policy taken, mechanism constrained (7.3). Mechanism is the sweeper's (#814). |
| C4 | Resibo 10-year default vs Bazaar `indefinite` default | Resibo's number is a Philippine rule. | Default `indefinite`, floors are PROPOSED drafts. |
| C5 | Audit actor strings in `src` | Several bare and non-canonical values (section 8.3). | Read-side mapping; no rewrite (rewriting would break hashes). |
| C6 | `Bazaar.Diagnostics.Redactor` 4096-character cap | Incompatible with "full tracebacks locally". | Pattern layer reused without the cap; an additive option may be needed in `Bazaar.Diagnostics`. |
| C7 | `AuditPayloadGuard` word list vs `key_id`-style names | Guard rejects any key containing `key`, `code`, `auth`, `pin`. | Registered payload fields avoid those words; widening the guard is OPEN. |
| C8 | Doc 43 ring buffer vs server tail ring | Overlap in purpose. | Same class, different instances and scopes; no duplicate type. |
| C9 | OpenTelemetry logs (doc 23) vs program logs | Overlap in `ILogger` consumption. | Both fed by `ILogger`; no OTLP sink built. |
| C10 | Namespace: `IAuditLog` lives in `Bazaar.Server.Security.Audit` inside project `Bazaar.Audit.Abstractions` | Project and namespace names differ. | Noted so new modules reference the project name; not changed here. |
| C11 | Docs 72 and 76 | Not in the tree when this doc was written. | Referenced by number only; section 9 and 5 must be checked against them when they land. |

## 17. Testing (xUnit v3; headless UI where UI)

- **Logs:** traceback regression (an exception thrown inside `Task.Run` and through an `await` chain is written with its full inner-exception text at every tier); tier filter (Information entry dropped at Warning, exception entry kept at Error tier); lease expiry and survival across restart; day rotation across a faked UTC boundary with an injected `TimeProvider`; `.trace.jsonl` routing for content-bearing entries; retention planner pure-function tests including "never today", legal-hold skip and the 7-day trace window; slug filter (`../x`, absolute path, empty id) cannot escape the root; start-up guard (root inside a temp git work tree falls back and raises `logs.root.unsafe_location`); self-ignoring `.gitignore`; sidecar index delete-and-rebuild equals scan; redaction (seeded token, no truncation of a 100 KiB traceback, redactor throw yields `<redacted:failed>`); slow-sink isolation (one sink throws, the others still receive, caller never sees an exception); queue pressure drops Information but never an exception, emergency file written when reserved capacity is full; tail gap frame and bounded memory; Principal scope off for `user` by default.
- **Audit vocabulary:** duplicate kind fails startup naming both modules; foreign-prefix registration refused; `flag` mode appends with `vocab=unregistered` once per kind; `reject` throws and writes nothing; transactional path covered; legacy mapper table-driven test over every literal in section 8.3, plus a property test that `Read` never throws and canonical strings round-trip; a hash test proves rows written with old and new actor forms both verify.
- **Retention:** boundary test (a row at horizon minus one day kept, plus one day eligible), `Indefinite` never yields a horizon, Security and Admin never eligible whatever the setting, held scope skipped, floors refuse a shorter setting, shortening needs a reason and writes `audit.retention.shortened`, run record exempt, record written after effect and a failed record write is surfaced.
- **Access grant:** missing reason or expiry throws; free-text never in payload (guard test); five operations registered; used rows link to `grant_id`.
- **Hygiene rules:** seeded violation per rule (planted bare catch, planted unreferenced operation, planted literal kind, planted unregistered provider), the shrink-only baseline test, the marker-reason test, and anti-vacuity counts (10.7). Real-tree test runs each rule with the baseline.
- **Client:** `Avalonia.Headless` tests for the log pane: level filter, follow mode, `Attention` rendered with the persistent hint, traceback expand, gap marker visible.
- **Kodo:** each module inherits `ModuleHealthContractTests<T>` (liveness no I/O with throwing fakes; budgets under a slow fake).

## 18. Work packages

Each is one project or file group with its own tests. Order is by dependency; units at the same level are independent.

| WP | Content | Depends on |
|---|---|---|
| WP-P0 | `Principal` in `Bazaar.Core` (other thread; this doc only consumes it) | none |
| WP-V1 | `Bazaar.Server.AuditVocabulary.Abstractions`: `AuditOperation`, registry contracts, `AuditClass`, name pattern, `LegacyActorMapper`, Principal extension methods, with mapper tests | WP-P0 |
| WP-V2 | `Bazaar.Server.AuditVocabulary`: registry build and freeze, duplicate and prefix checks, snapshot row and table, `VocabularyCheckingAuditLog` decorator, modes | WP-V1, `audit` |
| WP-V3 | Register `PrivacyAuditKinds` and the vocabulary and logs operations as the first sources | WP-V2 |
| WP-V4 | Inventory of every existing audit kind literal in `src` (Ryusei, settings, kill switch, anchors, modules, install, vms, memory, community, identity, setup), one registration PR per owning module (small, parallel) | WP-V3 |
| WP-V5 | `Bazaar.Server.AuditVocabulary.Testing` conformance base | WP-V2 |
| WP-A1 | `Bazaar.Server.AccessGrantAudit(.Abstractions)` and recorder | WP-V2 |
| WP-R1 | `Bazaar.Server.AuditRetention.Abstractions`: classes, policy, run record | WP-V1 |
| WP-R2 | `Bazaar.Server.AuditRetention`: policy implementation, floors, hold check, run table, settings rows; agree mechanism with sweeper #814 | WP-R1, doc 48 retention decision |
| WP-L1 | `Bazaar.Server.ProgramLogs.Abstractions` and the `ILogger` provider, scopes, queue, tiers, `LogFailure` and `LogAttention` extensions | none (uses kernel) |
| WP-L2 | `Bazaar.Server.ProgramLogs.FileSink`: rotation, scopes, sidecar index, never-committed guard, emergency file | WP-L1 |
| WP-L3 | Redaction hook over `Bazaar.Diagnostics` patterns; request additive no-cap option | WP-L1 |
| WP-L4 | Verbosity runtime settings, leases and table | WP-L1, settings registry |
| WP-L5 | `Bazaar.Server.ProgramLogs.LiveTail` and the `logs.tail` contracts in `Bazaar.Contracts` | WP-L1, doc 02 stream registry |
| WP-L6 | Retention planner and sweeper job | WP-L2 |
| WP-L7 | `Bazaar.Server.ProgramLogs.Testing` | WP-L1 |
| WP-C1 | `Bazaar.Client.ProgramLogs` pane | WP-L5 |
| WP-H1 | Hygiene rule R1 (silent catch) with seeded test and baseline file | none |
| WP-H2 | Rule R2 and R3 (operation callers, literal kinds) | WP-V3, WP-V4 |
| WP-H3 | Rule R4 (provider reachable) static and dynamic | WP-L1, WP-L2 |
| WP-K1 | Kodo health contracts for all six modules | each module |
| WP-M1 | Per-module writer migration to Principal overloads (one PR per module) | WP-V1 |

## 19. Open questions

1. **Audit retention mechanism (A crypto-shred vs B segment prune)** for the sweeper #814. Which, and who owns it? Preference stated in 7.3 is A.
2. **Retention floors and defaults** (365 and 1825 days, `Indefinite` default) are drafts. Owner to confirm or replace, and whether `DataWrite` should be prunable at all.
3. **Principal and devices.** Principal has no `device` kind. Record the owning `user:` plus `device_id` in the payload (proposed), or add a kind. Also `anonymous` and the `OperatorIdentity` setup text mapping.
4. **Exact-value redaction in logs.** Needs secret material in the logger's memory. Option: a keyed-hash matcher fed by the Identity Service. Needs a security note before building.
5. **Verbosity lease approval.** Is owner approval for raising a content-bearing module to Trace too strict for routine debugging, or right?
6. **`AuditPayloadGuard` word list** blocks `key_id`, `auth_*`, `code_*`. Widen to exact allow-listed names, or keep and avoid the words.
7. **Roslyn in the architecture test project** for rule scanning, or a token scanner. Roslyn is proposed (dev-only).
8. **Log index on Postgres as an optional provider** for fast cross-day queries, beside the sidecar files. Not proposed for v1.
9. **Per-Principal `user` scope**: ship the option at all in v1? Needs counsel on keying a log to a person.
10. **Client mnemonics** (`lg` proposed) and the pane's place in the dashboard: doc 41.
11. **Names.** Annales and Tabula are the owner's library names. The Bazaar module names in section 4 are working names, not owner-named.
