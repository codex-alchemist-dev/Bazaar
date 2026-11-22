# 70. Issue Filer

Status: PROPOSED (2026-11-19). The owner's 2026-11-19 direction to adopt the Resibo issue filer into Bazaar as module `Bazaar.Server.IssueFiler` is DECIDED (owner timeline, 2026-11-19: "incorporate everything from Resibo that can benefit Bazaar; where something conflicts, separate module"; `CONVERGENCE.md` section 8, row B1). Every design detail below that direction is PROPOSED unless it restates a DECIDED item. Nothing in this document is built. Owner questions: 6 (section 17).
Depends on: 02-wire-protocol-and-sync.md (authenticated surface, stream registry), 03-server-host-and-providers.md (settings registry), 04-postgres-schema-and-migrations.md (conventions), 14-identity-service-and-passport.md (GitHub App keys, `ISecretStore`), 15-security-taint-and-approvals.md (taint, reader sessions, approval tiers, quotas), 21-github-integration.md (App tokens, rate limits, bot labelling), 40-core-feature-modules-audit.md (module shape), 43-telemetry-and-crash-reporting.md (redaction, fingerprint, grouping, consent), 46-kodo-health-module.md (incidents, module health contract), 48-gap-decisions-security-identity.md (audit log), BAZAAR-COMPONENTS.md (manifest, `Principal`, migration namespaces), 71 (dependency watcher; not yet written, see section 10 item 4).
Used by: 43 (its issue-filing stage moves behind this module), 46 (Kodo is a signal source), 06 (agent loop-guard is a signal source), 34 (test strategy).

## 1. Purpose

A Bazaar install notices things that deserve a tracked issue: a client crash group, a Kodo incident, a module that stopped answering its liveness check, a dependency change that needs a human, an agent that tripped the loop guard. Today the only designed path from signal to issue is the crash path in doc 43, which ends in one GitHub repository. Resibo (the sister program) has the missing generic half: a fingerprint-deduplicated, append-only ledger of everything the install ever filed, GitHub App filing, a detector that turns a signal into a filing, and an opt-in gate that files nothing unless configured.

This document designs `Bazaar.Server.IssueFiler`: one module that takes signals from many sources, decides whether and where to file, redacts, deduplicates, rate limits, files through a sink, and keeps an append-only ledger of everything it did. The short answer:

- **One pipeline, many sources, many sinks.** Sources (crash groups, Kodo incidents, silent modules, dependency findings, loop-guard trips) and sinks (GitHub issues, email, a GC post) are separate provider modules that plug into one core by interface. Nothing in the core names GitHub.
- **The ledger is append-only.** Every decision (filed, commented, reopened, held, refused, closed) is a row that is never updated or deleted. Current state is a projection of it.
- **Consent is explicit and scoped.** Filing is off until a `Principal` with authority turns it on for a scope. The shipped default files nothing.
- **Redaction is not optional and is not new code.** Every outbound body goes through doc 43's redactor and residual scan. Resibo's docs state redaction as a rule (`core/telemetrees/CLAUDE.md`) and describe no mechanism for it; Bazaar's redactor is the implementation.
- **No GitHub credential logic is duplicated.** Tokens come from doc 21's `IGitHubTokenProvider` (the Identity Service signs the App JWT, doc 14). This module never holds a private key.
- **Reopen on recurrence is built in**, where Resibo's docs describe dedupe only as never re-filing an already-open issue (`core/telemetrees/CLAUDE.md`) and say nothing about recurrence after close.

## 2. Decided inputs (not re-opened here)

