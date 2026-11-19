# 71. Dependency Watcher and Proving Grounds

Status: PROPOSED for all design below. DECIDED only for the adoption itself: the owner's direction of 2026-11-19 is to adopt Resibo's Dependencies Warden and Proving Grounds into Bazaar as `Bazaar.Server.DependencyWatcher` and `Bazaar.Server.ProvingGrounds`, and, where a Resibo choice conflicts with Bazaar's design, to split it into a separate module instead of choosing. Nothing here is built. Source of the direction: owner statement relayed 2026-11-19; the CONVERGENCE.md rows 5 and 9 and item B3 (`/mnt/project-files/convergence/CONVERGENCE.md`) name the same two features as "R concept, B tooling" and "adopt as the pre-adoption gate".
Depends on: 03-server-host-and-providers.md (provider seam, settings registry, scheduler), 04-postgres-schema-and-migrations.md (conventions, migrations), 14-identity-service-and-passport.md (GitHub App keys), 15-security-taint-and-approvals.md (approval tiers, taint, audit), 16-agent-vms-and-environments.md and 50-windows-sandbox-provider.md (`IVmBackend`), 18-vault.md (Markdown output), 33-backup-dr-and-export.md (snapshots, restore into scratch), 37-distribution-matrix-and-packaging.md (SBOM, signed releases), 42-deps-as-modules.md (dep manifests, update policy defaults, line move, compatibility matrix), 46-kodo-health-module.md (module health contract, probes, rings gate), 47-licensing-and-trademarks.md (licence check), 49-gap-decisions-operations.md (sections 3, 6, 7), BAZAAR-COMPONENTS.md (section 4.2 `Principal`, section 5 migrations), `docs/FORWARD-FACING.md` (canary, Dependabot, Renovate), `docs/ci.md`.
Used by: 42 and 49 (module manager update policy, `UpdateCandidate.MatrixPassed`), 46 (rings, probes), doc 70 (issue filer; being written in parallel, see section 12), the module manager and Kodo client panes.

## 1. Purpose

Bazaar already decides what to do with an update once it knows one exists (`UpdatePolicyEvaluator` in `src/Bazaar.Server.Modules/Updates/`). It does not yet know that one exists, what kind of change it is, or whether the candidate works on this install before anyone adopts it. Two gaps:

1. **Watching.** Upstream projects change continuously: a PostgreSQL minor, an Npgsql prerelease, an Avalonia fix, a Python runtime wheel that finally supports free threading, an upstream issue that was blocking us and just closed, a CVE in a bundled library, a line that reaches end of life. Today this is done by hand against `docs/FORWARD-FACING.md` (a dated audit table, a weekly canary, Dependabot, Renovate). Those are build-time tools for the Bazaar repo. They do not know what an installed server runs.
2. **Proving.** The module manager's `UpdateCandidate` carries a `MatrixPassed` flag and the evaluator has a `MatrixFailed` rule (`UpdateRule.MatrixFailed`), but nothing produces that flag. Doc 42 section 10.3 describes a line-move dry run, and doc 49 section 7 requires a migration dry run and preflight, yet there is no general pre-adoption test of a candidate dependency or module version in isolation.

Resibo (the sister program) solved both with two small sub-APIs: Dependencies Warden polls per-fact-kind sources and surfaces "this exists now"; Proving Grounds tests a candidate in isolation and gates promotion. The split is the key idea. The watcher never decides adoption and never runs a test. The prover never decides what is worth testing and never watches. The update policy (existing) decides. This document adopts that split as two modules and extends it for what Bazaar is: a server with hosted dependency modules, rings, an audit chain, and an owner who must be able to see evidence.

## 2. Decided inputs

- Adopt Resibo's Dependencies Warden and Proving Grounds as Bazaar modules; where something conflicts with Bazaar's design, make it a separate module (owner, 2026-11-19, DECIDED).
- Every feature is its own module `Bazaar.Server.<Name>` plus `.Abstractions`, with a `Bazaar.Client.<Name>` module where it has a client side. No code in `Bazaar.Server` or `Bazaar.Client.Core`. Providers are plain modules on Core, with no selector. Shared libraries hold contracts and logic only, with no third-party dependencies; providers are separate modules (owner, 2026-11-19, DECIDED).
- Actor attribution is `Principal` (`kind:id`), BAZAAR-COMPONENTS.md section 4.2 (owner, 2026-11-19, DECIDED).
- Dependencies are modules; PostgreSQL 18 and 19 are two lines of one module; dual-version support; day-0 beta support; security patches auto, everything else manual; majors always manual; beta and RC never automatic (doc 42 D1 to D4, doc 49 section 7, DECIDED 2026-11-13; LTSC exception 2026-11-16).
- ZERO EXTERNAL INSTALLS for users; install is a Bazaar-only action (doc 42 D5, D7, DECIDED). This rules out requiring Docker for users (section 10.2).
- Rings (canary, early, broad), Kodo health gates and auto-halt are DECIDED (doc 49 section 6). Fail closed on security checks is a Bazaar invariant (doc 15).
- Configurability rule (DECISIONS-LOG round 13): undecided values become operator settings with a safe default; settings may tighten and never loosen a floor.
- Resibo's Keymaster (licence gating of updates) stays in Resibo (CONVERGENCE.md row 5). Not adopted.

## 3. What this document does not decide

- The update policy model, its evaluator and the module manager's decision record. They exist (doc 42, doc 49, `Bazaar.Server.Modules`) and are reused, not redesigned. This doc only proposes the narrow ports they need to receive evidence (section 11).
- The issue filer. Doc 70 owns filing, dedupe and GitHub App auth. This doc defines the finding contract the filer consumes (section 8.3) and marks doc 70's names OPEN.
- The release pipeline, rings thresholds and A/B slots (doc 49 section 6, doc 46 section 6). This doc supplies a pre-ring gate and reads the existing ring gate.
- What upstream sources a given installation is willing to contact. That is an operator setting (section 7).
- The licence verdict itself (doc 47). The watcher reports `licence_change` facts; the verdict is doc 47's.

## 4. Module map

| Module id | Projects | Kind | Role |
|---|---|---|---|
| `depwatch` | `Bazaar.Server.DependencyWatcher`, `Bazaar.Server.DependencyWatcher.Abstractions` | first-party feature | Tracked subjects, fact kinds, poll scheduler, change folding, classification, findings, changelog orchestration |
| `depwatch-nuget` | `Bazaar.Server.DependencyWatcher.NuGet` | provider | `release_version` for NuGet packages |
| `depwatch-github` | `Bazaar.Server.DependencyWatcher.GitHub` | provider | `release_version` from GitHub releases and tags, `upstream_issue_status`, repository archival |
| `depwatch-ospkg` | `Bazaar.Server.DependencyWatcher.OsPackage` | provider | `release_version` and `end_of_life` for OS packages (distribution package indexes, Windows package manifests; sub-flavours are parts) |
| `depwatch-pypi` | `Bazaar.Server.DependencyWatcher.PyPI` | provider | `release_version`, `free_threading_support`, Python line currency, for Python engine runtimes (doc 20) |
| `depwatch-osv` | `Bazaar.Server.DependencyWatcher.Osv` | provider | `security_advisory` across ecosystems from an OSV-style advisory API (name of the public service to be verified at build) |
| `depwatch-vault` | `Bazaar.Server.DependencyWatcher.VaultSurface` | surface | Writes the changelog as Markdown into the Vault (default surface) |
| `depwatch-gc` | `Bazaar.Server.DependencyWatcher.GcSurface` | surface | Posts a digest to an operator-chosen GC |
| `depwatch-repochangelog` | `Bazaar.Server.DependencyWatcher.RepoChangelog` | surface | Keep a Changelog file in a git working tree. Developer instances only, off by default (section 10.3) |
| `depwatch-client` | `Bazaar.Client.DependencyWatcher` | client pane | "Upstream" pane: facts, findings, changelog, coverage |
| `provinggrounds` | `Bazaar.Server.ProvingGrounds`, `Bazaar.Server.ProvingGrounds.Abstractions` | first-party feature | Candidate model, probe suites, gate, history, evidence, trigger rules |
| `provinggrounds-scratch` | `Bazaar.Server.ProvingGrounds.ScratchProcess` | environment provider | Isolated scratch process and directory (OS limits behind a provider) |
| `provinggrounds-vm` | `Bazaar.Server.ProvingGrounds.Vm` | environment provider | Child VM from a golden image through `IVmBackend` (Hyper-V, Windows Sandbox, QEMU/KVM) |
| `provinggrounds-docker` | `Bazaar.Server.ProvingGrounds.Docker` | environment provider | Resibo's Docker isolation, kept as an optional separate module for developer and CI use |
| `provinggrounds-client` | `Bazaar.Client.ProvingGrounds` | client pane | Candidate list, evidence viewer, "run proving" action |