- Adopt the Resibo issue filer as a Bazaar module (owner, 2026-11-19; `CONVERGENCE.md` B1). Where Resibo conflicts with a Bazaar decision, both exist as separate modules (owner, 2026-11-19); section 11 lists each conflict and where it went.
- Providers are plain modules on Core, with no selector or role setting (owner, 2026-11-19, PR #919). Sources and sinks here follow that.
- Actor attribution is the `Principal` type, kinds `agent`, `user`, `service`, `system`, text form `kind:id` (owner, 2026-11-19; `BAZAAR-COMPONENTS.md` 4.2). The audit log keeps its single `actor` text column and its hash chain.
- Crash reports go to the company's own server. The server files GitHub issues and deduplicates by fingerprint. No client holds a GitHub credential (owner, 2026-11-13; doc 43 section 2).
- Redaction is never model-dependent (owner, 2026-11-13). Any model explanation is labelled "unverified diagnosis".
- GitHub identity is GitHub Apps in org `codex-alchemist-dev`, not machine accounts. The Identity Service holds provider and App keys (CLAUDE.md; doc 14; doc 21).
- Instances are private by default. Public-posting approval tiers are operator-configured with documented options (CLAUDE.md).
- Configurability rule (DECISIONS-LOG round 13): an open value is an operator setting with a safe default, audited on change. DECIDED items and security invariants are not settings. Settings may tighten, never loosen.
- Email is external-only. Internal agent coordination stays in GCs (CLAUDE.md). The email sink therefore sends outward, to a configured external address, never as a coordination channel.
- Every feature is a module `Bazaar.Server.<Name>` plus `.Abstractions`, with a `Bazaar.Client.<Name>` module when it has a client side (`BAZAAR-COMPONENTS.md` 2.1, doc 40). Shared libraries and modules hold contracts and logic only, with no third-party dependencies. Providers are separate modules.
- Version display `0.MM.PP+commit`; the module carries the same SemVer as every module.

## 3. What this document does not decide

- The crash pipeline itself: capture, on-device redaction, consent on the device, ingestion and grouping rules. Those stay in doc 43 and `Bazaar.Diagnostics`. This module takes over only the last stage (decide, file, comment, reopen) for crash groups, and only after the owner confirms question 1 in section 17.
- Which repository receives issues for Bazaar's own modules. That is doc 43 open question 1. This module's default is "no sink configured, nothing filed".
- The dependency watcher (doc 71). Section 10 item 4 states the signal shape this module needs from it, and nothing more.
- GitLab or other forge sinks. The sink interface permits them as later provider modules; the GitLab provider contract is doc 21's.
- Counsel questions on publishing redacted failure detail to a public repository (doc 43 question 3 stays open and applies here).
- The Kodo alert model. This module consumes Kodo incidents through a source adapter and does not add alert types to Kodo.

## 4. Existing code and docs this module reuses

State of the tree at this commit, checked by reading `src/`. Nothing below is duplicated by this design.

| What exists | Where | How this module uses it |
|---|---|---|
| Redaction layers, rules v1, `IFieldRedactor`, `Redactor`, `RedactionResult` | `src/Bazaar.Diagnostics/Redaction/` (doc 43 section 6) | Called on every outbound title and body, per field, before any sink sees it. Residual-scan failure holds the item (section 7). Not reimplemented. |
| Fingerprint (`fp1`) and `FingerprintInput` | `src/Bazaar.Diagnostics/Fingerprinting/` | The key for crash signals, unchanged. |
| Grouping rules: `GroupState`, `GroupTransition.Apply`, `GroupAction` (`FileIssue`, `QueueDeferred`, `AddComment`, `Reopen`, `RecordMismatch`), `ReleaseVersion`, comment thresholds 1, 10, 100, 1000, one comment per day, reopen only at or above the fixed version | `src/Bazaar.Diagnostics/Grouping/` | Crash signals are decided by `GroupTransition.Apply` as it is. Section 6.3 says how non-crash signals reuse the same thresholds without forking the logic. |
| Issue body renderer and hidden marker `<!-- bazaar-crash-fp: ... -->` | `src/Bazaar.Diagnostics/Grouping/IssueBody.cs` | The crash template is `IssueBody.Render`, unchanged. Other templates follow its rules (code-spans only, no control characters, no comment-breaking text). |
| Consent modes and settings keys (`telemetry.crash.*`) | `src/Bazaar.Diagnostics/Consent/` | Crash filing still honours `telemetry.crash.issue_repo`, `issues_per_day`, `mode`. Section 9.1 layers this module's consent on top and never loosens it. |
| App token provider `IGitHubTokenProvider`, `GitHubTokenScope`, `GitHubToken`, Identity Service client | `src/Bazaar.GitHub/Auth/` (doc 21 Tokens, doc 14) | The GitHub sink obtains every installation token here. No JWT code, no key handling, no token cache in this module. |
| `IGitHubRateState`, `GitHubResponseCache`, `GitHubAdapter` retry and `retry-after` handling | `src/Bazaar.GitHub/Adapter/` | The GitHub sink shares rate state so filing counts against the same installation budget as agent work (doc 21 rate limits). |
| `IAuditLog`, `AuditEntry(Ts, Actor, Kind, Payload)`, `ITransactionalAuditLog` | `src/Bazaar.Audit.Abstractions/` | All audit events (section 9). `Actor` is the canonical `Principal` text. |
| Module health contract `IModuleHealthContract`, `ModuleHealthCheck` | `src/Bazaar.Server.Modules.Abstractions/Health/` | Section 9.4. |
| `ILoopGuard`, `LoopEvent`, `GuardKind`, `GuardVerdict` | `src/Bazaar.Server.Agents.Abstractions/LoopGuard/` | The loop-guard source reads verdicts through this contract. |
| Kodo evaluator, probe results, module health results | `src/Bazaar.Kodo/` (doc 46) | The Kodo source module reads incident and probe state through Kodo's abstractions. Kodo gains no code for this. |
| `IGcLogWriter` and GC abstractions | `Bazaar.GC.Abstractions` (doc 40) | The GC sink posts through it. |
| Approvals and owner signature contracts | `Bazaar.Approvals.Abstractions` (doc 32 5a) | "Ask" mode approvals and consent changes that need the owner. |

What does **not** exist yet and is new here: a generic signal contract, the append-only ledger, the sink and source interfaces, a GitHub issue REST client (`IGitHubAdapter` has PR methods only), non-crash templates, per-source rate limits, per-scope consent records, and the client pane.

Overlap found and its handling: doc 43 section 5.4 gives crash groups their own tables (`crash_group` with `github_issue_number`). This module does not move or duplicate them. For crash signals, `crash_group` stays the authority for crash state; the ledger records filing decisions and issue references. See section 6.5 and open question 1.

## 5. Module map

All names follow `Bazaar.Server.<Name>` plus `.Abstractions` (doc 40 section 4). Each provider is its own module. Component ids are kebab-case; migration namespaces use underscores, as `ryusei-schedule` maps to `ryusei_schedule`.

| Module | Component id | Kind | Role | Depends on (abstractions only) |
|---|---|---|---|---|
| `Bazaar.Server.IssueFiler.Abstractions` | none (contracts) | contracts | `IIssueSignalSource`, `IIssueSink`, `IIssueTemplate`, signal and decision records, ledger reader | `Bazaar.Contracts`, Core (`Principal`) |
| `Bazaar.Server.IssueFiler` | `issue-filer` | feature | Pipeline, policy, consent, ledger, rate limits, hold queue, sync, Postgres store, settings, health | its abstractions, `Bazaar.Diagnostics`, `Bazaar.Audit.Abstractions`, `Bazaar.Approvals.Abstractions`, `Bazaar.Server.Modules.Abstractions` |
| `Bazaar.Server.IssueFiler.Sinks.GitHub` | `issue-filer-sink-github` | provider | GitHub issue create, comment, close, reopen, search by marker | abstractions, `Bazaar.GitHub` token and rate abstractions |
| `Bazaar.Server.IssueFiler.Sinks.Email` | `issue-filer-sink-email` | provider | Outward email notice per filing decision | abstractions, the email module's send contract (doc 22) |
| `Bazaar.Server.IssueFiler.Sinks.Gc` | `issue-filer-sink-gc` | provider | Post a summary into an operations GC | abstractions, `Bazaar.GC.Abstractions` |
| `Bazaar.Server.IssueFiler.Sources.Crash` | `issue-filer-source-crash` | provider | Crash groups from doc 43 | abstractions, `Bazaar.Diagnostics` |
| `Bazaar.Server.IssueFiler.Sources.Kodo` | `issue-filer-source-kodo` | provider | Kodo incidents, silent modules, failed probes | abstractions, Kodo abstractions (doc 46) |
| `Bazaar.Server.IssueFiler.Sources.Dependencies` | `issue-filer-source-deps` | provider | Dependency watcher findings (doc 71) | abstractions, doc 71's abstractions |
| `Bazaar.Server.IssueFiler.Sources.LoopGuard` | `issue-filer-source-loopguard` | provider | Agent loop-guard trips | abstractions, `Bazaar.Server.Agents.Abstractions` |
| `Bazaar.Client.IssueFiler` | `issue-filer-pane` | client-pane | Filed Issues pane and consent view | `Bazaar.Client.Core`, `Bazaar.Contracts` wire types |

Soft dependencies (`SoftDependsOn`, doc 40 section 6): the core soft-depends on `kodo` (health readiness) and `gc` (hold notifications). It hard-depends on `audit`. A missing source or sink degrades only the signals or destinations that name it. With no sink module enabled the core still records signals in the ledger as `held: no_sink`, so nothing is lost and nothing leaves the install.

Wire contracts for the pane go in `Bazaar.Contracts` under the namespace `Bazaar.Contracts.IssueFiler`, serialised through `IWireCodec` (JSON, source generated).

## 6. Design

### 6.1 Contracts (sketch)

```csharp
// Bazaar.Server.IssueFiler.Abstractions
namespace Bazaar.Server.IssueFiler;

/// One raw observation from a source. Sources never decide whether to file.
public sealed record IssueSignal(
    string SignalKind,               // "crash" | "kodo.incident" | "module.silent" | "dependency.finding" | "agent.loop_guard" | ...
    string SourceId,                 // component id of the source module
    string Fingerprint,              // opaque, stable per underlying problem, never per occurrence
    SignalScope Scope,               // who this is about (section 6.4)
    Severity Severity,               // Info | Warning | Error | Critical
    string Summary,                  // short plain text, not yet redacted
    IReadOnlyDictionary<string, string> Facts,   // scalar facts only; values pass the redactor
    string? Version,                 // product or module version, SemVer, when relevant
    DateTimeOffset ObservedAt);

public readonly record struct SignalScope(string Kind, string Id);   // Kind: install | tenant | user (generic Scope, CONVERGENCE A2)

public interface IIssueSignalSource
{
    string SourceId { get; }
    string SignalKind { get; }
    /// Push: the source calls this when it observes something. Pull: the host calls PollAsync on a schedule.
    IAsyncEnumerable<IssueSignal> PollAsync(DateTimeOffset since, CancellationToken ct);
}

public interface IIssueSignalIntake
{
    /// Idempotent per (Fingerprint, ObservedAt, SourceId). Never throws for a refused signal; returns the decision.
    ValueTask<IntakeResult> SubmitAsync(IssueSignal signal, Principal submittedBy, CancellationToken ct);
}

/// A sink is a destination. It knows nothing about consent, rate limits or dedupe; the core has done those.
public interface IIssueSink
{
    string SinkId { get; }                 // component id, for example "issue-filer-sink-github"
    IssueSinkCapabilities Capabilities { get; }   // Create | Comment | Close | Reopen | FindByMarker | ReadState
    ValueTask<SinkResult> CreateAsync(SinkTarget target, RenderedIssue issue, CancellationToken ct);
    ValueTask<SinkResult> CommentAsync(SinkRef issue, string body, CancellationToken ct);
    ValueTask<SinkResult> ReopenAsync(SinkRef issue, string comment, CancellationToken ct);
    ValueTask<SinkResult> CloseAsync(SinkRef issue, CloseReason reason, string? comment, CancellationToken ct);
    ValueTask<SinkRef?> FindByMarkerAsync(SinkTarget target, string marker, CancellationToken ct);
    ValueTask<SinkIssueState> ReadStateAsync(SinkRef issue, CancellationToken ct);   // live; never cached into the ledger
}

public sealed record SinkTarget(string SinkId, string Destination);   // GitHub: "owner/name"; email: address id; GC: gc id
public sealed record SinkRef(string SinkId, string Destination, string ExternalId, string? Url);
public sealed record SinkResult(bool Ok, SinkRef? Ref, SinkFailure? Failure);   // errors are data (Resibo discipline), never exceptions for expected failures
public sealed record SinkFailure(string Code, string Message, bool Retryable, TimeSpan? RetryAfter);

public sealed record RenderedIssue(string Title, string Body, string Marker, IReadOnlyList<string> Labels);

/// Pure. Same input, same output. Versioned so a template change is visible in the ledger.
public interface IIssueTemplate
{
    string SignalKind { get; }
    string TemplateVersion { get; }
    RenderedIssue Render(IssueSignal signal, GroupSummary group, string filedBy);
}

/// Read-only ledger access for the pane, Kodo and tests.
public interface IIssueLedgerReader
{
    IAsyncEnumerable<LedgerEntry> ReadAsync(LedgerQuery query, CancellationToken ct);
    ValueTask<LedgerProof> ProveAsync(long seq, CancellationToken ct);   // chain position and head, section 6.2
}
```

Design notes: `IssueSignal.Facts` is scalar-only for the same reason doc 43's log property allow-list is: objects and arrays are dropped, and each value passes `IFieldRedactor` at the `FreeText` class before it can reach a template. A template may only print facts it declares.

### 6.2 The ledger (taken from Resibo, hardened)

Resibo's `FiledIssueLedger` is described as a persisted, append-only record of every issue the install has filed, deduplicated by fingerprint and never overwriting an existing entry (`core/telemetrees/CLAUDE.md`). Bazaar keeps the idea (append-only, queryable, the honest record of what left the install) and changes the shape: one row per decision, not per issue, in Postgres, hash-chained. That is what lets "reopened", "held" and "refused" be shown honestly, not only "filed".

```sql
-- Namespace: src/Bazaar.Server.IssueFiler/Data/Sql/issue_filer/0001_ledger.sql
create table issue_ledger (
  seq          bigint generated always as identity primary key,
  at           timestamptz not null,
  fingerprint  text not null,
  signal_kind  text not null,
  source_id    text not null,
  event        text not null,       -- received | held | refused | filed | commented | reopened | closed_seen |
                                    -- deleted_seen | deduped_existing | deferred | consent_changed | sync_failed
  sink_id      text null,
  destination  text null,
  external_id  text null,           -- issue number, message id, GC message id
  url          text null,
  template_version text null,
  reason       text null,           -- machine code, for example 'rate_cap_day', 'consent_off', 'residual_scan'
  actor        text not null,       -- Principal canonical text, e.g. 'service:issue-filer'
  detail_hash  bytea null,          -- SHA-256 of the exact redacted body sent; the body itself is not stored here
  prev_hash    bytea not null,
  row_hash     bytea not null
);
create unique index issue_ledger_filed_once
  on issue_ledger (fingerprint, sink_id, destination)
  where event = 'filed';
-- Append-only: triggers reject UPDATE and DELETE. Correction is a new row.
```

- **Hash chain.** `row_hash = SHA-256(canonical fields || prev_hash)`, the same recipe as the Kodo journal (doc 46 section 6.5). The ledger rides on the audit checkpoints of doc 48 section 5.2 where available: the chain head is included in the audit checkpoint as a registered chain, so anchoring and Merkle proofs are inherited and not rebuilt (OPEN: confirm with the audit owner, question 5).
- **What is not stored.** The redacted body is not kept in the ledger. Its hash is, so an auditor can match a sink's content to the ledger. The body lives at the sink, and a short-lived copy lives in `issue_hold` only while held.
- **Live state is never cached as truth.** Resibo's rule, kept: open or closed and linked PRs are read from the sink on demand (`ReadStateAsync`) with a `checked_at`, and the pane shows the age. A cached projection (`issue_ref`, section 6.5) exists to drive the pipeline, and the pane labels it with its last sync time.

### 6.3 The decision pipeline

```
 source -> intake -> normalise -> redact -> group/decide -> consent and policy -> rate limit -> dedupe -> sink
                         |          |           |                  |                 |            |
                      ledger     ledger      ledger             ledger            ledger       ledger
```

1. **Intake.** `SubmitAsync` validates the signal shape and size, drops duplicate deliveries (same fingerprint, source and `ObservedAt`), and appends `received`.
2. **Redact.** Every string in the signal is redacted through `IFieldRedactor` (class `FreeText` for summaries and facts, `Identifier` for ids). A `Failed` or residual-scan result moves the signal to `issue_hold` with reason `residual_scan` and appends `held`. It is never sent. This is a fail-closed rule and not a setting.
3. **Group and decide.**
   - Crash signals: the source module hands the core the group as `GroupState` plus the `Occurrence`; the core calls `GroupTransition.Apply` and carries out the returned `GroupAction`s. No logic is forked.
   - Other signals: `SignalGroupTransition` (in the core) applies the same rules to a generic group (`first_seen`, `last_seen`, `occurrence_count`, issue references): file when none exists and the budget allows, defer when it does not, comment at `GroupTransition.CommentThresholds` at most once per UTC day, reopen when the signal's `Version` is at or above the closed issue's fixed version (`ReleaseVersion`). The thresholds and `ReleaseVersion` are read from `Bazaar.Diagnostics`, so the two paths cannot drift. Signals with no `Version` (a Kodo incident, a silent module) reopen when the previous issue was closed with `reason = fixed` and the new occurrence starts after the close time by at least `issuefiler.reopen_quiet_minutes`. Whether to generalise `GroupState` in `Bazaar.Diagnostics` instead of keeping a second transition is OPEN (question 1).
4. **Consent and policy** (section 9.1). No consent for the signal's scope and sink means `held: consent_off` and nothing leaves. `ask` means the draft goes to the hold queue for a `Principal` with authority to approve (section 9.2).
5. **Rate limit** (section 6.6). A rejected action is `deferred`, not dropped, and retries when the window opens.
6. **Dedupe against existing open issues** (section 6.7).
7. **Sink call.** Success appends `filed`, `commented` or `reopened` with the external reference. A retryable failure appends `sync_failed` and schedules a retry with backoff. A non-retryable failure holds the item with the failure code.

Everything between 1 and 7 is deterministic given the signal, the stored state, the settings and the injected clock. The only model-touching step in the whole module is the optional "unverified diagnosis" attachment from doc 43 section 9, which this module only forwards as a separately labelled comment when doc 43's own setting allows it.

### 6.4 Scope and Principal

`SignalScope` uses the generic `Scope` of `CONVERGENCE.md` A2 (install, tenant, user; owner card, 2026-11-19), mapped by Bazaar as: install = the company server, tenant = reserved (see question 4), user = one human member. Agents are never a scope. They appear only as `Principal`s: `agent:<id>` when an agent's behaviour is the signal (loop-guard), `service:issue-filer` for the module's own acts, `system:<x>` for scheduled sweeps, `user:<id>` for a human decision.

### 6.5 State tables

```sql
create table issue_group (            -- projection for non-crash signals; crash groups stay in crash_group (doc 43)
  fingerprint     text primary key,
  signal_kind     text not null,
  scope_kind      text not null,
  scope_id        text not null,
  occurrence_count bigint not null default 0,
  first_seen_at   timestamptz not null,
  last_seen_at    timestamptz not null,
  first_seen_version text null,
  last_seen_version  text null,
  last_comment_date date null,
  status          text not null           -- open | filing_deferred | resolved_in | ignored
);

create table issue_ref (              -- where a group is filed, per sink
  fingerprint     text not null,
  sink_id         text not null,
  destination     text not null,
  external_id     text not null,
  url             text null,
  closed          boolean not null default false,
  fixed_in_version text null,
  last_synced_at  timestamptz not null,
  primary key (fingerprint, sink_id, destination)
);

create table issue_hold (             -- items waiting for a person or for a retry
  hold_id         uuid primary key,
  fingerprint     text not null,
  reason          text not null,          -- consent_ask | residual_scan | no_sink | rate_cap | sink_failed
  rendered_title  text not null,
  rendered_body   text not null,          -- already redacted; deleted when released or after hold_retention_days
  created_at      timestamptz not null,
  expires_at      timestamptz not null,
  decided_by      text null,              -- Principal text
  decision        text null               -- approved | rejected | expired
);

create table issue_consent (          -- current consent; every change also appends consent_changed to the ledger
  scope_kind      text not null,
  scope_id        text not null,
  signal_kind     text not null,
  sink_id         text not null,
  mode            text not null,          -- off | ask | auto
  set_by          text not null,          -- Principal text
  set_at          timestamptz not null,
  primary key (scope_kind, scope_id, signal_kind, sink_id)
);

create table issue_rate_window (
  bucket          text not null,          -- e.g. 'day:all', 'hour:signal:kodo.incident', 'min:sink:issue-filer-sink-github'
  window_start    timestamptz not null,
  count           int not null,
  primary key (bucket, window_start)
);
```

Migrations sit in the module's own namespace `issue_filer`, never in frozen `common/` (`BAZAAR-COMPONENTS.md` section 5). Numbers are local to the namespace. The module writes no other module's table. For crash signals the module reads `crash_group` only through the doc 43 contract exposed by `Sources.Crash`.

### 6.6 Rate limits

All limits are settings (section 8) with safe defaults; none may be set above the floors owned by docs 15 and 21.

| Limit | Default | Behaviour on breach |
|---|---|---|
| New issues per UTC day, all sources | 20 (`issuefiler.issues_per_day`) | Remaining new groups become `filing_deferred`; ledger `deferred`; shown in the pane. Crash signals also respect `telemetry.crash.issues_per_day`, whichever is lower. |
| New issues per source kind per hour | 5 (`issuefiler.per_kind_per_hour`) | A flood from one source (for example a dependency finding burst) cannot use the whole day's budget. |
| Comments per group per UTC day | 1 | From `GroupTransition`; not a setting for crash, same value for others. |
| Comment thresholds | 1, 10, 100, 1000 | Constant shared with `Bazaar.Diagnostics`. |
| GitHub writes | doc 15 `security.quota.github_writes_per_minute`, doc 21 `github.writes_per_hour` | The GitHub sink counts against these. 403 and 429 honour `retry-after`. Repeated breaches pause the sink and notify the owner. |
| Public-repo posts | doc 15 `security.quota.public_items_per_platform_day` and per-repo caps | Applies when the destination is a repository outside owner-controlled orgs; such destinations also need the allowlist (doc 21). |
| Burst breaker | 10 sink failures in 5 minutes opens the sink for 15 minutes | Items wait in `issue_hold` with reason `sink_failed`. |

Deferred items drain oldest-first when a window opens. A deferred group that recurs keeps counting occurrences, so its eventual issue shows the true count.

### 6.7 Deduplication, reopen and close

- **Primary dedupe is the ledger and `issue_ref`**, by fingerprint, so a recurrence costs no search quota (doc 43 section 8.4). This is Resibo's "fingerprint the signal, not the occurrence" rule.
- **Hidden marker.** Every created issue carries `<!-- bazaar-issue-fp: <fingerprint> -->` (crash issues keep the existing `bazaar-crash-fp` marker via `IssueBody`). The marker is what lets the system recover.
- **Dedupe against issues that already exist but are not in the ledger.** Before `CreateAsync`, if no `issue_ref` exists for the fingerprint and the sink advertises `FindByMarker`, the core calls `FindByMarkerAsync` once. A hit becomes `deduped_existing` (an `issue_ref` is adopted, a comment is added if due) and no new issue is created. This covers restore from backup, a ledger loss, and a second install pointed at the same repository. It spends one search call per new fingerprint only, never on recurrences. GitHub's search quota applies and is shared (doc 21).
- **Closed with a fixed version, recurrence at or above it:** reopen with a comment naming both versions (the `Reopen` action). **Recurrence from an older version:** count only. **Closed without a fixed version:** no reopen by version; the quiet-period rule in 6.3 applies for versionless signals; the default for versionless closes is "comment, do not reopen" (`issuefiler.reopen_versionless`, default `off`, question 3).
- **Resibo's gap, fixed.** Resibo's docs say it never re-files an already-open issue and describe no handling of a problem that returns after the issue closes. Bazaar reopens (or, when the issue was deleted by hand, files a new one and records the new reference), as doc 43 section 8.4 already specifies for crashes.
- **Sync.** A scheduled job (`issuefiler.sync_interval_minutes`, default 60) reads live state for open `issue_ref`s through `ReadStateAsync`, records `closed_seen` or `deleted_seen`, and updates `closed` and `fixed_in_version` (parsed from a configured label or closing comment convention, OPEN: question 3). Resibo's status client (documented as unauthenticated, read-only, open/closed plus linked PRs through GitHub's Timeline API; `core/telemetrees/CLAUDE.md`) is the model for the read side. Reading goes through the same GitHub token path, so private repositories work. Linked fix-PR numbers are shown in the pane with `checked_at`, answering Resibo's "is a fix PR already developing" requirement.

### 6.8 Templates

One `IIssueTemplate` per signal kind, versioned. A template is a pure function; its output is deterministic and covered by golden tests. Shared rules (taken from `IssueBody`): fingerprint marker first, a line that says the issue was filed automatically by the named install and redacted, facts only inside code spans, control characters and comment terminators neutralised, title at most 120 characters, body size cap (`issuefiler.max_body_bytes`, default 16384).

| Signal kind | Title | Body contains | Never contains |
|---|---|---|---|
| `crash` | `IssueBody.Render` as built | Section 8.5 of doc 43 | logs, screenshots, device or person identifiers |
| `kodo.incident` | `Incident: <probe or module> <state>` | probe id, module id and version, state word, first and last seen, duration, count, link to the incident in the Monitor pane by id only | host names, paths, log lines, key ids |
| `module.silent` | `Module not responding: <module id>` | module id, version, which check failed (liveness or readiness), since when, restart count | stack traces, config values |
| `dependency.finding` | `Dependency update: <name> <old> to <new>` | name, versions, fact kind, source URL of the public release or advisory, compatibility note | local paths, private repo names |
| `agent.loop_guard` | `Loop guard tripped: <guard kind>` | guard kind, thresholds crossed, counts, time window, module and prompt-pack version | agent names, GC ids, message text, tool arguments (see section 10) |