Neither feature depends on the other's implementation. They meet at the finding and candidate contracts in the two Abstractions projects and at the module manager's ports (section 11). Provider modules reference only their feature's Abstractions.

## 5. Reuse and extension: what already exists

Checked against `docs/design/40-core-feature-modules-audit.md`, DECISIONS-LOG, BAZAAR-COMPONENTS.md, doc 42, doc 49, `docs/FORWARD-FACING.md`, and `src`.

| Existing | Where | How this doc uses it |
|---|---|---|
| Update policy evaluator, `UpdateCandidate` (`IsSecurity`, `MatrixPassed`, `VersionType`), `UpdateRule.MatrixFailed` | `src/Bazaar.Server.Modules/Updates/` | Reused unchanged. The watcher fills `IsSecurity`, `VersionType`, `Line`; Proving Grounds fills `MatrixPassed`. No second policy engine |
| `DependencyManifest` with the `upstream` block and `lines` | `src/Bazaar.Server.Modules/Deps/`, doc 42 section 8 | The watcher's main inventory source (section 6.3). No second manifest |
| `DeferAging`, setting `deps.update.deferWarnDays` | `Bazaar.Server.Modules.Abstractions` | Reused for "aging" display. The watcher adds nothing to deferral |
| `DryRunPlan`, `IDryRunExecutor`, `DryRunRunner`, `LineMoveRunner`, `LineMovePreflight` | `src/Bazaar.Server.Modules/LineMove/` | Reused as the migration dry-run probe of the proving suite. `IDryRunExecutor` lives in the implementation project, not in `Bazaar.Server.Modules.Abstractions`: Proving Grounds must not reference an implementation module. Follow-up: move `IDryRunExecutor`, `DryRunStepResult` and `DryRunPlan` to Modules.Abstractions (section 11) |
| `IModuleHealthContract`, `ModuleHealthProbeRunner`, `ModuleHealthContractValidator` | `src/Bazaar.Server.Modules*/Health/`, doc 46 section 10 | The module-contract probe of the suite runs the candidate through the same contract. Both new modules implement it themselves |
| `ReleaseCandidate`, `ReleaseRing`, `RingHealthGate`, `RollbackPlanner` | `src/Bazaar.Server.Modules/Releases/` | Read only. Proving evidence is a precondition to `Candidate -> Rolling` for builds whose dependency pins changed (section 9) |
| Module signature and trust (`EcdsaModuleSignatureVerifier`, `ModuleInstallService`, `VerifiedTagChecks`) | `src/Bazaar.Install/Trust/` | The candidate's package signature and permission diff are checked by calling these, never reimplemented |
| `UpdateVerifier`, `UpdateManifest`, `PinnedCertificateSignatureVerifier` | `src/Bazaar.Updates/` | Artifact acquisition for Bazaar-signed packages (section 8.4) |
| `IVmBackend`, Windows Sandbox provider | `src/Bazaar.Server.Vms/`, doc 16, doc 50 | The VM environment provider. Windows Sandbox has no checkpoints (doc 50): that is a capability the provider reports, and a clean boot per run suits proving |
| Backup restore into scratch (`RestoreRunner`, `RestoreWizard`) | `src/Bazaar.Server.Components.Backup/` | The dry-run probe restores a snapshot to scratch through it. `IProcessRunner` there is Backup-internal: Proving Grounds defines its own narrow process port (section 8.4) rather than referencing Backup |
| `IAuditLog.AppendAsync(AuditEntry)` | `src/Bazaar.Audit.Abstractions/` | Every audit event below. Actor is the canonical `Principal` text |
| `FORWARD-FACING.md` canary, Dependabot, Renovate | `.github/workflows/canary.yml`, `dependabot.yml`, `renovate.json` | Kept. They remain the build-time tooling for the Bazaar repo. The watcher does not replace them; it covers what an installation hosts and exposes the same facts to the product. Relation in section 10.4 |
| Kodo probes and module contract (doc 46) | `src/Bazaar.Kodo/` | Probes in sections 6.8 and 8.9 |
| Doc 70 issue filer | not present when this was written | Consumer of findings (section 8.3) |

Nothing here duplicates the update policy, the dry-run, the ring gate or the issue filer.

## 6. Dependency Watcher design

### 6.1 Concepts