Templates for new signal kinds ship with their source module, so the core never needs to know a kind.

## 7. Redaction

Resibo's CLAUDE.md says "any diagnostic data leaving an install is scrubbed of receipt and financial content first" but its docs describe no redaction mechanism. Bazaar already has the implementation in `Bazaar.Diagnostics` (doc 43 section 6). This module wires it in as a gate and adds no rules of its own.

- **Gate.** No `RenderedIssue` reaches a sink unless every field passed `IFieldRedactor`, the residual scan over the whole rendered title and body found nothing, and the result status is not `Failed`. This is a floor, not a setting.
- **Pluggable rules.** Operators may add patterns (`telemetry.crash.redaction_extra_patterns` is reused as `issuefiler.redaction_extra_patterns` for non-crash signals; question 2 asks whether to share one key). A program built on the same libraries (for example Resibo, with receipt amounts and tax ids) adds rules through the same hook, matching `CONVERGENCE.md` A6. A built-in rule can never be removed.
- **Facts are allow-listed.** A template prints only declared fact keys. An undeclared key is dropped and counted, as in doc 43 section 5.3.
- **Server-side only for non-crash signals.** Crash reports are already redacted on the device. Other sources originate on the server, so redaction runs once here. The re-check at ingestion in doc 43 section 12.1 still applies to crash signals.
- **Test.** A canary planted in every fact of every source kind must be absent from every sink's recorded request body (section 15).

## 8. Settings registry rows

Scope is `install` unless noted. All changes are audited. "Approval" means an owner-signed approval (doc 15 Tier 2). Defaults are the most protective option, so a fresh install files nothing.

| Key | Type | Default | Who may change | Approval |
|---|---|---|---|---|
| `issuefiler.enabled` | bool | `false` | operator (owner) | Yes |
| `issuefiler.default_mode` | enum `off`, `ask`, `auto` | `off` | operator (owner) | Yes, owner, for `auto` |
| `issuefiler.issues_per_day` | int, 0 to 200 | 20 | operator | No |
| `issuefiler.per_kind_per_hour` | int, 0 to 50 | 5 | operator | No |
| `issuefiler.hold_retention_days` | int, 1 to 30 | 7 | operator | No |
| `issuefiler.sync_interval_minutes` | int, 5 to 1440 | 60 | operator | No |
| `issuefiler.reopen_quiet_minutes` | int, 0 to 10080 | 60 | operator | No |
| `issuefiler.reopen_versionless` | enum `off`, `on` | `off` | operator | Yes, owner, for `on` |
| `issuefiler.max_body_bytes` | int, 1024 to 65536 | 16384 | operator | No |
| `issuefiler.redaction_extra_patterns` | list of pattern entries | empty | operator | No (tightening only) |
| `issuefiler.sink.github.destination` | string `owner/name` or empty | empty | operator | Yes, owner (public repo, doc 43 question 1) |
| `issuefiler.sink.github.labels` | list | `bazaar-auto` | operator | No |
| `issuefiler.sink.email.to` | list of address ids | empty | operator | Yes, owner |
| `issuefiler.sink.gc.target` | gc id or empty | empty | operator | No |
| `issuefiler.source.<kind>.enabled` | bool per source | `false` | operator | Yes for `agent.loop_guard` |
| `issuefiler.consent.show_in_dashboard` | bool | `true` | operator | No |

**Floors (not settings).** Redaction and the residual scan are always on. A signal with no consent for its scope and sink files nothing. Agents cannot read or change these settings or approve a hold; a role that carries human approval cannot be granted to an agent (doc 48 section 4.1). No agent text or tool output is placed in an issue body except as an allow-listed, redacted fact. The module never holds a GitHub private key. Writes to repositories outside owner-controlled orgs also need doc 21's allowlist. Settings may tighten these, never loosen them.

Save-time validation refuses: `enabled = true` with no sink configured; `default_mode = auto` with a sink destination in a repository outside owner-controlled orgs; a per-kind cap above the day cap; `sink.email.to` set with `mode = auto` and no rate cap.

## 9. Consent, audit, health

### 9.1 Consent per Principal scope (opt-in)

Resibo's opt-in is documented as one boolean per install (`telemetrees_opt_in`, default false, stored in a local config file), plus an `is_configured()` gate under which an install with no GitHub App credentials files nothing (`core/telemetrees/CLAUDE.md`). Bazaar keeps both ideas and makes consent a record keyed by scope, signal kind and sink, with a `Principal` as the decider:

- A consent row `(scope_kind, scope_id, signal_kind, sink_id) -> mode` exists only if a `Principal` with the right role set it. `install` scope: owner or a role with `issuefiler.consent.install`. `user` scope: that user (a person controls whether signals about themselves leave the install, for example client crash groups from their device). No row means `off`.
- Effective mode is the most restrictive of: the matching consent row, `issuefiler.default_mode`, and for crash signals `telemetry.crash.mode` and `telemetry.crash.issue_repo` (empty repository means no GitHub filing). A person's `off` is never overridden by an install-level `auto`.
- `ask`: the redacted draft goes to `issue_hold` with reason `consent_ask`, an inbox item (kind `approval`, doc 06) goes to the approver, and the draft is sent only after approval by a `Principal` that is `user` kind. An agent cannot approve (floor above).
- Zero-network gate: with `enabled = false`, or no consent row, or no sink, the module makes no outbound call at all. A test uses a network spy (section 15). Resibo's docs state the same intent: an unconfigured install "files nothing, ever".
- Consent changes append `consent_changed` to the ledger and an audit entry. Raising a mode to `auto` needs owner approval.

### 9.2 Audit events

Every event goes to the hash-chained audit log (`ITransactionalAuditLog` where a change exists in the same transaction, doc 48 section 5). `Actor` is the canonical `Principal` text. Rows carry ids, hashes and counts, never content.

| Event kind | Actor example | Payload (no content) |
|---|---|---|
| `issuefiler.signal.received` | `service:issue-filer` | signal kind, source id, fingerprint, scope |
| `issuefiler.signal.refused` | `service:issue-filer` | reason code (`schema`, `size`, `duplicate`) |
| `issuefiler.hold.created` / `.released` / `.expired` | `service:issue-filer`, `user:dom` | hold id, reason, decision |
| `issuefiler.issue.filed` / `.commented` / `.reopened` | `service:issue-filer` | sink id, destination, external id, template version, body hash |
| `issuefiler.issue.deduped_existing` | `service:issue-filer` | sink id, external id |
| `issuefiler.issue.closed_seen` / `.deleted_seen` | `system:sync` | sink id, external id |
| `issuefiler.consent.changed` | `user:dom` | scope, signal kind, sink id, old and new mode |
| `issuefiler.setting.changed` | `user:dom` | key, old and new value (registry rules) |
| `issuefiler.sink.paused` / `.resumed` | `service:issue-filer`, `user:dom` | sink id, reason |
| `issuefiler.redaction.held` | `service:issue-filer` | fingerprint, counts (no matched text) |
| `issuefiler.ledger.read` | `user:dom` | range, count (doc 48 reads rule) |