- **Tracked subject**: one thing watched, identified by `(ecosystem, name)` plus an optional `line`. Examples: `nuget:Npgsql`, `github:valkey-io/valkey`, `ospkg:debian/postgresql-18`, `pypi:numpy`, `module:postgresql` (line 19).
- **Fact kind**: what is observed about a subject. Each kind has its own polling mechanism (Resibo's "polling is not uniform", `v3-deepdive-37-dependencies-warden.md` section 3).
- **Fact**: `(subject, kind, value, observedAt, detail, source)`.
- **Change**: a fact whose value differs from the last observed one, or from the installed baseline (section 6.5).
- **Finding**: a change that is worth a person's or the policy's attention after classification (section 6.6).
- **Poller**: a provider module's implementation of one `(ecosystem, kind)`.

### 6.2 Fact kinds and what they mean

Three kinds come from Resibo's deep-dive 28 section 3 (`release_version`, `free_threading_support`, `upstream_issue_status`). Its section 3.1 inventory also names three further shapes of fact (model and preset currency, security-dataset freshness such as virus definitions, and regulatory-value currency); Bazaar adopts them as fact kinds, and their names here are Bazaar's, PROPOSED (Bazaar's design, not from Resibo). Three more are added for Bazaar.

| Kind | Meaning | Source examples | From |
|---|---|---|---|
| `release_version` | Newest stable and pre-release versions of a subject. Pre-releases are watched on purpose (day-0 rule, doc 42 section 5.4) | NuGet, GitHub releases, PyPI, OS package index | Resibo (deep-dive 28 section 3) |
| `free_threading_support` | Whether a Python package declares or is observed to support free-threaded Python: `supported`, `unsupported`, `unknown`. Absence of a declaration is `unknown`, never `unsupported` | PyPI trove classifier, community tracker | Resibo (deep-dive 37 section 3) |
| `upstream_issue_status` | Open or closed state of one named issue that blocks or enables something for us. A release poller never sees this | GitHub issues | Resibo (deep-dive 28 section 3.2) |
| `model_currency` | Whether a bundled or recommended model or preset is behind the upstream's newer generation | Model catalogue sources behind the gateway (doc 05), local NPU models (doc 31) | Shape from Resibo (deep-dive 28 section 3.1); name and pollers are Bazaar's, PROPOSED, pollers OPEN |
| `dataset_freshness` | Age or version of a bundled dataset: time zone database, SPDX licence list, public-suffix list, scanner definitions where a scanner exists | Dataset publishers | Shape from Resibo (deep-dive 28 section 3.1, virus-definition freshness is "a silent security gap"); name is Bazaar's, PROPOSED; filed as `Security` category in the changelog because a stale scanner database is a security fact |
| `regulatory_value` | A tracked threshold or rule set outside software releases (for example a privacy-law value a compliance setting depends on) | Operator-declared URLs and values | Shape from Resibo (deep-dive 28 section 3.1, a government-set threshold); name is Bazaar's, PROPOSED; no default inventory, operator-supplied only |
| `security_advisory` | A published advisory affecting a tracked subject and version range. The only source of `IsSecurity = true` | OSV-style advisory feeds, GitHub advisories | New for Bazaar |
| `end_of_life` | An upstream line reaches or announces end of support. Drives the `retired` role of a line (doc 42 section 5.3) | Upstream lifecycle data, OS package metadata | New for Bazaar |
| `licence_change` | A subject's declared licence changed between versions. Input to doc 47's check; the watcher never gives the verdict | Package metadata, repository licence file | New for Bazaar |

### 6.3 Where the tracked inventory comes from

Resibo's `core/telemetrees/CLAUDE.md` describes a fixed `INVENTORY` of 14 tracked dependencies. Bazaar derives the inventory from facts it already holds, and lets an operator add to it:

1. **Installed modules and deps.** Every dependency module's manifest `upstream` block (`project`, `version`, `homepage`) and its `lines` (doc 42 section 8). This covers PostgreSQL, pgvector, Valkey, Garnet and runtimes. Source: Modules.Abstractions.
2. **SBOM.** The signed release SBOM (doc 49 section 3, doc 37) lists the compile-time packages. This is the read-only compile-time summary of doc 42 section 4, now live.
3. **Operator subjects.** Added through the settings registry or the Upstream pane, each audited.
4. **Declared blockers.** A module manifest or a doc can declare `tracks: [{kind: upstream_issue_status, ref: "owner/repo#123", reason: "..."}]`. Resibo's example was `opencv/opencv#27933` blocking free-threaded wheels; Bazaar's equivalent is any upstream bug that gates a feature.

Each source implements `ITrackedSubjectSource`. The module manager adapter is the only one in `depwatch` core; it reads through Modules.Abstractions. The SBOM adapter is a separate small class in the same module reading a file path, so it adds no dependency.

### 6.4 Contracts

```csharp
// Bazaar.Server.DependencyWatcher.Abstractions

public enum FactKind { ReleaseVersion, FreeThreadingSupport, UpstreamIssueStatus, ModelCurrency,
                       DatasetFreshness, RegulatoryValue, SecurityAdvisory, EndOfLife, LicenceChange }

public sealed record TrackedSubject(string Ecosystem, string Name, string? Line,
                                    IReadOnlySet<FactKind> FactKinds, string? InstalledVersion,
                                    string Origin /* module:postgresql | sbom | operator | blocker */);

public sealed record ObservedFact(TrackedSubject Subject, FactKind Kind, string Value,
                                  string Detail, string SourceId, DateTimeOffset ObservedAt);

public abstract record PollOutcome
{
    public sealed record Observed(ObservedFact Fact) : PollOutcome;
    public sealed record NoSignal(string Why) : PollOutcome;                 // nothing to report, not an error
    public sealed record Unreachable(string Code, string Detail) : PollOutcome; // never reported as a value
}

/// One provider answers one (ecosystem, kind). Providers are plain DI registrations.
public interface IFactPoller
{
    string ProviderId { get; }
    string Ecosystem { get; }
    FactKind Kind { get; }
    bool Corroborating => false;           // true: may coexist with another poller for the same key
    ValueTask<PollOutcome> PollAsync(TrackedSubject subject, CancellationToken ct);
}

public interface ITrackedSubjectSource { IAsyncEnumerable<TrackedSubject> ListAsync(CancellationToken ct); }

public sealed record FactChange(ObservedFact Fact, string? Previous, bool IsFirstObservation, bool IsNewerThanInstalled)
{ public bool Changed => !IsFirstObservation && Previous != Fact.Value || IsNewerThanInstalled; }
```

Poller coverage rules. From Resibo's docs (deep-dive 37 section 8, "multi-poller coverage test"): every declared `(subject, kind)` pair must have a poller, with no silent fallback to release-only. Bazaar's own rules, PROPOSED (Bazaar's design, not from Resibo): a second poller for the same `(ecosystem, kind)` is a startup error unless both declare `Corroborating`, so which poller answers never depends on registration order (this follows Resibo's PRINCIPLES section 1.2 "more than one provider can run at once where corroboration adds real value"). `CoverageReport` lists declared `(subject, kind)` pairs with no poller, shown in the pane and raised as a Kodo amber. There is no fallback to release-only when a declared kind has no poller.

### 6.5 The polling pass and its guarantees

Taken from `core/telemetrees/dependencies_warden/poller.py` and kept:

- **A pass never fails.** An unreachable source costs that one fact one cycle and is recorded with its code; every other subject is still polled.
- **An unobserved fact is never reported.** Unreachable produces no value, not a stale or assumed one.
- **First observation is not a change.** It would otherwise dump the whole inventory on day one.

Changed for Bazaar:

- **Baseline is the installed version.** For `release_version`, the first observation is compared against the module's installed version (known from the manifest). A newer release that already exists on day one is a real finding (`IsNewerThanInstalled`). PROPOSED (Bazaar's design, not from Resibo).
- **Freshness is tracked.** Each `(subject, kind)` stores `lastSuccessAt`. A fact older than `deps.watcher.staleAfterHours` is shown as stale and never counted as "no change". A source that fails for a week must be visible. PROPOSED (Bazaar's design, not from Resibo).
- **Per-source backoff and budget.** Each provider has a circuit breaker (doc 46 section 7.4 pattern) and a request budget. Rate limits are read from response headers and respected.
- **Idempotent persistence.** A fact change is written once, keyed by `(subject, kind, value)`, so a retried pass cannot duplicate. PROPOSED (Bazaar's design, not from Resibo).
- **Egress is policy.** All pollers use `IUpstreamHttp` (a thin port in Abstractions over `HttpClient`) so the operator's egress allowlist, proxy and offline mode apply. In offline mode the watcher runs against a mirrored snapshot file or does nothing and says so.

### 6.6 Change classification

Resibo's docs leave classification to a human ("whether a surfaced change is program-important is a human call", `core/telemetrees/dependencies_warden/CLAUDE.md`) and state no per-kind changelog mapping. Bazaar's mapping below (release Changed, free-threading Added, issue Fixed, model Changed, dataset Security, regulatory Changed) is its own, PROPOSED (Bazaar's design, not from Resibo). Bazaar needs a machine-readable class for the policy, so classification has two outputs that both come from deterministic rules:

1. **Changelog heading** (Bazaar's mapping above; new kinds: `security_advisory` Security, `end_of_life` Removed, `licence_change` Changed).
2. **Update category and version type** for `UpdateCandidate`: category from doc 49 section 7 (Security, critical fix, bug fix, feature, breaking, dependency, platform) and version type from the SemVer or numeric difference between installed and observed (major, minor, patch, pre-release, with `VersionType` GA, RC, Beta).

Rules, in order, first match wins:

| Rule | Result |
|---|---|
| A `security_advisory` fact names the subject and the installed version is in the affected range, and a fixed version is observed | Category Security, `IsSecurity = true`, candidate is the fixed version |
| Observed version is a pre-release marker (`-beta`, `-rc`, `-preview`, `a`, `b`, `rc` per ecosystem rules) | Version type Beta or RC. Never automatic regardless of policy (existing `PreReleaseNotAuto`) |
| Major number increased, or a new line appears | Category Breaking (provisional), new-line approval path (existing `NewLineNeedsApproval`) |
| `end_of_life` observed for the installed line | Finding with severity High, suggests line move, does not move |
| Anything else with a newer version | Category Dependency, version type from the difference |
| Version string cannot be parsed | Unclassified: treated as Manual, and flagged to the owner (matches the existing evaluator's `DefaultManual` for "cannot be classified") |

`IsSecurity` never comes from release-note text. Release notes can say "security" loosely and a wrong `true` would make an update automatic. A missing advisory source means `IsSecurity = false`, which is the safe direction (manual). Release-note keyword hints are stored as an unverified label (`hint:security-keyword`) shown to the human, never fed to the evaluator.

### 6.7 Changelog output

Resibo's lesson, kept: tracking that nobody reads is pointless, so the output goes where the reader already is. In Resibo that is a checked-in summary file read by Claude Code sessions, regenerated on every fact change with `ChangelogWriter` as the single writer (deep-dive 37 section 4, `core/telemetrees/CLAUDE.md`). In Bazaar the readers are the owner, agents in GCs, and the Vault. Output is an interface with surface modules (section 4):

```csharp
public sealed record ChangelogEntry(string Heading, string Text, FactKind Kind, string SubjectKey, DateTimeOffset RecordedAt, string? FindingId);

public interface IChangelogSurface
{
    string SurfaceId { get; }
    ValueTask<SurfaceResult> AppendAsync(IReadOnlyList<ChangelogEntry> entries, CancellationToken ct);
}
public sealed record SurfaceResult(IReadOnlyList<ChangelogEntry> Written, string? Error); // never throws for I/O
```

- One write path: the same change event updates tracking and appends the entry, in one transaction for the database record. The surface write follows from an outbox row, so a failed surface never loses the observation (errors as data, PRINCIPLES P4.1).
- Entries are phrased per kind (PROPOSED, Bazaar's design, not from Resibo): "opencv-python: tracked upstream issue X is now closed (was open)" style, not a raw diff.
- The Vault surface writes `vault/ops/dependency-changelog.md` in Keep a Changelog form under `[Unreleased]`, grouped by heading, deduplicated per line. The Vault is the source of truth for Markdown (CLAUDE.md), Postgres only indexes.
- The GC surface posts one digest per cycle (not one message per entry), authored as `service:depwatch`, to a GC the operator names. GCs stay the coordination system; this only informs.
- The database also holds the structured history (table `depwatch_change`), while Resibo's docs keep the file as the single source of truth (`core/telemetrees/CLAUDE.md`). Bazaar needs queryable history for the pane and for evidence.

### 6.8 Settings registry rows

Rows follow the doc 03 format. Keys use the `deps.` prefix already used by `deps.update.deferWarnDays`.

| Key | Type and values | Default | Scope | Who may change | Approval |
|---|---|---|---|---|---|
| `deps.watcher.enabled` | bool | `true` | install | operator | No |
| `deps.watcher.pollIntervalHours` | int 1 to 168 | 24 (Resibo's `poll_interval_hours: 24`, deep-dive 37) | install | operator | No |
| `deps.watcher.advisoryPollIntervalHours` | int 1 to 168 | 6 | install | operator | No |
| `deps.watcher.staleAfterHours` | int 2 to 720 | 72 | install | operator | No |
| `deps.watcher.trackPrereleases` | bool | `true` (day-0 rule) | install | operator | No |
| `deps.watcher.egress.mode` | `direct`, `proxy`, `mirror`, `offline` | `direct` | install | owner | Owner tier (legal and privacy exposure: contacts third parties) |
| `deps.watcher.egress.allowHosts` | list | public registry hosts of enabled providers | install | owner | Owner tier |
| `deps.watcher.github.auth` | `none`, `app` | `none` (unauthenticated, low rate limit) | install | owner | Owner tier; `app` uses the Identity Service GitHub App key, never a personal token |
| `deps.watcher.surface.vault.path` | Vault-relative path | `ops/dependency-changelog.md` | install | operator | No |
| `deps.watcher.surface.gc` | GC id or `off` | `off` | install | operator | No |
| `deps.watcher.surface.repoChangelog` | `off` or repo path | `off` | install | owner | Owner tier; developer instances only |
| `deps.watcher.operatorSubjects` | list of subjects | empty | install | operator | No, audited |
| `deps.watcher.findings.minSeverity` | `info`, `low`, `medium`, `high` | `low` | install | operator | No |

Floors, not settings: pollers never send install-identifying data, GC or Vault content or secrets; every outbound call carries only the public subject name and a generic client name; `IsSecurity` comes only from an advisory source (section 6.6); an unreachable source is never reported as a value.

### 6.9 Data tables

Migration namespace `src/Bazaar.Server.DependencyWatcher/Data/Sql/depwatch/NNNN_name.sql` (BAZAAR-COMPONENTS.md section 5). Sketch of `0001_init.sql`:

```sql
CREATE TABLE depwatch_subject (
  subject_id    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  ecosystem     text NOT NULL,
  name          text NOT NULL,
  line          text,
  fact_kinds    text[] NOT NULL,
  origin        text NOT NULL,                 -- module:<id> | sbom | operator | blocker
  installed_version text,
  added_by      text NOT NULL,                 -- Principal text, kind:id
  added_at      timestamptz NOT NULL DEFAULT now(),
  UNIQUE (ecosystem, name, COALESCE(line, ''))
);

CREATE TABLE depwatch_fact_state (            -- last observed value and freshness per (subject, kind)
  subject_id    bigint NOT NULL REFERENCES depwatch_subject,
  kind          text NOT NULL,
  value         text NOT NULL,
  detail        text NOT NULL DEFAULT '',
  source_id     text NOT NULL,
  observed_at   timestamptz NOT NULL,
  last_success_at timestamptz NOT NULL,
  last_error_code text,
  PRIMARY KEY (subject_id, kind)
);

CREATE TABLE depwatch_change (                -- append-only history of real changes
  change_id     bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  subject_id    bigint NOT NULL REFERENCES depwatch_subject,
  kind          text NOT NULL,
  previous      text,
  value         text NOT NULL,
  detail        text NOT NULL DEFAULT '',
  heading       text NOT NULL,
  category      text,                          -- doc 49 category
  version_type  text,
  is_security   boolean NOT NULL DEFAULT false,
  observed_at   timestamptz NOT NULL,
  UNIQUE (subject_id, kind, value)             -- idempotent per change
);

CREATE TABLE depwatch_finding (
  finding_id    uuid PRIMARY KEY,
  change_id     bigint NOT NULL REFERENCES depwatch_change,
  severity      text NOT NULL,
  state         text NOT NULL,                 -- open | acknowledged | candidate_created | dismissed
  candidate_ref text,                          -- ProvingGrounds candidate id when one exists
  filer_ref     text,                          -- issue filer reference (doc 70), when filed
  decided_by    text,                          -- Principal text
  decided_at    timestamptz
);

CREATE TABLE depwatch_outbox (                -- surface delivery, one row per surface per entry
  outbox_id     bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  change_id     bigint NOT NULL REFERENCES depwatch_change,
  surface_id    text NOT NULL,
  delivered_at  timestamptz,
  attempts      int NOT NULL DEFAULT 0,
  last_error    text,
  UNIQUE (change_id, surface_id)
);
```

### 6.10 Findings, the policy and the issue filer

A finding is the unit handed onward:

```csharp
public sealed record DependencyFinding(Guid FindingId, TrackedSubject Subject, FactKind Kind,
    string Previous, string Value, FindingSeverity Severity, string Heading,
    string? UpdateCategory, string? VersionType, bool IsSecurity, string? FixedVersion,
    IReadOnlyList<string> EvidenceUrls, DateTimeOffset ObservedAt);

public interface IFindingSink { ValueTask PublishAsync(DependencyFinding finding, CancellationToken ct); }
```

- **To the module manager update policy.** The watcher publishes an `UpdateCandidateAvailable` record per newer, installable version (section 11). It does not call the evaluator. The module manager builds `UpdateCandidate` from it, asks Proving Grounds for `MatrixPassed`, and runs its existing evaluator. This is the Resibo rule ("the watcher never decides adoption") expressed with Bazaar's policy engine.
- **To the issue filer (doc 70).** Findings that need a person (a `high` finding, an `end_of_life`, an unparseable version, a proving failure on a security patch, a source dark for longer than a limit) are published through `IFindingSink`. Doc 70 consumes it and does the dedupe by fingerprint (Resibo's "fingerprint the signal, not the occurrence", `telemetrees/CLAUDE.md`). The fingerprint this module supplies is `depwatch:<kind>:<ecosystem>:<name>[:<line>]:<value>` so a recurring problem groups. The filer's own interface and names are OPEN until doc 70 lands (section 12).
- **To Kodo.** Findings with severity `high` also raise a Kodo non-critical alert through the probe in 6.11. A `security_advisory` for a hosted dependency never makes Kodo red by itself.

### 6.11 Kodo health contract

`depwatch` implements `IModuleHealthContract` (liveness without I/O, readiness from cached state, declared dependencies, quick and full self-test). Provider modules implement it too (a provider that cannot answer fails the contract and does not load, per doc 46 section 10).

| Probe id | Critical | Meaning |
|---|---|---|
| `depwatch.poll.freshness` | no | Oldest `last_success_at` across tracked pairs against `staleAfterHours` |
| `depwatch.source.<provider>.reachable` | no | Per-provider breaker state and last error code |
| `depwatch.coverage` | no | Declared fact kinds with no poller |
| `depwatch.surface.backlog` | no | Undelivered outbox rows older than an hour |
| `depwatch.finding.high_open` | no | Open `high` findings older than a configured age |

Self-test `Quick`: classify a fixed fixture of version pairs and advisory ranges and compare to expected results. `Full`: run a poll pass against a recorded transport fake inside a scratch schema and verify idempotency, with no residue.

## 7. Provider modules

All providers implement `IFactPoller` only and reference `Bazaar.Server.DependencyWatcher.Abstractions`. No provider calls another.

- **`depwatch-nuget`**: `release_version` from the NuGet flat container and registration index. Stable and pre-release both; ordering by NuGet SemVer rules (doc 42 uses NuGet range syntax for the same reason). Also reports `licence_change` from package metadata when `licenceExpression` differs between versions.
- **`depwatch-github`**: `release_version` from releases and tags (for projects not on a registry, such as Valkey or PostgreSQL tooling), `upstream_issue_status` for one issue ref (Resibo deep-dive 37 section 3, issue open/closed state; plus an optional linked-PR hint like Resibo's status client), repository archived or renamed as an `end_of_life` hint. Authentication per `deps.watcher.github.auth`.
- **`depwatch-ospkg`**: `release_version` and `end_of_life` for OS packages and for runtimes baked into VM golden images (doc 16, doc 42 category `runtime`). Flavours are parts, each a small reader with its own tests: Debian-family and RPM-family package indexes, and Windows package manifests. Which distributions and Windows source is first is OPEN (section 14).
- **`depwatch-pypi`**: `release_version` (stable and pre-release), `free_threading_support` and Python line currency, for Python engine runtimes (doc 20). Resibo's deep-dive 37 reads the PyPI trove classifier "Programming Language :: Python :: Free Threading", release notes and a community tracker as a cross-check, with no single source assumed authoritative. Bazaar's precedence is its own, PROPOSED (Bazaar's design, not from Resibo): the classifier is authoritative when present; a community tracker is a secondary signal only when the classifier is absent; no signal is `unknown`, never `unsupported`; the event records which source spoke. The community tracker URL (Resibo uses `py-free-threading.github.io`) is configuration, not hard-coded.
- **`depwatch-osv`**: `security_advisory` for any ecosystem the other providers cover, matching installed version ranges. This is the only source for `IsSecurity`. Its host is on the egress allowlist like the others.

`model_currency`, `dataset_freshness` and `regulatory_value` have no shipped poller in this proposal. The contract and changelog mapping exist so a module can supply one (for example the gateway module for `model_currency`). That is the honest state; Resibo's own bench registry also starts empty rather than fabricating results (`services/update/proving_grounds/CLAUDE.md`).

## 8. Proving Grounds design

### 8.1 Purpose in Bazaar terms

A candidate is "this exact version of this dependency or module, as an installable package". Proving Grounds takes a candidate to a scratch environment that cannot touch the running install, runs a defined suite of probes, and records evidence. The result is the source of `MatrixPassed` for the policy and an attachment on the update decision record. It does not decide to adopt.

Resibo rules kept (`v3-deepdive-36-proving-grounds.md`, `services/update/proving_grounds/CLAUDE.md`): the test runs against the real workload of the affected code, not a generic smoke test; isolation is mandatory and never silently downgraded; the gate is `ok and passed` with no looser bar for dependencies than for code releases; a test that could not run (`ok = false`) is distinct from one that ran and failed (`passed = false`) and neither promotes; one mechanism serves both dependency bumps and release candidates; a dispatcher's own exception becomes data.

### 8.2 Contracts

```csharp
// Bazaar.Server.ProvingGrounds.Abstractions

public enum CandidateKind { DependencyBump, ModuleUpdate, CodeRelease }

public sealed record ProvingCandidate(
    Guid CandidateId, CandidateKind Kind, string ModuleId, string Line, string FromVersion, string ToVersion,
    string ArtifactRef, string? ArtifactSha256, Guid? FindingId, Principal RequestedBy, DateTimeOffset RequestedAt);

public sealed record ProbeResult(string ProbeId, ProbeVerdict Verdict, TimeSpan Duration, string Evidence, string? ErrorCode);
public enum ProbeVerdict { Passed, Failed, NotRun }          // NotRun: could not run (environment, missing probe)

public sealed record ProvingResult(Guid ResultId, Guid CandidateId, ProvingOutcome Outcome,
    IReadOnlyList<ProbeResult> Probes, string EnvironmentId, string SuiteVersion,
    DateTimeOffset StartedAt, DateTimeOffset FinishedAt, string EvidenceHash)
{
    public bool Ok => Outcome != ProvingOutcome.CouldNotRun;           // Resibo's `ok` (documented in proving_grounds/CLAUDE.md)
    public bool Passed => Outcome == ProvingOutcome.Passed;            // Resibo's `passed`
}
public enum ProvingOutcome { Passed, Failed, CouldNotRun }

/// Resibo's documented `gate_promotion()`: total and deliberately simple. Requires Ok and Passed and a complete suite.
public static class ProvingGate
{
    public static bool Allows(ProvingResult r, SuiteRequirement required) => r.Ok && r.Passed && required.IsSatisfiedBy(r);
}

/// A module (or Proving Grounds itself) contributes probes. Resibo's documented `BenchDispatcher`, keyed per affected thing.
public interface IProvingProbe
{
    string ProbeId { get; }
    bool AppliesTo(ProvingCandidate candidate);
    ValueTask<ProbeResult> RunAsync(ProvingCandidate candidate, IProvingEnvironment env, CancellationToken ct);
}

/// Isolation seam. A candidate never runs without it (Resibo documents an availability check before a candidate runs).
public interface IProvingEnvironmentProvider
{
    string ProviderId { get; }
    ValueTask<EnvironmentCapabilities> ProbeAsync(CancellationToken ct);       // available? isolation level? OS?
    ValueTask<IProvingEnvironment> CreateAsync(ProvingCandidate candidate, EnvironmentSpec spec, CancellationToken ct);
}
public interface IProvingEnvironment : IAsyncDisposable
{
    string EnvironmentId { get; }
    ValueTask<ExecResult> ExecAsync(ExecRequest request, CancellationToken ct);  // bounded time, memory, no network by default
    ValueTask StageAsync(string artifactPath, CancellationToken ct);
    ValueTask<EnvironmentManifest> InspectAsync(CancellationToken ct);           // hashes for the evidence
}

public interface IMatrixVerdictSource                  // port the module manager calls (section 11)
{
    ValueTask<MatrixVerdict> GetAsync(string moduleId, string line, string toVersion, CancellationToken ct);
}
public sealed record MatrixVerdict(MatrixState State, Guid? ResultId, DateTimeOffset? At);
public enum MatrixState { Passed, Failed, Untested, Stale }   // Untested and Stale are NOT Passed
```

`Principal` is the type from `Bazaar.Core` (BAZAAR-COMPONENTS.md section 4.2).

### 8.3 What the suite runs

Resibo ran the affected API's bench suite and nothing generic. Bazaar's equivalent of "the real workload for the code affected" is, in order:

1. **`pg.artifact.verify`**: artifact hash against the pinned SHA-256 and the signature check through `Bazaar.Updates` and `Bazaar.Install.Trust` (Bazaar-signed packages) or the pinned archive hash (upstream binary archives, doc 42 Q1 path b). Failure here stops the suite.
2. **`pg.manifest.validate`**: the candidate's `DependencyManifest` through `DependencyManifestValidator` (tags, origin, hidden-surface rule, permission floors of doc 42 section 14.3). A permission diff against the installed version is recorded; any new permission needs the owner's approval later, not here.
3. **`pg.licence.check`**: doc 47's check on the candidate's SBOM. A verdict change turns the result to `Failed` with the verdict as evidence.
4. **`pg.health.contract`**: start the candidate in the environment and run the Kodo module health contract tests (liveness, readiness, dependencies, self-test; doc 46 section 10.3), including the negative tests. This is the general-purpose real workload every module already must pass.
5. **`pg.migration.dryrun`**: for candidates with data (PostgreSQL line or minor, any module with migrations), restore the latest snapshot into the scratch environment and run the migrations, using the existing `DryRunRunner` flow (restore, migrations in plan order, GC consistency checks). Reports runtime, lock time and irreversible steps as doc 49 section 7 requires.
6. **`pg.compat.matrix`**: for each dependent module in the compatibility matrix (doc 42 section 9.1), run that module's own proving probe against the candidate in the same environment. A dependent that contributes no probe leaves its cell `Untested`.
7. **Module probes**: any module that depends on the candidate may contribute `IProvingProbe`s (its own contract tests, a bench, a smoke workload with a real schema). This is Resibo's `BenchDispatcherRegistry` keyed by "affected thing", with modules registering instead of one central file.
8. **`pg.soak`** (optional by candidate class): run the candidate for a configured soak period under synthetic load in the scratch environment and watch the Kodo probes of the scratch host. Default off, required for self-update candidates (section 9.3).

`SuiteRequirement` states which probes must be `Passed`, derived from the candidate: the first four always, `migration.dryrun` for any candidate with data, `compat.matrix` for any with dependents. A required probe that is absent (no applicable `IProvingProbe`) is `NotRun`, which makes the outcome `CouldNotRun`. Resibo's rule that a missing bench dispatcher is reported as `NO_BENCH_DISPATCHER` rather than a fabricated pass is kept as `NO_PROVING_PROBE`.

### 8.4 Environment providers

| Provider | Isolation | Notes |
|---|---|---|
| `provinggrounds-scratch` | A scratch directory and process tree with OS limits applied through the server's OS provider (Job Object on Windows, cgroup or parent-death on Linux, as for hidden deps in doc 42 section 6.2), no network by default, loopback only for the candidate's own ports | The default and the only one that needs nothing installed. Isolation level reported as `process`. Enough for a database, a cache, a package, a runtime. Not enough for an untrusted third-party executable |
| `provinggrounds-vm` | A child VM from a golden image through `IVmBackend` (doc 16), restored from checkpoint where the backend supports it, clean boot where it does not (Windows Sandbox, doc 50). Network off by default | Isolation level `vm`. Required for third-party executables, runtimes inside guest images, and anything marked `requiresVm` in the candidate manifest. Uses the server's VM capacity: Proving Grounds takes a lease through the VM lifecycle service, so it cannot starve agents (a priority and a cap setting) |
| `provinggrounds-docker` | Docker container, via the plain CLI | Resibo's choice (deep-dive 36 "containerization (Docker)" resolved; `services/update/proving_grounds/CLAUDE.md`: plain CLI, not the SDK). Kept as a separate optional module because Bazaar forbids requiring a user-installed tool (doc 42 D5, D7). For developer machines and CI only. Off by default |

Rules: `is_available()` is checked before the candidate is touched (Resibo's docs: a host with no Docker binary reports the runner unavailable rather than running unisolated). If no provider meets the candidate's required isolation level, the result is `CouldNotRun` with `ENVIRONMENT_UNAVAILABLE`, never a downgrade to running on the host. The provider with the highest isolation that satisfies the requirement is chosen by a plain rule over reported capabilities, not a selector module: required level from the candidate, then the operator's preference setting, then registration order is not used.

Process execution inside providers goes through a narrow `IProvingProcessPort` in Abstractions with a bounded-time, bounded-output contract. It is not `Bazaar.Server.Components.Backup.IProcessRunner`, which is Backup-internal.

Artifact acquisition: `IArtifactFetcher` in Abstractions. The default implementation lives in `provinggrounds` core and calls the existing verifier; Resibo's shared download machinery with gated-model token support (deep-dive 36 section 3) has no Bazaar counterpart yet and is not needed until a model-currency candidate exists (OPEN, section 14). Secrets (tokens for gated downloads) are injected from the Identity Service, never stored in the candidate row.

### 8.5 Triggering: who decides what to test

Resibo's rule 8: the watcher says "this exists now", and a human decides what is worth testing. Bazaar's policy engine already decides update behaviour automatically for security patches, so a pure human gate would block that. Resolved as a setting, with the Resibo behaviour as one option:

| `deps.proving.trigger` | Behaviour |
|---|---|
| `manual` | Only a person starts a run (Upstream pane or palette action) |
| `policy` (default) | A run starts automatically for a candidate the update policy would otherwise offer for adoption on a line in use (security patches, candidates within a pinned range), and on request for everything else. Pre-releases and new lines run only on request, except on Beta and Alpha channel installs, where day-0 beta runs start automatically (doc 42 section 5.4) |
| `all` | Every newer version of a tracked hosted dependency gets a run |

A person always decides adoption. The existence of a passing result does not adopt anything.

### 8.6 Results as evidence in the update decision record

- Each run writes one `provinggrounds_result` row and one evidence bundle. The bundle holds per-probe output, the environment manifest hashes, the suite version, and timings. Its SHA-256 is stored in the row and in the audit entry, so the evidence is tamper-evident on the existing audit chain (doc 46 section 6.5 pattern).
- The bundle stores no secrets and no GC or Vault content. Probe output that touches a restored snapshot is limited to counts, hashes and error codes; row contents never leave the scratch environment.
- The module manager attaches the `ResultId` to the update decision record and shows the probe list next to the decision (section 11). Decisions made without a result are marked "untested" in the audit record, not left blank.
- Evidence is retained per `deps.proving.evidenceRetentionDays` and survives the candidate. It is exported with other audit evidence (doc 33).

### 8.7 History, persistence and tables

Resibo's `GetTestHistory` was a real in-memory list, not persisted (`proving_grounds/CLAUDE.md`). Bazaar persists it. Migration namespace `src/Bazaar.Server.ProvingGrounds/Data/Sql/proving-grounds/0001_init.sql`:

```sql
CREATE TABLE pg_candidate (
  candidate_id  uuid PRIMARY KEY,
  kind          text NOT NULL,                  -- dependency_bump | module_update | code_release
  module_id     text NOT NULL,
  line          text NOT NULL,
  from_version  text NOT NULL,
  to_version    text NOT NULL,
  artifact_ref  text NOT NULL,
  artifact_sha256 text,
  finding_id    uuid,                           -- depwatch_finding, no FK across modules
  requested_by  text NOT NULL,                  -- Principal text
  requested_at  timestamptz NOT NULL DEFAULT now(),
  UNIQUE (module_id, line, to_version, COALESCE(artifact_sha256, ''))
);

CREATE TABLE pg_result (
  result_id     uuid PRIMARY KEY,
  candidate_id  uuid NOT NULL REFERENCES pg_candidate,
  outcome       text NOT NULL,                  -- passed | failed | could_not_run
  environment_id text NOT NULL,
  isolation     text NOT NULL,                  -- process | vm | container
  suite_version text NOT NULL,
  started_at    timestamptz NOT NULL,
  finished_at   timestamptz NOT NULL,
  evidence_hash text NOT NULL,
  evidence_ref  text NOT NULL,                  -- blob store key, doc 33 export covers it
  superseded_by uuid
);

CREATE TABLE pg_probe_result (
  result_id     uuid NOT NULL REFERENCES pg_result,
  probe_id      text NOT NULL,
  verdict       text NOT NULL,                  -- passed | failed | not_run
  duration_ms   int NOT NULL,
  error_code    text,
  evidence_excerpt text NOT NULL DEFAULT '',
  PRIMARY KEY (result_id, probe_id)
);

CREATE TABLE pg_gate_decision (                -- every promotion decision, so a bypass is detectable
  gate_id       bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  candidate_id  uuid NOT NULL REFERENCES pg_candidate,
  channel       text NOT NULL,
  ring          text,
  result_id     uuid REFERENCES pg_result,
  allowed       boolean NOT NULL,
  reason        text NOT NULL,
  decided_at    timestamptz NOT NULL DEFAULT now()
);
```

### 8.8 Fail-closed rules

1. No environment of the required isolation, no run, no downgrade. The result is `CouldNotRun`.
2. `CouldNotRun` never counts as passed. `MatrixState` is `Untested`, and `MatrixPassed` is false.
3. A passing result is bound to `(module, line, to_version, artifact_sha256, suite_version)`. A different artifact hash, a suite version change that adds a required probe, or a result older than `deps.proving.resultMaxAgeDays` makes it `Stale`, which is not a pass.
4. Every promotion decision, for any channel or ring, is recorded in `pg_gate_decision`. A test (the Resibo "promotion-gate bypass test") asserts there is no code path that moves a dependency-changing build to a ring without a row with `allowed = true`.
5. The gate is `ok and passed and suite satisfied`. A failed required probe fails the candidate regardless of how many others passed. There is no weighted score.
6. A probe that throws is `Failed` with an error code (Resibo's docs convert a dispatcher's exception to data), never an escape and never a pass.
7. Environment teardown always runs. A leaked environment is a Kodo amber and is reaped by a sweeper (a Bazaar rule, PROPOSED, not from Resibo). Failure to tear down does not change the verdict but does raise the probe in 8.9.
8. A candidate run never has the host's secrets, the install's database credentials or its production data directory mounted. Dry runs use a restored snapshot copy only.
9. Any internal error in Proving Grounds itself results in `CouldNotRun` and a Kodo alert. It can never produce `Passed`.
10. An unreachable artifact source, a signature failure or a hash mismatch is `Failed` at `pg.artifact.verify` with an audit security event, not a retry loop.

### 8.9 Settings registry rows and Kodo contract

| Key | Type and values | Default | Scope | Who may change | Approval |
|---|---|---|---|---|---|
| `deps.proving.trigger` | `manual`, `policy`, `all` | `policy` | install | operator | No |
| `deps.proving.isolation.min` | `process`, `vm`, `container` | `process` for databases, caches, packages; `vm` forced for third-party executables | install | owner may tighten only | Owner tier to relax toward `process` for a class (never below the candidate's required level) |
| `deps.proving.environment.preference` | ordered list of provider ids | `scratch`, `vm` | install | operator | No |
| `deps.proving.network` | `none`, `artifactHostOnly` | `none` | install | owner | Owner tier to loosen |
| `deps.proving.timeoutMinutes` | int 5 to 720 | 60 | install | operator | No |
| `deps.proving.maxConcurrent` | int 1 to 8 | 1 | install | operator | No |
| `deps.proving.vm.priority` | `below_agents`, `equal` | `below_agents` | install | operator | No |
| `deps.proving.resultMaxAgeDays` | int 1 to 90 | 14 | install | operator | No |
| `deps.proving.evidenceRetentionDays` | int 30 to 3650 | 365 | install | operator | No |
| `deps.proving.selfUpdateSoakDays` | int 0 to 90 | 14 (Resibo's `proving_grounds_soak_days: 14`, `v3-deepdive-38`; PROPOSED value) | install | owner | Owner tier to lower; floor 1 for Kodo, Sentinel and the updater |
| `deps.proving.requireForPolicyAuto` | bool | `true` | install | owner | Owner tier to turn off; floor: cannot be off for the stable channel's broad ring |

Floors, not settings: a candidate never runs without an isolation provider; a failed or untested required probe never produces a pass; the host's secrets and production data are never exposed to a candidate; the audit event is written before a result is announced.

Kodo contract: `provinggrounds` implements `IModuleHealthContract`. Probes:

| Probe id | Critical | Meaning |
|---|---|---|
| `provinggrounds.environment.available` | no | At least one provider reports available at the configured minimum isolation. Red means automatic updates fall back to Manual (section 11), which is a degraded state, not an outage |
| `provinggrounds.environment.orphans` | no | Environments or scratch directories older than the timeout still present |
| `provinggrounds.history.integrity` | no | Evidence hashes in `pg_result` match the stored bundles on a sampled check |
| `provinggrounds.gate.bypass` | yes | A ring promotion exists for a dependency-changing build without an allowing `pg_gate_decision` row |
| `provinggrounds.coverage` | no | Hosted dependency modules with no applicable proving probe |

Self-test `Quick`: run the gate over a fixture matrix (ok and passed, ok and failed, could-not-run, stale, incomplete suite) and require the expected decision. `Full`: run a trivial no-op candidate through the scratch provider and check teardown left no residue.

## 9. Rings, snapshots and the update path

### 9.1 Where Proving Grounds sits

```
Watcher  ->  finding  ->  module manager builds UpdateCandidate
                              |
                              +-- asks IMatrixVerdictSource  <-  Proving Grounds (scratch / VM)
                              |
                              +-- UpdatePolicyEvaluator (existing): Auto / Manual / Deferred / ... / Blocked
                              |
        owner or window approves  ->  snapshot (doc 33, A/B doc 46)  ->  apply  ->  verify (health contract)
```

Proving Grounds is before the snapshot and before any ring. It answers "may this candidate be offered", not "was the rollout healthy".

### 9.2 Rings integration

- Bazaar's own release builds change dependency pins (a bumped Npgsql, a new PostgreSQL bundle). Such a build cannot go from `ReleaseCandidateStatus.Candidate` to `Rolling` (canary) unless every changed pin has an allowing `pg_gate_decision` for its candidate. The check is a precondition in the release pipeline, implemented as an adapter against `ReleaseCandidateLifecycle`'s input, not a change to its transition rules.
- The ring gates are unchanged (`RingHealthGate`, Kodo signals). Proving is a pre-ring gate; ring gates are the post-deployment gate. Resibo also held the dependency gate "to the same bar as a code release's health-check cutover", with no looser bar; Bazaar keeps this by making the proving suite include the same module health contract the ring gate reads.
- Install-level rings for a hosted dependency update (the owner's own instance as canary) work as doc 49 section 6 says. A halt of a build records the proving result id that cleared it, so a post-mortem can see whether proving missed something (the suite then gets a new probe).
- The channel decides who can run what. Beta and Alpha channel installs get day-0 prerelease runs; Stable and LTSC do not run prerelease candidates automatically, and LTSC receives only security candidates (doc 42 Q4).

### 9.3 Self-update candidates

Resibo holds Supervisor self-update candidates to a higher bar (`proving_grounds_soak_days: 14`, deep-dive 38). Bazaar's equivalent set is Kodo, the Sentinel and the updater. For candidates of these modules the suite adds `pg.soak` with `deps.proving.selfUpdateSoakDays`, and a ring canary run on the owner's own instance before early. Rollback uses the existing A/B slot flip (doc 46 section 6.3), never a reverse migration.

### 9.4 Data line moves

For a database line move (18 to 19), the doc 42 section 10.3 procedure is unchanged. Proving Grounds supplies its step 1 preflight evidence ("a line-19 artifact exists for every extension") and runs the dry run of step 3 ahead of time and repeatedly, so the owner sees a passing dry run before approving. The move's automatic retry and rollback are doc 42's.

## 10. Where Resibo's choices conflict with Bazaar, and how each is handled

### 10.1 Human decides what to test

Resibo: the watcher never triggers a test, and the changelog reader never builds a candidate (`services/update/proving_grounds/CLAUDE.md`). Bazaar: the update policy auto-adopts security patches. Handled by `deps.proving.trigger` (section 8.5), with Resibo's behaviour as `manual`. Not a separate module; the setting is enough because both behaviours use the same code.

### 10.2 Docker as the isolation mechanism

Resibo resolved isolation as Docker. Bazaar forbids requiring users to install a tool (doc 42 D5, D7). Separate module: `provinggrounds-docker` (optional, developer and CI) beside the default `provinggrounds-scratch` and `provinggrounds-vm`. Providers are plain modules on Core, with no selector (owner, 2026-11-19), and the capability rule in section 8.4 chooses by required isolation.

### 10.3 Changelog as a checked-in repo file

Resibo writes `docs/CHANGELOG.md` into its own repo so LLM dev sessions see it. A running Bazaar server must not write into a git working tree. Separate module: `depwatch-repochangelog` for developer instances, off by default, plus the Vault surface for everything else. They coexist.

### 10.4 Build-time tooling versus runtime watcher

`docs/FORWARD-FACING.md` runs a canary, Dependabot and Renovate for the repo. Those open PRs; they do not know what an install hosts. Not duplicated and not replaced. The watcher reads the same public registries for what an install hosts and for operator subjects. Where an operator is the Bazaar developers, the watcher can also be pointed at the SBOM subject set, and the dated audit table can be generated from `depwatch_fact_state` instead of by hand (a follow-up, section 13 item 14). Known overlap, deliberately accepted: both read NuGet.

### 10.5 Hard-coded inventory

Resibo's inventory is project-specific (14 entries). Bazaar's is derived (section 6.3) so it cannot drift from what is installed.

### 10.6 Keymaster

Not adopted (CONVERGENCE.md row 5: stays in Resibo).

### 10.7 First observation rule

Kept, but baselined to the installed version (section 6.5). A genuine behaviour change from Resibo, noted here.

### 10.8 Python-first scope

Resibo is Python-only (PyPI, trove classifiers). Bazaar is .NET-first; PyPI exists only for Python engine runtimes (doc 20) and is one provider among five.

## 11. Interaction with existing modules

- **`Bazaar.Server.Modules` (module manager)**. Two narrow ports, both additive and neither implemented in this doc:
  - `IUpdateCandidateFeed` (in `Bazaar.Server.Modules.Abstractions`): the watcher publishes `UpdateCandidateAvailable(moduleId, line, version, versionType, category, isSecurity, channelHint, findingId)`; the manager builds `UpdateCandidate`.
  - `IMatrixVerdictSource` (section 8.2): the manager reads it to set `MatrixPassed` and attaches `ResultId` to the decision record. `Untested` and `Stale` map to `MatrixPassed = false`. For a policy that would be `Auto`, an `Untested` result downgrades the decision to `Manual`, which is the safe direction and uses the evaluator's existing `MatrixFailed` rule semantics. Whether `Untested` should reuse `MatrixFailed` or get its own `UpdateRule` value is OPEN (section 14), since this changes an enum in an existing module.
  - Follow-up in that module: move `IDryRunExecutor`, `DryRunStepResult`, `DryRunPlan` to `Bazaar.Server.Modules.Abstractions`, so Proving Grounds references only Abstractions.
- **Kodo (doc 46)**. Probes above; findings with severity high raise alerts; the ring gate and the Kodo health contract tests are reused by the proving suite. Kodo is not changed.
- **Doc 70 issue filer**. The watcher publishes `DependencyFinding` through `IFindingSink`; Proving Grounds publishes a `ProvingFailureFinding` for a failed security candidate. Doc 70 owns dedupe state and filing. This doc does not file anything.
- **Vms (docs 16, 50)**. The VM provider leases a child VM through the lifecycle service and respects the VM caps.
- **Backup (doc 33)**. The dry-run probe restores into scratch using the restore flow; evidence bundles are included in exports.
- **Identity Service (doc 14)**. GitHub App key for authenticated polling; tokens for gated artifact downloads. Providers never hold keys.
- **Vault (doc 18)**. The Vault surface writes Markdown; Postgres indexes it.
- **Settings, audit, approvals (docs 03, 15, 48)**. All rows above go in the registry; relaxing a floor-adjacent setting needs owner-tier approval. All audit entries use `Principal` text.
- **Client panes**. Both client modules register panes through the pane registry (doc 41), with mnemonics `upstream` and `proving` (PROPOSED, subject to the mnemonic table), no Ctrl chords, accessible names, and a text alternative for any chart.

### Audit events

All written through `IAuditLog.AppendAsync` with `Actor` as `Principal` text.

| Kind | Actor | When |
|---|---|---|
| `depwatch.subject.added` / `.removed` | user or service Principal | Operator subject change |
| `depwatch.change.recorded` | `service:depwatch` | A fact change was stored (payload: subject, kind, previous, value, heading, is_security) |
| `depwatch.finding.state` | user, agent or `service:depwatch` | Finding acknowledged, dismissed, or turned into a candidate |
| `depwatch.source.degraded` / `.recovered` | `service:depwatch` | Breaker opens or closes |
| `depwatch.setting.changed` | user | Any `deps.watcher.*` change |
| `proving.candidate.created` | requester Principal | Manual or automatic (`system:policy`) |
| `proving.run.started` / `.finished` | `service:provinggrounds` | With result id, outcome, evidence hash |
| `proving.gate.decided` | `service:provinggrounds` | Every promotion decision, allowed or not |
| `proving.security.event` | `service:provinggrounds` | Signature failure, hash mismatch, environment escape attempt |
| `proving.setting.changed` | user | Any `deps.proving.*` change |

## 12. Security and privacy

- Pollers contact public services only, with subject names and a generic client name. No install id, no company name, no GC or Vault content, no model prompts. This is a floor (section 6.8).
- Upstream data is untrusted. Version strings, release notes and issue titles are parsed with bounded lengths, escaped when displayed, and never used as prompts for an agent without the taint rules of doc 15. Any agent that reads the changelog sees it as untrusted external content. The classifier never executes or interprets text.
- Release-note text is never an authority for `IsSecurity` (section 6.6).
- Candidate artifacts are untrusted code until verified. They run only inside a provider environment with no network, no host secrets and no production data. A candidate that attempts to escape (writes outside the scratch root, opens a non-loopback port) fails the run and writes a security audit event. The VM provider is the answer for third-party executables.
- Probe output is scrubbed with the doc 23 server scrubber before it is stored as evidence, and row content from restored snapshots never leaves the scratch environment (section 8.6).
- GitHub authentication uses the GitHub App, not a personal token (CLAUDE.md; Resibo does the same: `telemetrees/CLAUDE.md`). The key stays in the Identity Service.
- Diagnostic data leaving the install is redacted deterministically before it reaches the issue filer. Resibo's CLAUDE.md states this rule but its docs describe no redaction mechanism; Bazaar does not copy that gap. Doc 70 owns the redaction implementation; this doc supplies only the public fields.
- Role permissions: `deps.view`, `deps.watcher.manage`, `deps.proving.run`, `deps.proving.approve_override`, `deps.proving.settings`. Defaults grant the owner all and others none. There is no override that turns a failed result into a pass; `approve_override` only lets the owner adopt anyway with a recorded reason, shown as "adopted without a passing proof" in the decision record (the module manager decides whether to allow it; OPEN).

## 13. Testing and work packages

### 13.1 Test plan (xUnit v3)

- **Classification**: table-driven tests over version pairs per ecosystem, prerelease markers, advisory ranges, unparseable versions. Property test: `IsSecurity` is true only when an advisory fact is present.
- **Poll pass**: fake pollers; assert a pass survives one unreachable source, that an unreachable result is never stored as a value, first observation is not a change, a newer-than-installed first observation is a finding, and a retried pass writes once (mirrors the tests in Resibo's deep-dive 37 section 8: multi-poller coverage, no silent release-only fallback, classifier detection).
- **Coverage**: duplicate poller for one key fails startup unless both `Corroborating`; declared kind without poller appears in the coverage report.
- **Free threading**: classifier present wins; tracker only when classifier absent; nothing is `unknown`, not `unsupported`.
- **Surfaces**: idempotent per line, heading placement, failed delivery retried from the outbox and never loses the observation.
- **Gate**: the fixture matrix of section 8.9; stale, incomplete suite, and could-not-run are never allowed; a bypass test over the release pipeline adapter (Resibo's promotion-gate bypass test).
- **Isolation**: a failing candidate never affects the running instance (Resibo's isolation test), a provider reporting unavailable yields `CouldNotRun`, an attempted escape fails the run, teardown always runs.
- **Probes**: each built-in probe against fakes; the dry-run probe over the existing `DryRunRunner` with a fake `IDryRunExecutor`.
- **Contract tests**: both modules inherit `ModuleHealthContractTests`; providers as well.
- **Architecture tests** (`tests/Bazaar.Architecture.Tests`): no reference from `Bazaar.Server` or `Bazaar.Client.Core` to these modules; providers reference only Abstractions; Abstractions have no third-party packages.
- **Client**: `Avalonia.Headless` tests for the two panes (list, filters, empty state, error state, keyboard navigation and accessible names).
- **Integration**: Postgres via the existing test container for migrations and repositories; a live-network test of each provider is manual and off in CI (recorded transports in CI).

### 13.2 Work packages

Each is one project or one file group with its own tests, owner-merged. Order is by dependency.

| # | Package | Project | Depends on |
|---|---|---|---|
| 1 | Watcher contracts and settings names | `Bazaar.Server.DependencyWatcher.Abstractions` | none |
| 2 | Proving contracts, gate, suite requirement | `Bazaar.Server.ProvingGrounds.Abstractions` | none |
| 3 | Watcher core: registry, scheduler, fold, freshness, coverage | `Bazaar.Server.DependencyWatcher` | 1 |
| 4 | Classifier (versions, categories, advisories) | same, `Classification/` | 1 |
| 5 | Persistence and migrations (`depwatch`) | same, `Data/` | 1, 3 |
| 6 | Changelog orchestration, outbox, `IChangelogSurface` | same, `Surfaces/` | 3, 5 |
| 7 | Providers: NuGet, then GitHub, PyPI, OsPackage, Osv (five independent packages) | `Bazaar.Server.DependencyWatcher.<Provider>` | 1 |
| 8 | Surfaces: Vault, GC, RepoChangelog (three independent) | `Bazaar.Server.DependencyWatcher.*Surface` | 6 |
| 9 | Findings, `IFindingSink`, candidate feed port | `Bazaar.Server.DependencyWatcher` | 4, 6 |
| 10 | Watcher Kodo contract and probes | same, `Health/` | 3 |
| 11 | Proving core: candidates, orchestration, probes, history, evidence | `Bazaar.Server.ProvingGrounds` | 2 |
| 12 | Proving persistence (`proving-grounds`) | same, `Data/` | 2 |
| 13 | Built-in probes (verify, manifest, licence, contract, dry-run, matrix) | same, `Probes/` | 11, plus Modules.Abstractions move of the dry-run types |
| 14 | Environment providers: Scratch, then Vm, then Docker (three independent) | `Bazaar.Server.ProvingGrounds.<Provider>` | 2 |
| 15 | `IMatrixVerdictSource` implementation and module manager adapters (cross-module PR) | `Bazaar.Server.Modules*` | 11, 9 |
| 16 | Release pipeline pre-ring adapter and bypass check | `Bazaar.Server.ProvingGrounds` | 11, 12 |
| 17 | Proving Kodo contract and probes | same, `Health/` | 11 |
| 18 | Client panes | `Bazaar.Client.DependencyWatcher`, `Bazaar.Client.ProvingGrounds` | 9, 11 |
| 19 | Generate the dated audit table from `depwatch_fact_state` (optional) | tooling | 3 |

Docs first: this doc lands as its own docs PR before any code PR, and each code PR states `Docs section this implements: docs/design/71-dependency-watcher-and-proving-grounds.md, section N`.

## 14. Open questions

Defaults are what the design uses until the owner decides, per the configurability rule.

1. **Doc 70 interface.** The issue filer's names and fingerprint format were not available when this was written. Default: `IFindingSink` and the fingerprint in section 6.10; doc 70 adapts or this doc is amended.
2. **`Untested` in the evaluator.** Reuse `UpdateRule.MatrixFailed` or add `MatrixUntested`. Default: add a new rule value, since the audit record should say why. Needs a change in `Bazaar.Server.Modules`.
3. **Where the dry-run types live.** Default: move to `Bazaar.Server.Modules.Abstractions` (section 11).
4. **OS package sources first.** Which distributions and which Windows source ship first. Default: the distribution used by the Linux VM golden image, and the Windows package manifest repository. Not verified here.
5. **Advisory service.** Which public advisory API `depwatch-osv` uses and its terms. Default: an OSV-style API behind `IUpstreamHttp`; must be verified before build, including rate limits and licence of the data.
6. **Override of a failed proof.** Whether the owner may adopt over a failed result at all. Default: no for security-critical packages; for others allowed with a recorded reason and an audit marker. DECISIONS-LOG entry needed.
7. **Soak default.** The 14-day self-update soak is Resibo's number, not tested on Bazaar. Default 14, owner may change.
8. **`model_currency`, `dataset_freshness`, `regulatory_value` pollers.** Which Bazaar modules own them and what the subjects are. Default: none shipped.
9. **Shared downloader.** Resibo shares one downloader between initial install and proving. Bazaar's install path is `Bazaar.Install`/`Bazaar.Updates`. Whether a shared downloader exists or is needed is not checked. Default: use the existing verifier, add a downloader when a model candidate appears.
10. **VM capacity.** How many proving runs may share the server with agent VMs on the owner's PC. Default `maxConcurrent = 1`, VM runs below agent priority. Needs the doc 16 sizing benchmark.
11. **Offline and mirror mode format.** How a mirrored snapshot is produced and signed. Default: not designed; `offline` shows "no data".
12. **Mnemonics.** `upstream` and `proving` are PROPOSED for the doc 41 table.
13. **Repo changelog heading policy for developer instances.** Whether Bazaar's own repo wants the file at all. Default: off.
14. **Replace the dated audit table.** Whether `FORWARD-FACING.md` should be generated from the watcher's database. Default: no, keep manual until the watcher has run for several cycles.