### 9.3 Settings and consent for agents

Agents are not scopes and cannot change any `issuefiler.*` value. When an agent is the subject of a signal (loop-guard), the signal's `Facts` never include its name, GC or text. See section 10.

### 9.4 Kodo health contract

The core implements `IModuleHealthContract` (doc 46 section 10, `Bazaar.Server.Modules.Abstractions`), and `Bazaar.Kodo.Testing`'s `ModuleHealthContractTests<TModule>` runs in its test project.

- **Liveness:** the worker loop heartbeat; no I/O.
- **Readiness:** the ledger store is reachable (cached), the audit sink is registered, and at least one enabled sink exists or the module is intentionally idle (`issuefiler.enabled = false` reports `Pass` with reason `disabled`). Hard dependencies: `audit`. Soft: `kodo`, `gc`.
- **Self-test:** `Quick` renders each template over a fixture and runs the redactor over a planted canary. `Full` appends and reads back a ledger row inside a rolled-back scope, verifying the chain, with `LeftNoResidue = true`.
- **Probes** added to the catalogue (signed catalogue, so an update, not a setting): `issuefiler.queue_depth` (holds and deferred, non-critical, amber above a setting), `issuefiler.sink.<id>.reachable` (non-critical, external pool, via `ReadStateAsync` on a known reference; never creates anything), `issuefiler.ledger_chain` (critical for integrity, sampled chain verification), `issuefiler.oldest_hold_age` (non-critical). Kodo remains a signal source for this module and this module a probe target of Kodo; the two are soft edges and may form a cycle without setting start order (`BAZAAR-COMPONENTS.md` 4.1).

## 10. Signal sources

Each source is its own provider module (section 5). A source maps its domain events to `IssueSignal` and nothing else.

1. **Crash reports (`Sources.Crash`).** From doc 43's ingestion output (group state and occurrence). It is the only source that supplies a `GroupState`. Fingerprint is the server's `fp1` value. It forwards the "unverified diagnosis" text only as a separate, labelled comment when `telemetry.crash.issue_diagnosis_post` is `on`.
2. **Kodo incidents (`Sources.Kodo`).** Incident entries and sustained red probe results from Kodo's read contract. Fingerprint `kodo:<probe or category>:<module id or scope>`, never per occurrence. Only states Kodo calls `failing` or an incident open longer than `issuefiler.source.kodo.min_open_minutes` (default 30) file, so flapping does not create issues; amber states never do. Kodo's own state is the authority; this source cannot change it.
3. **Silent services and modules (inside `Sources.Kodo`, signal kind `module.silent`).** This is Resibo's one wired detector (Health Watchdog `GetSilentServices`: a service stopped sending kicks), re-expressed for Bazaar. Bazaar has no kick registry. Its equivalent is the module health contract: a module whose `LivenessAsync` fails or whose readiness stays `Fail` or `Unknown` past its unknown budget (doc 46 section 8.1). Fingerprint `module.silent:<module id>` (Bazaar's own shape). In Resibo the detector turns the Watchdog's silent-service signal into a filing directly; here, the read is Kodo's, and filing is this module's.
4. **Dependency watcher findings (`Sources.Dependencies`).** Doc 71 does not exist at the time of writing. This document assumes only: a finding has a dependency name, a fact kind, old and new values, a public source URL and an observed time, and the watcher's own first-observation-is-not-a-change rule (Resibo `dependencies_warden`) is applied before it reaches here. Fingerprint `dependency:<name>:<fact kind>:<new value>`, so each new release is a new group while repeats of the same finding dedupe. Which findings are worth an issue and which go only to a changelog is doc 71's decision, not this module's. UNVERIFIED until doc 71 lands; question 6.
5. **Agent loop-guard trips (`Sources.LoopGuard`).** From `ILoopGuard` verdicts that trip (doc 06). A trip already goes to the Psych Ward (CLAUDE.md, loop-guard trips go to the Psych Ward); an issue is a separate, off-by-default outward report that a guard is mis-tuned or a prompt pack loops, for the maintainers of the platform. The body is aggregated: guard kind, thresholds, counts, time window, and the agent-runtime and prompt-pack versions. It carries no agent identity, GC, text or tool arguments, because those are the private working memory of an agent and may carry untrusted input. Fingerprint `loop_guard:<guard kind>:<runtime version>`. `issuefiler.source.agent.loop_guard.enabled` defaults to `false` and needs owner approval to enable. Whether the Psych Ward must be notified first is question 4.

New sources are added by a new module; the core is never edited.

## 11. What is taken from Resibo and what is changed

Resibo sources cited are Resibo docs only (its `docs/apis/` deep-dives and per-package `CLAUDE.md` files).

| Resibo item | Resibo source | Taken as | Changed for Bazaar, and why |
|---|---|---|---|
| Fingerprint the signal, not the occurrence; dedupe is load-bearing | `core/telemetrees/CLAUDE.md`, `docs/apis/v3-deepdive-28-telemetrees-api.md` | Rule 6.7 and the fingerprint contract on `IssueSignal` | Crash fingerprints stay doc 43's `fp1`. Other kinds use `kind:scope` text. Bazaar adds reopen on recurrence; Resibo's docs describe only never re-filing an already-open issue. |
| Append-only ledger of filed issues | `core/telemetrees/CLAUDE.md` (`FiledIssueLedger`: persisted, append-only, fingerprint-deduplicated) | Append-only, never overwrite, fingerprint-keyed (6.2) | Postgres, one row per decision (not per issue), hash chain, partial unique index for `filed`. A JSON file in a config folder cannot be chained or queried concurrently and would sit outside the audit story (doc 48). |
| Live status read, never cached into the record, with `checked_at` | `core/telemetrees/CLAUDE.md` (status client, `checked_at`) | `ReadStateAsync` and the pane's age label (6.7) | Authenticated through the Bazaar token provider so private repositories work; Resibo's client is documented as unauthenticated. |
| GitHub App filing: App-owned, scoped to issues only, short-lived installation token | `core/telemetrees/CLAUDE.md`, `docs/apis/v3-plan-01-core-apis.md` item 27 | The model: App, not a PAT, issues only, installation token | **Not copied.** Resibo's docs say it mints the RS256 JWT itself and keeps App credentials in `<install_root>/telemetrees/github_app.json`. Bazaar forbids that: the Identity Service holds keys and mints tokens (doc 14, doc 21). The sink calls `IGitHubTokenProvider`. A system App with issues-write on one repository is the doc 43 section 14 conflict row, still for the owner to register. |
| `is_configured()` gate: unconfigured means zero network calls | `core/telemetrees/CLAUDE.md` | Zero-network gate (9.1) | Configuration is a settings and consent record, not a file; and "configured" is not "consented". |
| Opt-in flag default false | `telemetrees/CLAUDE.md` (`telemetrees_opt_in`) | Default `off` | Per scope, signal kind and sink, with the Principal who decided it recorded; and doc 43's off/ask/auto vocabulary. |
| Silent-service detector | `core/telemetrees/CLAUDE.md` | The `module.silent` source (10.3) | Reads Kodo's module health, since Bazaar has no kick registry. |
| Three more planned sources (soft degradation, failed rollout, pre-release features) | `docs/apis/v3-plan-01-core-apis.md` item 27, `core/telemetrees/CLAUDE.md` | Kodo incidents, dependency findings (and doc 49 update failures, section 17 question 6) | Resibo's CLAUDE.md says one of four is wired; Bazaar designs all as modules. |
| Dependencies Warden | `core/telemetrees/CLAUDE.md`, `docs/apis/v3-deepdive-37-dependencies-warden.md` | Only its findings as a signal source | The watcher itself is doc 71. |
| Redaction as a stated rule | `telemetrees/CLAUDE.md` | Gate (section 7) | Implemented with doc 43's `Bazaar.Diagnostics` redactor. |
| Filing never raises, errors are data (PRINCIPLES P4.1) | `docs/PRINCIPLES.md` | `SinkResult` with `SinkFailure` | Same discipline; retryability and `retry-after` are explicit. |
| `DetectAndFileIssues` RPC, not invoked by anything | `telemetrees/CLAUDE.md` | Scheduled and event-driven intake from the start | Resibo calls its own gap "not yet invoked on a timer". Bazaar's sources push or are polled by the module's worker. |
| TUI `filed_issues_screen` | `telemetrees/CLAUDE.md` | `Bazaar.Client.IssueFiler` pane (12) | Avalonia, keyboard-first, mnemonic, screen-reader labels; renders what the server computes. |

Conflicts and how each is kept apart, per the owner's direction:

1. **Key custody.** Resibo: private key in a local JSON file. Bazaar: Identity Service. Not both in one module. Bazaar's rule is a floor. Should Resibo run on this library later, its host supplies its own token provider; no file-based provider is shipped in Bazaar. If the owner wants a standalone file-based provider for non-Bazaar programs, it is a separate module outside this repository.
2. **Consent shape.** Resibo: one boolean per install. Bazaar: per scope. The boolean is the degenerate case (one install-scope row), so both fit one implementation.
3. **Re-file after close.** Resibo's docs state no recurrence handling after close; Bazaar reopens. Resibo's documented behaviour (no re-file of an open issue; nothing stated for a closed one) is approximated by `issuefiler.reopen_versionless = off` plus a group `ignored` status, so it is a configuration of the same module and does not need a second one.
4. **Storage.** Resibo JSON file and `install_root` paths; Bazaar Postgres and the Vault rule (Postgres holds structured state). Not combined.

## 12. Client module

`Bazaar.Client.IssueFiler` (component `issue-filer-pane`), registered through the pane registry (doc 41, doc 40 step 7). It renders what the server computes; it never calls GitHub.

- **Filed Issues view** (Resibo's `filed_issues_screen`): one row per group: signal kind, title, sink and destination, issue link, current state with `checked_at`, linked fix-PR numbers when known, occurrence count, first and last seen. Filterable by kind, sink, state.
- **Holds view:** drafts waiting on consent or on a retry, with the exact redacted text. Approve or reject needs a role and, for approval, an owner signature on Windows and Android (doc 32).
- **Consent view:** the effective mode per scope, signal kind and sink, and why (which setting wins). Edits go through the settings registry.
- **Ledger view:** the decision log with the chain position and a "prove" action (hash proof from `IIssueLedgerReader.ProveAsync`).
- Keyboard: mnemonics registered through doc 41's command registry (proposed `ifiled`, `iholds`, `iconsent`, `iledger`), no Ctrl chords, nothing focused by default, Approve is never the default action. Screen-reader names for every row and state word; colour is never the only signal. Terminal head (Calcinato) gets `:issues list`, `:issues holds`, `:issues approve N`.
- Wire messages in `Bazaar.Contracts.IssueFiler`, with the snapshot-plus-revision pattern of doc 23. Offline: the last snapshot with its age.

## 13. Interaction with existing modules

- **Doc 43 and `Bazaar.Diagnostics`.** The pipeline from capture to grouping is unchanged. After this module ships, doc 43's step "apply the filing rules (8.4)" is done by this module's `Sources.Crash` plus core, and the `github_issue_number` bookkeeping moves to `issue_ref`. Until then, doc 43's inline path stays. Doc 43 and this doc must not both file: a single switch `issuefiler.source.crash.enabled` makes the crash path pass through this module, and doc 43's inline filing is disabled when it is on (OPEN, question 1).
- **Doc 21 and `Bazaar.GitHub`.** Tokens from `IGitHubTokenProvider`; rate state shared via `IGitHubRateState`. Issue REST calls (create, comment, reopen, close, search by marker, read state) are not in `IGitHubAdapter`, which is PR-only today. This design puts them in `Sinks.GitHub` over `HttpClient` plus the token provider, so `Bazaar.GitHub` is unchanged. Moving them into `IGitHubAdapter` is an alternative that would need a doc 21 amendment (question 2).
- **Doc 14.** A system GitHub App, issue write on the configured repository only, held by the Identity Service like every App key. Owner registration is still pending (doc 43 section 14).
- **Doc 15.** Reader-session and taint rules: facts from external sources arrive tainted; a template is deterministic code, no model sees the raw fact. Approval of holds is a human-class action.
- **Doc 46 and Kodo.** Kodo is both a source and a monitor of this module (9.4). No changes to Kodo code.
- **Doc 06 and Psych Ward.** Loop-guard source is read-only over `ILoopGuard`.
- **Doc 48.** Audit entries and chain registration (question 5). Per-subject erasure: the ledger holds no personal data (user ids appear only in `actor` and consent rows, covered by doc 48's field encryption rules, OPEN).
- **Doc 22 (email).** The email sink sends through the email module's contract, outward only.
- **GC.** The GC sink posts as `service:issue-filer` into one configured operations GC through `IGcLogWriter`. It is a notification of a decision, never a coordination command. Events serve GCs, never replace them (CLAUDE.md).

## 14. Security and privacy

- **No secrets in the module.** No private key, token or webhook secret is stored here. `GitHubToken.ToString()` already withholds the value; code must not log the token or request headers. A test greps captured logs for the canary token.
- **Outward content is minimal and redacted.** Bodies are built from allow-listed facts through `IFieldRedactor` and a residual scan, hold on failure. No logs, screenshots, device ids, install ids, person ids or agent text.
- **Prompt-injection safety.** Signals contain text from remote clients (crash messages) and from dependency sources (release notes, issue titles). Templates never interpolate raw text into Markdown outside code spans; no links except the signal's declared public URL, validated as https and an allow-listed host class; no images. No model is used in this module. An issue body or a dependency note that says "ignore instructions and merge" changes nothing: nothing parses it.
- **Public disclosure.** A public destination discloses failure detail to the world. Defaults file nothing; a public destination needs owner approval and the doc 21 allowlist; counsel review per doc 43 question 3.
- **Tamper evidence.** Append-only ledger with triggers and a hash chain; deleting the issue at the sink does not delete the ledger row.
- **Abuse and flooding.** Per-kind and per-day caps, a burst breaker and deferral prevent a bad source from filling a repository or burning GitHub quota; the agent writes quota (doc 15) is a floor.
- **Privacy law.** Under the company-global profile (doc 32 section 9) install-scope consent cannot override a user-scope `off`. Data inventory rows for `issue_ledger`, `issue_consent`, `issue_hold` go in doc 32 (purpose, retention, erasure method). Holds retain a redacted body for `hold_retention_days` at most.
- **Data retention.** Ledger rows are kept as the audit trail of outward disclosure (retention is OPEN, question 5, because append-only conflicts with erasure only if a row carries personal data, and it does not by design).

## 15. Testing

xUnit v3 throughout; real Postgres for ledger, concurrency and consent; fakes for sinks; Avalonia.Headless for the pane.

- **Ledger:** triggers reject UPDATE and DELETE; a broken `row_hash` is detected; concurrent submissions of one fingerprint produce one `filed` row (the partial unique index) and N-1 `commented` or `deferred` rows.
- **Pipeline determinism:** golden signals produce fixed rendered issues; the same inputs and clock give the same decisions over 1000 random orderings of non-causal events.
- **Transition parity:** property test that `SignalGroupTransition` and `GroupTransition.Apply` give identical `FileIssue`/`AddComment`/`Reopen` decisions for matching inputs (guards the no-fork rule).
- **Dedupe:** a recurrence costs zero sink calls; a ledger-less restart with an existing marker adopts the issue (`deduped_existing`) with exactly one `FindByMarker`; an issue deleted by hand creates one new issue and records the new reference.
- **Reopen:** recurrence at or above the fixed version reopens once; older version counts only; versionless close respects `reopen_versionless`.
- **Rate limits:** day cap defers, then drains in order when the window rolls; the per-kind cap is independent; a burst opens the breaker; a 429 with `retry-after` is honoured.
- **Consent:** no row means no network call (network spy over every sink fake); `ask` holds until approval; an agent `Principal` cannot approve (negative test); a user-scope `off` beats an install-scope `auto`; `enabled = false` is silent.
- **Redaction:** a canary in every fact of every source kind is absent from every recorded sink request; a failing redactor holds the item; the residual scan holds; an architecture test asserts the module and its sources reference no model, gateway or NPU assembly.
- **Templates:** golden tests per kind; control characters and `-->` neutralised; body cap enforced; title length.
- **Auth reuse:** the GitHub sink test substitutes `IGitHubTokenProvider`; an architecture test fails if the module references a JWT, RSA or PEM API or the `Bazaar.GitHub` implementation namespace beyond the token and rate abstractions.
- **Kodo contract:** `ModuleHealthContractTests<IssueFilerModule>`; liveness without I/O; readiness fails when the audit dependency is down.
- **Architecture:** boundary rules 1 and 2 (`BAZAAR-COMPONENTS.md` section 6), a manifest and registration test per module, and a provider-module test that the core builds and idles with no sink or source module present.
- **Pane:** headless tests for the four views from fixtures; Approve is not the default button; screen-reader names present.
- **Live (manual, never in CI):** one filing, comment, close and reopen against a human-created throwaway repository through a test App, like doc 21's live tests; the Resibo equivalent is untested end to end (`telemetrees/CLAUDE.md`), so Bazaar marks it UNVERIFIED until run.

## 16. Work packages

Each is its own project or file group with tests, built and tested before merge. Order is by dependency; packages in the same row can run in parallel.

1. **`Bazaar.Server.IssueFiler.Abstractions`**: records, `IIssueSignalSource`, `IIssueSink`, `IIssueTemplate`, `IIssueLedgerReader`, result and failure types. No dependencies. (First.)
2. **Ledger store and migrations** (`issue_filer/0001`): `issue_ledger`, triggers, hash chain, `IIssueLedgerReader` over Postgres (needs 1, doc 04 conventions).
3. **Core state tables** (`issue_group`, `issue_ref`, `issue_hold`, `issue_consent`, `issue_rate_window`) and repositories (needs 1, 2).
4. **`SignalGroupTransition`** and the parity test against `GroupTransition` (needs 1).
5. **Redaction gate**: wiring `IFieldRedactor` and residual scan, fact allow-lists, canary tests (needs 1; reads `Bazaar.Diagnostics`).
6. **Consent and policy engine**: effective-mode resolution, ask/hold flow, approval hook, `Principal` checks (needs 3, 5).
7. **Rate limiter and burst breaker** (needs 3).
8. **Pipeline orchestrator, sync job, settings, audit events, health contract** (needs 2 to 7). Registers the `issue-filer` component.
9. **`Sinks.GitHub`**: create, comment, reopen, close, find by marker, read state, over `IGitHubTokenProvider` and rate state; fake GitHub fixtures (needs 1; independent of 2 to 8).
10. **`Sinks.Gc`** and **`Sinks.Email`** (each needs 1; parallel).
11. **`Sources.Crash`**: adapter over doc 43 grouping, templates (needs 1, 4; coordinate with doc 43 work package 10).
12. **`Sources.Kodo`** (incidents, `module.silent`) with templates (needs 1; Kodo abstractions).
13. **`Sources.LoopGuard`** with its aggregated-only template and tests that no identity leaks (needs 1).
14. **`Sources.Dependencies`** (needs 1 and doc 71's abstractions; blocked until doc 71 exists).
15. **Wire contracts and `Bazaar.Client.IssueFiler` pane**, headless tests (needs 2, 3; wire types in `Bazaar.Contracts.IssueFiler`).
16. **Live throwaway-repository harness**, manual and gated (needs 9 and 8).

Packages 1, 4, 5, 7, 9, 10 and 13 can start in parallel after 1. The first mergeable slice is 1, 2, 3, 5, 8 and 9, with `Sources.Crash` (11), because it replaces doc 43's inline filing end to end.

## 17. Open questions

1. **Crash path ownership (owner).** Does this module replace doc 43's inline filing stage (section 13), or does doc 43 keep filing crash groups and this module serves only non-crash signals? And is `GroupState` generalised in `Bazaar.Diagnostics` (one transition) or does `SignalGroupTransition` stay as a parity-tested sibling? Recommend: replace, and keep the sibling with the parity test until the crash path is migrated.
2. **Where issue REST calls live.** `Sinks.GitHub` over the token provider (recommended, `Bazaar.GitHub` untouched) or new methods on `IGitHubAdapter` (needs a doc 21 amendment). Also: share one extra-patterns key between `telemetry.crash.redaction_extra_patterns` and `issuefiler.redaction_extra_patterns`.
3. **Closing convention.** How is a fixed version recorded for closed issues so the reopen rule can read it (a label such as `fixed-in:0.4.1`, a closing comment convention, or a linked release)? And the default for versionless closes (`reopen_versionless = off`)? Owner decision.
4. **Scope "tenant" and loop-guard ordering.** Is tenant a real scope in Bazaar v1 (the CONVERGENCE A2 card makes it generic; Bazaar has one company per install) or reserved? Must the Psych Ward process a loop-guard trip before an outward issue is allowed?
5. **Audit and retention.** Does the ledger register as a chain inside doc 48's checkpoints (recommended) or run its own chain with its own anchoring? Ledger retention default? Needs doc 48's owner.
6. **Dependency findings and update failures.** Doc 71 does not exist yet; its signal contract is assumed (section 10.4) and unverified. Resibo also names "failed rollout or bump-test events" (doc 49 update system) as a planned signal; this doc proposes it as a later `Sources.Updates` module and the owner should confirm.
