# 73. Content screening: the Content Screening module (file type, polyglot, archive bomb, malware)

Status: PROPOSED. Design intent only; no code, migration or setting exists yet. The owner's direction of 2026-11-19 to adopt Resibo's Content Security feature into Bazaar as a module is DECIDED ("incorporate everything from Resibo that can benefit Bazaar; where it conflicts, make it a separate module"). Everything below that direction, including every default, threshold and name, is PROPOSED unless marked otherwise. Owner questions: 10 (section 17).
Depends on: 03-server-host-and-providers.md (settings registry, provider seam), 04-postgres-schema-and-migrations.md (conventions), 14-identity-service-and-passport.md (per-person encryption), 15-security-taint-and-approvals.md (taint, reader and actor sessions, quarantine, approvals, `audit_log`), 22-email-subsystem.md (MIME limits, deterministic screens, quarantine), 40-core-feature-modules-audit.md (module layout), 44-data-portability.md (staging, parser isolation), 46-kodo-health-module.md (module health contract), BAZAAR-COMPONENTS.md (sections 4.2 Principal, 5 migrations, 6 boundaries), `Bazaar.Server.Blobs.Abstractions`, `Bazaar.Audit.Abstractions`, `Bazaar.Approvals.Abstractions`.
Used by: 15 (quarantine subjects), 16 (guest downloads), 21 (GitHub artifacts), 22 (attachments), 33 (outbound exports), 36 (community attachments), 42 and 07 (module and Soul uploads), 44 (import staging), 46 (probes), 51 (web admin read port).

## 1. Purpose

Every file that enters a Bazaar installation from outside is untrusted bytes. Today the design has pieces of a defence: doc 22 holds executables and macro files by deterministic screens, doc 44 section 8.3 limits expansion ratio and rejects bad archive paths, doc 07 quarantines imported Souls, and doc 15 gives the taint and quarantine model. Nothing checks what a file really is, whether it is two formats at once, whether an archive is a bomb before anyone opens it, or whether an antivirus engine objects. Each surface would otherwise grow its own checks.

This module is the one shared place that answers "may these bytes go on, be held for the owner, or be refused", for every entry point, with one verdict record per file. It is a security gate, not a trust upgrade: a file that passes screening is still `external` and still taints any session that reads it (section 8).

The module adopts Resibo's Content Security API (`core/content_security/`, deep dive 27) and extends it for Bazaar's threat model: streams instead of in-memory bytes, many archive formats, multiple antivirus provider modules with a policy, definition freshness, re-screening when definitions change, derived-file lineage, and owner-signed release.

## 2. Decided inputs

- Owner, 2026-11-19: adopt Resibo features that benefit Bazaar; overengineer; where a Resibo design conflicts with Bazaar's, split it into a separate module; account for everything (relayed in the task brief; source of the DECIDED status above).
- Every feature is its own module `Bazaar.Server.<Name>` plus `Bazaar.Server.<Name>.Abstractions` (plus a `Bazaar.Client.<Name>` module when it has a client side). Nothing goes in `Bazaar.Server` or `Bazaar.Client.Core` (CLAUDE.md; BAZAAR-COMPONENTS section 6).
- Shared libraries and modules hold contracts and logic only, no third-party dependencies. Providers are separate modules, plain modules on Core, with no selector (task brief, 2026-11-19).
- Actor attribution is `Principal` (`kind:id`, BAZAAR-COMPONENTS section 4.2, owner 2026-11-19).
- Taint is monotonic and server-set. External content taints hard. Release from quarantine keeps taint (docs 15 and 22, DECIDED in DECISIONS-LOG round 2 and C06).
- Quarantine release is owner only; a reader can escalate to quarantine but never release (doc 22, doc 15).
- Configurability rule (DECISIONS-LOG round 13): open values are operator settings with safe defaults; DECIDED items and security invariants are not settings; settings may tighten and never loosen below the floor.
- Wire format is JSON via `IWireCodec` (spike S6). Resibo's gRPC and protobuf surface is not carried over.
- No web client; the admin panel is in the apps (doc 51 reads through a port).
- Module health contract is mandatory (doc 46 section 10).

## 3. What this document does not decide

- Taint rules and the tag set (doc 15 owns them). This module consumes the tag and never writes it.
- The email pipeline order and the `mail_triage` hand-over (doc 22). Doc 22 will call this module at its step 4; this doc does not re-specify the pipeline.
- Portability importer formats and staging (doc 44). The import job calls this module at its Detect stage.
- Which antivirus engines ship in a given install. Providers are separate modules; section 6.5 lists proposals only.
- Packaging of an antivirus daemon (for example ClamAV) as a dependency module under doc 42. OPEN (section 17, Q1).
- Legal exposure of uploading file content to a cloud engine. Operator choice with the protective default (section 9).

## 4. Module layout

| Item | Value |
|---|---|
| Module id | `content-screening` (settings section `Bazaar:ContentScreening`, setting keys `screening.*`) |
| Contracts | `Bazaar.Server.ContentScreening.Abstractions`: screener, provider, inspector, verdict, policy, sink interfaces and records. No implementation, no third-party package |
| Implementation | `Bazaar.Server.ContentScreening`: file-type sniffer, polyglot detector, archive inspectors for formats the BCL reads, pipeline, verdict policy, verdict store, rescan service, decorators, Kodo probes, `AddBazaarContentScreening()` |
| Provider modules (each its own project pair, each registers `IScreeningProvider`) | `Bazaar.Server.ContentScreening.ClamAv` (clamd `INSTREAM` over a socket, `clamscan` fallback), `Bazaar.Server.ContentScreening.VirusTotal` (cloud, hash lookup by default), `Bazaar.Server.ContentScreening.WindowsDefender` (Windows host only; OS-specific, so it is a provider by the doc 03 rule). Proposals, section 6.5 |
| Archive inspector modules (each registers `IArchiveInspector`) | `Bazaar.Server.ContentScreening.Archives.SevenZip` (7z and similar; the BCL cannot read them, so a third-party parser lives in its own module, out-of-process, section 6.3). Zip, tar and gzip are in the main module (BCL only) |
| Client module | `Bazaar.Client.ContentScreening`: the Held files pane (owner review of `Review` and `Deny` verdicts, provider status). References `Bazaar.Client.Core` only. Pane registered through the pane registry (doc 41) |
| Component manifest | `Id = content-screening`; hard dependencies `audit`, `blobs`; soft dependencies `approvals` (owner-signed release), `kodo`, `agent-safety` (quarantine adapter), `web-admin`. A missing hard target stops start; a missing soft one degrades only what names it (BAZAAR-COMPONENTS 4.1) |
| Migrations | `src/Bazaar.Server.ContentScreening/Data/Sql/content-screening/NNNN_*.sql`, namespace `content_screening`. The folder equals the component id so the mismatch recorded in doc 40 item 7 does not recur. Tables are prefixed `screening_`. Bazaar.Server has no change |
| Licence | AGPL-3.0, as first-party core code (CLAUDE.md). Provider modules may use any licence under the module exception |

Boundary rules this module satisfies (BAZAAR-COMPONENTS section 6): the implementation references only `Bazaar.Contracts`, `Bazaar.Sdk`, its own abstractions and the abstractions of its dependencies (`Bazaar.Server.Blobs.Abstractions`, `Bazaar.Audit.Abstractions`, `Bazaar.Approvals.Abstractions`, the doc 46 health abstractions). Consumers (mail, GitHub, portability, VMs, Souls, module manager) reference `Bazaar.Server.ContentScreening.Abstractions` only.

## 5. Concepts

- **Screening request.** A stream (never a whole-file byte array), the surface it came from, the claimed filename and MIME (recorded, never trusted), the source trust tag, the acting `Principal`, and the blob hash when the content is already in the blob store.
- **Verdict.** One of three decisions. `Pass`: nothing objected, the bytes may proceed under their existing taint. `Review`: not condemned, not releasable by the system; the owner decides. `Deny`: refused. There is no fourth value. Failures, timeouts, missing providers and unreadable files are `Deny` (section 7). This copies Resibo's rule that the wire format has no way to say "could not check" (`core/content_security/CLAUDE.md` gotchas; deep dive 27 section 4).
- **Surface.** A named entry point (section 11) with its own policy row.
- **Pass one and pass two.** Pass one is static and runs without extracting anything: sniff, claimed-type mismatch, polyglot, dangerous-class table, archive preflight. Pass two is each extracted member and the antivirus fan-out. Resibo's "two-pass for containers" (deep dive 27 section 3) is the same idea; here pass two also covers non-archive files (antivirus) so every file gets both.
- **Derived file.** A new blob produced from another one (for example an archive with a member stripped). It has its own verdict and a lineage link to the original (section 7.6).

## 6. Design

### 6.1 Contracts (sketch)

```csharp
// Bazaar.Server.ContentScreening.Abstractions
public enum ScreenDecision { Pass, Review, Deny }          // no Unknown, no Skipped: by design (section 5)

public enum ScreenSurface { MailAttachment, GitHubArtifact, PortabilityImport, BrowserDownload,
                            VmDownload, SoulUpload, ModuleUpload, MemberUpload, CommunityAttachment,
                            OutboundExport, Other }          // Other needs an operator-registered surface id

public sealed record ScreenRequest(
    Stream Content,                  // seekable, or the pipeline spools it to a bounded temp file first
    string SurfaceId,                // ScreenSurface name or a registered custom id
    SourceTrust SourceTrust,         // from doc 15; read for policy, never changed here
    Principal Requester,             // Bazaar.Core Principal (doc BAZAAR-COMPONENTS 4.2)
    string? ClaimedFileName,
    string? ClaimedMimeType,
    string? BlobHash,                // sha256 hex when stored in the blob store
    string? ParentVerdictId,         // set for derived files and archive members
    string? CorrelationId);          // import job id, mail_message id, agent session id, etc.

public sealed record ScreenVerdict(
    Guid VerdictId, ScreenDecision Decision, string ReasonCode,
    string DetectedType,             // real type from bytes, never the claim
    bool ClaimMismatch,
    IReadOnlyList<FileClass> Classes,        // Executable, Script, MacroOffice, ArchiveNested, ...
    ArchiveFinding? Archive,
    IReadOnlyList<ProviderResult> Providers, // each provider's raw outcome, never folded away
    string PolicyVersion, string EngineSetHash,
    DateTimeOffset At, Principal ScreenedBy);

public interface IContentScreener
{
    ValueTask<ScreenVerdict> ScreenAsync(ScreenRequest request, CancellationToken ct);   // never throws for scan failures
    ValueTask<ScreenVerdict?> GetVerdictAsync(string blobHash, string surfaceId, CancellationToken ct);
    ValueTask<ScreenVerdict> RescreenAsync(Guid verdictId, Principal requester, CancellationToken ct);
}

public interface IScreeningProvider      // implemented by antivirus provider modules
{
    string ProviderId { get; }
    string EngineFamily { get; }          // providers sharing a signature database share a family (6.4)
    long MaxBytes { get; }
    ValueTask<ProviderStatus> GetStatusAsync(CancellationToken ct);   // available, definitions age, version
    ValueTask<ProviderResult> ScanAsync(Stream content, ProviderScanContext ctx, CancellationToken ct);
}
public enum ProviderOutcome { Clean, Suspicious, Malicious, Error }   // Error includes timeout and over-size

public interface IArchiveInspector       // zip, tar, gzip built in; 7z and others via inspector modules
{
    IReadOnlyCollection<string> Formats { get; }
    ValueTask<ArchiveFinding> PreflightAsync(Stream archive, ArchiveLimits limits, CancellationToken ct); // metadata only
    IAsyncEnumerable<ArchiveMember> OpenMembersAsync(Stream archive, ArchiveLimits limits, CancellationToken ct); // bounded streams
}

public interface IScreeningQuarantineSink    // adapter to doc 15's quarantine table
{
    ValueTask<Guid> HoldAsync(ScreenVerdict verdict, string subjectKind, string subjectId, Principal by, CancellationToken ct);
}
public interface IScreeningReleaseService    // owner-only; see 6.8
{
    ValueTask<ReleaseResult> ReleaseAsync(Guid verdictId, OwnerApproval approval, CancellationToken ct);
}
```

Two properties are tested, not trusted (section 15): no type in the abstractions can return or accept a value that lowers `SourceTrust` or clears a `TaintState`, and `ScreenDecision` has exactly three members. The reflection test mirrors Resibo's documented field-set assertion against the generated descriptor (`core/content_security/CLAUDE.md`).

### 6.2 Pipeline

For each request, in order. Steps 1 to 6 are free (no provider is asked) and any of them can end the run.

1. **Admit.** Check size against `screening.max_file_bytes`. Over the cap is `Deny` (`TOO_LARGE`). Spool non-seekable streams to a bounded temp file in an access-restricted scratch directory; delete on completion.
2. **Hash.** SHA-256 while spooling. Look up a cached verdict for `(hash, surface policy version, engine set hash)`. A cached `Pass` is reused only while every provider that produced it still reports current definitions (6.6). A cached `Deny` or `Review` is always reused.
3. **Sniff.** Real type from magic bytes and structure (6.3). The claimed MIME and extension are recorded, and a mismatch sets `ClaimMismatch`. Whether a mismatch is a `Review` or a `Deny` is per surface (section 11).
4. **Class table.** Map the detected type to a file class: executable (PE, ELF, Mach-O, MSI, JAR, APK), script (shell, PowerShell, batch, VBS, JS standalone), shortcut (`.lnk`, `.url`, `.desktop`), disk image (ISO, VHD, IMG), macro-capable Office (OOXML with `vbaProject.bin`, legacy OLE with macro streams), PDF with JavaScript or launch actions, HTML and SVG with script, container (archive). The table is data in the module, not a setting; surfaces choose how each class is handled (hold, deny, allow).
5. **Polyglot check** (6.3).
6. **Archive preflight** for containers (6.4). A failed preflight is `Deny` and nothing is extracted.
7. **Antivirus fan-out** (6.5), with the verdict policy (6.6).
8. **Pass two for containers.** If the preflight cleared, extract members one at a time through the inspector's bounded streams, and run steps 3 to 7 on each member with the `ParentVerdictId` set. Members are never written to disk unless a provider needs a file path, and then to the same restricted scratch directory. The container verdict is the worst member verdict (Deny over Review over Pass).
9. **Record.** Write the verdict (6.7), write the audit entry (section 9.2), and, for `Review` and `Deny`, call `IScreeningQuarantineSink` (section 8).

The pipeline never lets an exception cross its boundary. Every `try` exists to turn a failure into a `Deny` with a reason code, as Resibo's `core/content_security/CLAUDE.md` states (`safe=True` has exactly one origin, reached only when every enabled, available provider returned clean).

### 6.3 File type sniffing and polyglot detection

- **Sniffing.** A signature table of magic bytes at offset plus structural confirmation for the formats that matter (PE header chain, ELF class, OOXML by `[Content_Types].xml` and the main part name, OLE compound file directory, PDF header and trailer, common images, audio, video, text). It is pure, synchronous and in the module (no third-party library). Unknown binary content is `application/octet-stream` and is treated as the most restricted class the surface offers. Text is detected by a bounded valid-UTF-8 and control-character check, never by extension.
- **Claim mismatch.** Recorded, never trusted. A caller that acts on `ClaimedMimeType` instead of `DetectedType` has reintroduced the hole (Resibo deep dive 27: never trust an extension or client-supplied MIME type). The module exposes `DetectedType` only as the trusted field.
- **Polyglot detection.** The detector always looks for a second independent format signature after the primary header, regardless of what matched first (Resibo's docs require polyglot detection that does not stop at the first plausible format, deep dive 27 section 8; the specific signatures and windows below are Bazaar's own, PROPOSED, not from Resibo): a ZIP end-of-central-directory record searched backwards from the end of any non-ZIP file within the 65,535-byte comment window; PDF, PNG, JPEG and GIF signatures searched after the header region of a different primary type. Added for Bazaar: a PE or ELF header found after an image or document header, an OLE header inside an OOXML file, an HTML script block inside an SVG or image comment, and a ZIP local-file header at offset zero of a file that is also a valid PDF. 
- **Known false-positive guard.** Bazaar's reasoning, PROPOSED (not from Resibo): PDF legitimately embeds image streams, so scanning every PDF for embedded JPEG and PNG signatures would flag ordinary scanned PDFs. Raster signatures are not searched inside container types that legitimately embed them. The suspicious direction (an image that also parses as a PDF, an executable header after a document header, a ZIP EOCD in a non-ZIP) stays fully checked. Bazaar applies the same guard to OOXML and ODF, which embed images by design, with a test per container type.
- **Outcome.** A polyglot is `Review` by default (Resibo's proto returns `safe=False` plus `requires_staff_review`; it is neither passed nor condemned). On surfaces where an agent could open the file, it is `Deny` (section 11).
- **Parsers for risky formats.** Sniffing reads headers only. Any module that must parse a hostile format body (7z, rar, legacy OLE) runs it out-of-process under the doc 44 section 8.3 confinement (no network, input read-only, output stream only, wall-clock timeout) and under the out-of-process contract in BAZAAR-COMPONENTS section 7.

### 6.4 Archive bomb and archive hazard checks (pre-extraction)

Resibo's deep dive 27 section 3 requires a container-level check before any extraction (entry count, size and compression-ratio sanity checks) and states no numeric limits. Bazaar keeps the container-first rule and sets its own limits (2,000 entries, 1 GiB, 100:1 aggregate, 300:1 per entry), PROPOSED (Bazaar's design, not from Resibo) and to be tuned against measured imports and attachments. Bazaar adds more checks, because declared sizes in a central directory can lie.

- **Formats.** Zip (including Zip64), tar (plain and through a bounded gzip stream), gzip single-member, and, through inspector modules, 7z and others. A file detected as an archive format with no installed inspector is `Deny` (`NO_INSPECTOR`). An archive that cannot be pre-checked is never extracted.
- **Declared-size limits** (before extraction): entry count, total declared size, aggregate ratio, per-entry ratio, maximum declared single-entry size, maximum path length, maximum nesting depth.
- **Structural hazards** (new): overlapping local file entries (the zip-quine overlap bomb), declared sizes that disagree with the data descriptor, duplicate entry names, entry offsets outside the file, absolute paths, `..` segments, drive letters and UNC paths, symlinks and hard links, device files, names that collide when case-folded, Windows reserved names, trailing dots and spaces, control characters in names, and encrypted entries (a member that cannot be scanned is `Review`, never `Pass`). The path and link rules are the same ones doc 44 section 8.3 lists for import parsers; this module is the single implementation and doc 44 calls it.
- **Actual-size enforcement** (new): in pass two the inspector hands out a bounded stream that counts real decompressed bytes. It aborts when the running total passes the declared size, the per-entry cap, the remaining cumulative budget, or the ratio cap. A lying header is therefore caught at extraction, not trusted. The budget is cumulative over the whole archive tree.
- **Nesting.** The default is no nested archives: a member that is itself an archive is held, as doc 22 already says ("nested archives are held"). `screening.archive.max_depth` can raise it up to a hard ceiling of 4, and a member-of-a-member is screened with the same cumulative budget. OOXML and ODF documents are zip containers but are not treated as nested archives (Bazaar's own rule, PROPOSED: they embed zip structure by design); they are screened as documents with macro and external-reference checks.
- **Result.** A failed preflight is `Deny` (`ARCHIVE_BOMB`, `ARCHIVE_HAZARD`). A clean preflight with bad members goes to remediation (6.9) or `Deny`.
- **Symmetry.** The same checks run on outbound archives the server itself builds (backup bundles, exports, doc 33 and doc 44 export), so a corrupted or tampered export is caught before it is sent. This is Resibo's "outbound archive-bomb symmetry" (deep dive 31 section 10). It is on by default for outbound mail attachments and exports.

### 6.5 Malware scanning through provider modules

- **Discovery.** The pipeline resolves `IEnumerable<IScreeningProvider>` from DI. Every installed provider module is asked, in parallel, each in its own bulkhead (a concurrency semaphore and a per-provider timeout). There is no selector and no "active provider" setting; installing or removing a module changes the set. This is Resibo's "more than one provider can run at once" (`CLAUDE.md` Provider Registry rule) expressed as Bazaar's plain-module rule.
- **Per-provider contract.** `GetStatusAsync` reports availability, engine and definition versions, and definition age. `ScanAsync` returns Clean, Suspicious, Malicious or Error. A timeout, a crash, an over-size file (above that provider's `MaxBytes`), a malformed reply and an unavailable engine are all `Error`. Providers never throw across the seam.
- **Proposed provider modules** (all PROPOSED; the owner picks what ships):
  - `Bazaar.Server.ContentScreening.ClamAv`: talks to a running `clamd` over `INSTREAM`. Resibo found `clamscan` reloads its whole signature database on every call (about 11 seconds, per Resibo's `core/content_security/CLAUDE.md`), and 8 concurrent scans failed closed against a 15-second client timeout; the daemon path was about 1 second warm (`core/content_security/CLAUDE.md`). So `clamd` is the primary transport and `clamscan` a disabled-by-default fallback. This module does not start or configure the daemon.
  - `Bazaar.Server.ContentScreening.VirusTotal`: cloud multi-engine. Hash lookup only by default. Uploading file content is a separate setting, off, because it sends the file to a third party (section 9).
  - `Bazaar.Server.ContentScreening.WindowsDefender`: Windows host only, behind the OS provider rule.
  - Other candidates, not designed here: a YARA-rules provider and a local known-bad-hash list provider.
- **Engine families.** A provider declares an `EngineFamily`. Providers that share a signature database (ClamAV over `clamd` and over `clamscan`) have one family, and the policy counts one vote per family, because running both adds no corroboration (Resibo `CLAUDE.md`: "they share one signature database"). If two same-family providers are installed, only the first available one is asked.

### 6.6 Verdict policy

The module decides; providers only report. Default policy `consensus`, copied from Resibo's resolved rule (deep dive 27 section 9; `core/content_security/CLAUDE.md`):

1. No provider available (none installed, or all unavailable or out of date): `Deny` (`NO_PROVIDER`).
2. Any provider `Error` (including timeout): `Deny` (`PROVIDER_FAILED`), even if another provider said clean. A caller cannot tell a verdict reached despite a failure from one that would have differed had the failed provider run (Resibo's stated reason for being stricter than it looks).
3. Every family `Malicious`: `Deny` (`MALICIOUS_CONFIRMED`).
4. Every family `Clean`: eligible for `Pass`.
5. Anything else (a Clean and Malicious split, or any `Suspicious`): `Review` (`PROVIDERS_DISAGREE`). Each provider's raw outcome is stored so the disagreement can be inspected, never folded into one boolean.

Bazaar adds a stricter alternative, `screening.verdict_policy = any-malicious-denies`, where one `Malicious` is `Deny` and the owner cannot release it through the normal route. Consensus is the default because it keeps Resibo's behaviour; the alternative is the tightening direction and is always allowed. The floor: no policy may produce `Pass` when a provider errored, when no provider ran, or when any provider said `Malicious` and the file was never reviewed by the owner.

- **Definition freshness.** A provider whose definitions are older than `screening.av.max_definition_age_hours` (default 72) counts as unavailable. A stale ClamAV database is a silent gap distinct from an outdated package (Resibo `CLAUDE.md`, deep dive 27 section 5.1, resolved there as a tracked-fact entry). The Kodo probe in section 12 reports the age.
- **Re-screening.** When a provider's definitions or engine version change, a background `RescanService` re-screens retained `Pass` verdicts for blobs still in use or in quarantine, oldest first, rate-limited (`screening.rescan.max_per_hour`). A changed verdict is a new verdict row, an audit entry, and a notice to the owner. A `Pass` that flips to `Malicious` for a blob already read by an agent session is a security event with the sessions listed (section 8).
- **Over-size.** If a file exceeds the smallest `MaxBytes` among the families required by policy, the result is `Deny` (`TOO_LARGE_FOR_ENGINE`), not a partial scan.
- **Timeouts.** `screening.provider.timeout_seconds` default 30 (PROPOSED, Bazaar's design, not from Resibo), with a total deadline per request. A timeout is `Error` and therefore `Deny`; the file is not retried inline. A queued retry may produce a new verdict later, which is how a transient outage ends without ever having passed anything in between.
- **No AV installed.** Because rule 1 denies, an install with no provider module would block all external files. That is intended for the default (section 17, Q1). An operator may set `screening.av.required = false` for chosen surfaces, with owner approval and an audit entry. Such verdicts carry `ReasonCode = AV_NOT_REQUIRED_BY_POLICY` and show as static-only in the pane. Floor: executable-class files, macro files and encrypted archive members are never `Pass` without an AV result.

### 6.7 Data

```sql
-- Namespace content_screening. Files: Data/Sql/content-screening/0001_verdicts.sql
CREATE TABLE screening_verdict (
    verdict_id       uuid PRIMARY KEY,
    parent_id        uuid REFERENCES screening_verdict (verdict_id),  -- archive member or derived file
    blob_hash        bytea,                      -- sha256; null when never stored
    surface_id       text        NOT NULL,
    decision         text        NOT NULL CHECK (decision IN ('pass','review','deny')),
    reason_code      text        NOT NULL,
    detected_type    text        NOT NULL,
    claimed_type     text,
    claim_mismatch   boolean     NOT NULL DEFAULT false,
    classes          text[]      NOT NULL DEFAULT '{}',
    source_trust     text        NOT NULL,       -- copied from the request, never altered
    policy_version   text        NOT NULL,
    engine_set_hash  bytea       NOT NULL,
    archive_finding  jsonb,
    requester        text        NOT NULL,       -- Principal canonical text kind:id
    screened_by      text        NOT NULL,       -- service:content-screening, or the rescan principal
    correlation_id   text,
    screened_at      timestamptz NOT NULL DEFAULT now(),
    superseded_by    uuid REFERENCES screening_verdict (verdict_id)
);
CREATE INDEX screening_verdict_blob ON screening_verdict (blob_hash, surface_id) WHERE superseded_by IS NULL;

CREATE TABLE screening_provider_result (
    verdict_id  uuid NOT NULL REFERENCES screening_verdict (verdict_id),
    provider_id text NOT NULL,
    engine_family text NOT NULL,
    outcome     text NOT NULL CHECK (outcome IN ('clean','suspicious','malicious','error')),
    signature_name text,                        -- engine's detection name, display only, never fed to a model
    engine_version text, definitions_at timestamptz,
    duration_ms int NOT NULL,
    PRIMARY KEY (verdict_id, provider_id)
);

CREATE TABLE screening_review (               -- owner decisions on review/deny verdicts
    review_id   uuid PRIMARY KEY,
    verdict_id  uuid NOT NULL REFERENCES screening_verdict (verdict_id),
    action      text NOT NULL CHECK (action IN ('release','keep_held','delete','export_for_analysis')),
    decided_by  text NOT NULL,                  -- Principal, kind:user for the owner
    approval_ref text,                          -- owner-signed payload reference (doc 32 5a)
    decided_at  timestamptz NOT NULL DEFAULT now(),
    note        text
);

CREATE TABLE screening_rescan_queue (
    verdict_id uuid PRIMARY KEY REFERENCES screening_verdict (verdict_id),
    reason text NOT NULL, enqueued_at timestamptz NOT NULL DEFAULT now(), attempts int NOT NULL DEFAULT 0
);
```

Verdict rows are append-only: a re-screen writes a new row and sets `superseded_by` on the old one. The audit log, not this table, is the tamper-evident record. A quarantined file's raw bytes stay in the blob store (encrypted per person under doc 14 and doc 22 section "Quarantine and reporting"); the verdict row holds no file text.

### 6.8 Quarantine linkage and owner release

- `Review` and `Deny` verdicts are linked to doc 15's `quarantine(id, subject_kind, subject_id, reason, created_by, released_by)` through `IScreeningQuarantineSink`. `subject_kind` is `blob` (or `mail_attachment`, `import_record`, `artifact`, as the surface names it), `subject_id` is the blob hash, `reason` is the verdict reason code, and `created_by` is `service:content-screening`. The existing server-side quarantine and context filter (`Bazaar.Server/Security/Quarantine`) is the adapter target for now; the adapter lives in the module that owns quarantine when it is extracted (section 17, Q5).
- Quarantined bytes are excluded from every agent context and from memory and Vault promotion, as doc 15 and doc 22 already require. Agents see a count, not names or text (doc 22 section "Inbound flow", step 7).
- **Release** is `Review` verdicts only, owner only, through `IScreeningReleaseService` with an owner-signed payload (doc 32 section 5a, `Bazaar.Approvals.Abstractions`: `OwnerSignedPayload`, `IOwnerSignatureVerifier`). A `Deny` for confirmed malicious content has no release; the owner may delete it or export it for analysis. A reader session, an actor session, an agent, a provider and this module itself can never release (doc 22 invariant, doc 15).
- Release changes where the bytes may go. It does not change their trust: a released file is still `external` and still taints (section 8).
- This replaces Resibo's "staff review" workflow, which Resibo left to Review/Flagging and Notifications (deep dive 27 section 1). In Bazaar the owner is the reviewer and the Held files pane is the queue.

### 6.9 Remediation (strip a bad member)

Resibo planned to strip one bad archive member and extract the rest, using Python 3.16 `zipfile.remove()` and `repack()` behind a `hasattr` check, falling back to rejecting the whole archive (deep dive 27 section 3.1). Bazaar does not depend on that API. The module can build a new archive without the named members using the BCL, writing it as a derived blob with `ParentVerdictId` set. The original stays quarantined and is never modified. The derived blob is screened from scratch. Default `screening.remediation.mode = reject` (Resibo's deep dive 27 section 3.1 also falls back to rejecting the whole archive when the capability is absent); `strip` is available per surface and needs an owner decision per file on external surfaces. Floor: a derived file never inherits `Pass` from its parent.

## 7. Fail closed, as a contract

The Resibo guarantee is carried over in full: if the scan fails, times out, or the service is down, every caller treats the file as unscanned and therefore unsafe (deep dive 27 section 4, proto header comment). Bazaar enforces it in four places.

1. **Type.** `ScreenDecision` has no value for "could not check". `ScreenAsync` never throws for scan failures; it returns `Deny` with a reason code.
2. **Single origin of `Pass`.** `Pass` is produced in exactly one method (the policy resolver). A test greps the module for any second construction site (Resibo's `core/content_security/CLAUDE.md`: "if you find yourself adding a second place that can set it, that is the bug").
3. **Callers.** The abstractions expose `IContentScreener` and a helper `ScreenedBlobAccess.OpenAsync(hash, purpose)`; a consumer that gets no verdict row gets a refusal, not the bytes. The module registers a decorator over `IBlobReadAccess` (`Bazaar.Server.Blobs.Abstractions`) that refuses reads of blobs whose newest verdict is `Review` or `Deny`, and blobs that have none when the blob came from an external surface (a blob with no verdict is unscreened, hence unsafe). Blobs needs no change: the decorator lives here.
4. **Unavailability.** If the module is disabled, consumers that declared it a hard dependency do not start. Consumers that declared it soft treat "module absent" as `Deny` for external-origin files (a consumer-level test per integration, section 15).

## 8. Taint: screening never clears it

- The request carries the `SourceTrust` tag only so policy can differ by origin. The verdict records the tag unchanged. No interface in the module accepts a taint state or returns a trust value (reflection test).
- A `Pass` means "nothing objected, and it may be handled under its existing rules". A file from email, a stranger's pull request or the web stays `external`, any session that reads it is hard-tainted, and release from quarantine keeps the taint (doc 22 section "Quarantine and reporting", doc 15). Resibo has no equivalent because Resibo has no agent taint model; this is the Bazaar addition that keeps screening from becoming a trust-laundering step.
- Agent reading is unchanged: no agent opens an attachment in v1, and `mail.attachments.agent_readable` stays off by default; any read is in a reader session (doc 22).
- Text inside a file never reaches a model through this module. Reason codes are server-written strings. Detection names from engines and filenames are displayed to the owner only after control-character stripping and length limits, and are never passed into a prompt (the taint rule treats engine output as untrusted text).
- A later `Pass`-to-`Malicious` flip finds sessions that read the blob (from the audit log and `session_taint.taint_sources`), raises a security event, and applies the doc 15 quarantine effects for the agent only if the owner chooses; the module does not auto-kill sessions.

## 9. Settings, audit, privacy

### 9.1 Settings (registry owned by doc 03; all PROPOSED; changes are audited)

| Key | Type | Default | Scope | Floor and notes | Change needs approval? |
|---|---|---|---|---|---|
| `screening.enabled` | bool | true | install | Cannot be false while any external surface is on | Yes, owner |
| `screening.verdict_policy` | enum `consensus`, `any-malicious-denies` | `consensus` | install | May tighten only | Yes, owner |
| `screening.av.required` | bool per surface | true | surface | False blocked for executable, macro and encrypted-member classes | Yes, owner |
| `screening.av.max_definition_age_hours` | int | 72 | install | Hard ceiling 168 | Yes, owner to raise |
| `screening.provider.timeout_seconds` | int | 30 | install | Ceiling 300; a timeout is always Deny | Yes, owner to raise |
| `screening.max_file_bytes` | int | 256 MiB | surface | Not above host storage and not above `portability.import.max_upload_bytes` on imports | Yes, owner to raise |
| `screening.archive.max_entries` | int | 2,000 | surface | PROPOSED Bazaar default; ceiling 20,000 | Yes, owner to raise |
| `screening.archive.max_total_uncompressed_bytes` | int | 1 GiB | surface | PROPOSED Bazaar default; ceiling 16 GiB | Yes, owner to raise |
| `screening.archive.max_ratio` | int | 100 | surface | PROPOSED Bazaar default. Effective value is the minimum of this and `portability.import.max_expansion_ratio` (doc 44) | Yes, owner to raise |
| `screening.archive.max_entry_ratio` | int | 300 | surface | PROPOSED Bazaar default | Yes, owner to raise |
| `screening.archive.max_depth` | int | 1 (no nested) | surface | Hard ceiling 4 | Yes, owner to raise |
| `screening.archive.allow_encrypted_members` | enum `review`, `deny` | `review` | surface | Never `pass` | Operator |
| `screening.remediation.mode` | enum `reject`, `strip` | `reject` | surface | `strip` on external surfaces needs per-file owner decision | Yes, owner |
| `screening.polyglot.on_agent_readable` | enum `deny`, `review` | `deny` | install | Cannot be `pass` | Operator |
| `screening.rescan.max_per_hour` | int | 200 | install | Floor 10 | Operator |
| `screening.cloud.upload_content` | bool | false | provider | Off means hash lookup only. Needs counsel and the data-handling wording of the provider (Q6) | Yes, owner |
| `screening.cloud.allow_private_sources` | bool | false | install | Files from private sources (member uploads, imports) are never sent to a cloud engine when false | Yes, owner |
| `screening.audit.pass_detail` | enum `rollup`, `every` | `rollup` | install | Non-Pass verdicts are always audited individually | Operator |
| `screening.verdict.retention_days` | int | 365 | install | Quarantined raw bytes follow `mail.quarantine.raw_retention_days` (doc 22) | Operator |
| `screening.outbound.enabled` | bool | true | install | Archive checks on archives the server builds | Operator |

Per-surface policy rows (what to do with each class and with a claim mismatch) live in `screening.surface.<id>.*` with the defaults in section 11; a row may tighten, never loosen below its default for external surfaces.

### 9.2 Audit events (via `IAuditLog`; `Actor` is the canonical `Principal` text)

| Kind | Written when | Actor |
|---|---|---|
| `screening.verdict.deny` | Any `Deny` | `service:content-screening`, with the requester inside the payload |
| `screening.verdict.review` | Any `Review` | same |
| `screening.verdict.pass_rollup` | Hourly count of Pass by surface (or per file when `pass_detail = every`) | `service:content-screening` |
| `screening.verdict.changed` | A re-screen flips a verdict | `service:content-screening` |
| `screening.release` | Owner releases a `Review` verdict | `user:<owner>` |
| `screening.delete` / `screening.export_analysis` | Owner deletes or exports a held file | `user:<owner>` |
| `screening.provider.changed` | A provider appears, disappears, goes stale or recovers | `system:` or `service:` per source |
| `screening.setting.changed` | Any setting above | the changing `Principal` (user, service or agent) |
| `screening.av_not_required` | A surface runs without AV by operator choice | the approving `Principal` |
| `screening.blocked_read` | A consumer tried to read a held blob | the requesting `Principal` (agent, user or service) |

Payloads carry hashes, reason codes, class names and counts. They never carry file content, extracted text or full engine output (BAZAAR-COMPONENTS 4.2 audit rule; doc 15 audit).

### 9.3 Privacy

- Raw bytes of held files are personal data and are encrypted per person in the blob store as doc 22 requires; retention follows `mail.quarantine.raw_retention_days`. The verdict row stores hashes and codes only.
- A cloud engine is an outbound data flow. Default is hash lookup only, which discloses the file's hash. Content upload is a separate opt-in per provider and per source class, shown with its data-handling wording in the setup wizard, and it is an egress under doc 15's allowlist. Free tiers of some services train on or share submissions; the wizard warns (DECISIONS-LOG: free-tier data warning applies to model providers, and the same wording style is proposed here).
- Member uploads and imports are private by default and never go to a cloud engine unless `screening.cloud.allow_private_sources` is set.
- Erasure: when a subject is erased, held blobs under that subject follow doc 14 key destruction; verdict rows keep only the hash. Legal sufficiency is OPEN with the quarantine erasure question in doc 22 (Q3 there).

## 10. What is taken from Resibo, and what is changed

| Resibo item (path) | Taken | Changed for Bazaar, and why |
|---|---|---|
| Fail-closed contract, no "could not check" value (deep dive 27 section 4; `core/content_security/CLAUDE.md`) | Fully | `ScreenDecision` has three members; `Pass` has one origin. Enforced by tests, as Resibo does |
| Real type from magic bytes, never extension or claimed MIME (deep dive 27 section 1) | Fully | Streams and structural confirmation; the claim is stored, mismatch policy is per surface |
| Polyglot detection (deep dive 27 sections 1 and 8) | Fully | Added PE/ELF after document header, OLE in OOXML, script in SVG; the raster false-positive guard for PDF, OOXML and ODF is Bazaar's own, PROPOSED |
| Archive bomb preflight: entry count, total size, ratios, nested archives (deep dive 27 section 3) | Container-first rule; the numeric defaults 2,000 / 1 GiB / 100:1 / 300:1 are Bazaar's, PROPOSED | Metadata is not trusted: overlap, descriptor mismatch, path hazards, real-byte counting during extraction; more formats; nested default is "held" to match doc 22 |
| Two-pass scanning: container, then every member (deep dive 27 section 3) | Fully | Pass two also runs the AV fan-out on non-archives; cumulative budget over the tree |
| Provider registry with several providers at once (deep dive 27 section 2) | Fully | No registry or selector: plain provider modules resolved from DI (task brief); engine families so shared databases count once |
| Consensus policy: unanimous Malicious denies, split goes to review, provider error denies (deep dive 27 section 9; `core/content_security/CLAUDE.md`) | Fully, as the default | Optional stricter `any-malicious-denies`; floors in 6.6 |
| ClamAV via `clamd` daemon, `clamscan` fallback (`core/content_security/CLAUDE.md`) | As the proposed `ClamAv` provider module | Separate module with its own project pair; the daemon packaging question is OPEN |
| VirusTotal as cloud alternative (deep dive 27 section 2) | As the proposed `VirusTotal` module | Hash lookup by default, content upload off, private sources excluded (privacy) |
| ClamAV definition freshness as a tracked fact (deep dive 27 section 5.1 and 9) | Fully | A stale provider counts as unavailable; a Kodo probe reports definition age |
| Remediation by stripping a member (`zipfile.remove()`/`repack()`, deep dive 27 section 3.1) | Concept | No Python-version dependency; builds a derived blob in the BCL; default is reject |
| Outbound archive symmetry (deep dive 31 section 10) | Concept | `screening.outbound.enabled` for exports and outbound mail |
| Verdict returns pass/fail plus detail, owner of downstream workflow is elsewhere (deep dive 27 section 1) | Boundary | In Bazaar the module links quarantine and owner release itself (6.8), because Bazaar has no separate Review/Flagging module |
| Staff review for disagreements | Concept | Owner-only, owner-signed release, no release of confirmed malicious |
| gRPC `ScanFile` and `ScanContainer`, protobuf (`content_security.proto`) | Not taken | Wire is JSON (spike S6); in-process interfaces; out-of-process parsers use the protocol of BAZAAR-COMPONENTS section 7 |
| `FrozenDict` and Python forward-compat patterns | Not taken | Python-specific. .NET equivalents are immutable records and the newest-.NET rule in CLAUDE.md |

## 11. Integration points

Each surface has a policy row. "Hold" means `Review` with quarantine; "Deny" means refuse and quarantine the bytes for the owner to delete. Defaults are PROPOSED and may tighten only.

| Surface (`SurfaceId`) | Caller and seam | Default policy |
|---|---|---|
| `MailAttachment` (doc 22, inbound step 4) | The inbound pipeline, after MIME parse and before the reader session. Replaces the "executable or macro attachment" deterministic screen with the shared class table; the doc 22 screens that are not file checks (auth failure, lookalike domain, link text) stay in mail | Executables, scripts, shortcuts, disk images, macro files, nested archives: Hold. Claim mismatch: Hold. Polyglot: Deny. Other files: AV required; Pass goes to the blob store for owner viewing. `mail.max_attachments` and `mail.max_message_bytes` stay in doc 22 |
| `MailAttachment` outbound | Outbound builder in `Bazaar.Server.MailBackends` | Archive checks only (`screening.outbound.enabled`); Tier 2 approval for any attachment is unchanged (doc 22) |
| `GitHubArtifact` (doc 21) | The GitHub module before it hands a workflow artifact, release asset or PR attachment to a VM, the build host or an agent | Treated as `external`. Archives: preflight plus AV. Executables from third-party sources: Hold. Artifacts from the Bazaar company build (signed) are screened for bombs only (the signature and SBOM are doc 37's check, not this module's) |
| `PortabilityImport` (doc 44, stage 3 Detect) | The import job after staging and before parse; replaces the duplicated path and ratio rules in section 8.3 of doc 44 with calls here | AV and bomb checks required. `portability.import.max_expansion_ratio` stays and the stricter value wins. Macro-bearing docx: Hold, import without macros only on owner decision. A `Review` file does not block the whole job; its records are skipped and listed in the preview |
| `BrowserDownload` (doc 15 browser, doc 16) | Host-side per-agent proxy or the Chromium download hook when a download crosses from the browser profile into the VM share or the blob store | Always external. Executables and scripts: Deny to the agent, Hold for the owner. Mechanism OPEN (Q4) |
| `VmDownload` (doc 16) | A file moving guest-to-host (an artifact copied out, a mounted share write) and host-to-guest for external-origin files | Same as `BrowserDownload`. A guest-side hook through `Bazaar.Guest.Agent` is an option, never the only check, because the guest is untrusted |
| `SoulUpload` (doc 07 import) | Soul bundle importer before digest and signature checks | Zip preflight and AV; bundles with executables or `scripts/` Deny (the importer already never runs `scripts/`); a valid signature proves origin and integrity, not safety, so screening still runs |
| `ModuleUpload` (docs 41b, 42, 48 install gate) | Module manager at install, after signature and before any load | Signed first-party packages: bomb checks and AV run; the signature gate is separate. Third-party packages: full policy, Hold on unsigned. Screening never substitutes for signing, and signing never skips screening |
| `MemberUpload` (doc 02 blob surface) | Blob upload completion for human members' attachments in GCs | Owner and member-human tag untainted per doc 15, so policy is lighter: AV preferred, bomb checks required; the owner may set `required` |
| `CommunityAttachment` (doc 36) | A reader session's download of a stranger's attachment | Treated like mail attachments; reader sessions never receive bytes of a held file |
| `OutboundExport` (docs 33, 44) | The export builder before delivery | Bomb and structure checks only |
| Custom (`Other`) | Operator-registered surface ids from other modules via `IScreeningSurfaceRegistry` | Strictest default row until the owner sets one |

## 12. Kodo health contract

The module implements the module health contract (doc 46 section 10; the code seam today is `IModuleHealthContract` in `Bazaar.Server.Modules.Abstractions`, and `ModuleHealthContractTests` base class from `Bazaar.Kodo.Testing` applies).

- **Liveness** (no I/O): pipeline and rescan loops not stuck.
- **Readiness**: at least one provider available with definitions inside the age limit when `screening.av.required` is true on any surface; verdict store writable; scratch directory writable with space. Not ready means every external-origin read is refused, which is the intended fail-closed behaviour.
- **Dependencies**: `audit` and `blobs` Hard; `approvals`, `kodo`, `web-admin` and each installed provider and inspector module Soft (a provider is Soft for the module, but its absence can still turn readiness red through the rule above).
- **Self-test** (`Quick`: sniff, polyglot and bomb fixtures; `Full`: sandbox scope, each provider detects the standard antivirus test string, built at run time from fragments so it never sits whole in the repository; a benign file passes; a crafted polyglot is Review or Deny; a crafted archive bomb is Deny; a simulated provider timeout is Deny). Leaves no residue (blobs and verdict rows created in the sandbox scope are removed). A mutation probe flips the policy to always-Pass and requires the self-test to fail.
- **Probes added to the Kodo catalogue** (PROPOSED; catalogue changes are an update, doc 46 section 9.1): `screening.providers.available` (critical when AV is required), `screening.provider.<id>.definitions_age` (non-critical, red past the limit), `screening.provider.<id>.latency`, `screening.queue.depth`, `screening.rescan.backlog`, `screening.verdicts.denied_rate` (anomaly, advisory), `screening.selftest`.
- Metrics to Observatory: scans, passes, reviews, denies by reason code, bombs rejected, polyglots found, rescans, flips, p95 scan time per provider.

## 13. Interaction with existing modules

| Module | Reused or extended | How |
|---|---|---|
| `Bazaar.Server.Blobs` / `.Abstractions` | Reused, unchanged | Reads via `IBlobStore.OpenAsync`; decorates `IBlobReadAccess`; verdict keyed by blob SHA-256. No Blobs edit. A pre-commit hook inside the upload path is not needed: screening runs at the consumer's seam (completion callback) |
| `Bazaar.Server.MailBackends` | Reused | Back ends deliver raw MIME; doc 22's inbound pipeline calls `IContentScreener` per attachment. MailBackends gains no screening code |
| `Bazaar.Server.Portability` | Reused | Detect stage and parser-isolation limits call this module; doc 44's path and ratio rules become aliases |
| `Bazaar.Audit.Abstractions` | Reused | `IAuditLog.AppendAsync(new AuditEntry(ts, actor, kind, payload))` with `Actor` as the canonical `Principal` text |
| `Bazaar.Approvals.Abstractions` | Reused | Owner-signed release payload and nonce store; no new signature code |
| `Bazaar.Server/Security/Quarantine`, `Security/Taint` | Extended, not duplicated | Linkage through `IScreeningQuarantineSink`; taint read-only |
| `Bazaar.Server.AgentSafety` | Reused | Kill-switch and containment stay there; this module only reports |
| `Bazaar.Server.Modules` (install gate, doc 48) | Reused | Install calls the screener on packages; the gate stays in Modules |
| Kodo (doc 46) | Extended | Probes and health contract (section 12) |
| Doc 22 mail screens, doc 44 section 8.3 | Reused, then slimmed | Their file-level checks move here; they keep their own non-file checks and their own size and count settings |
| Souls importer (`Bazaar.Server/Souls/Import/BundleImporter.cs`) | Called by | Calls the screener first; no screening code is added there |

Nothing in this document creates a second archive-path checker, MIME sniffer or quarantine table; those that exist elsewhere are the callers' and become calls.

## 14. Security

- **Parsing hostile bytes** is the module's main risk. Sniffing reads bounded header windows. Archive inspection reads central directories, with hard limits on every count and length before allocating. Risky body parsers run out-of-process with no network (doc 44 section 8.3).
- **Time-of-check to time-of-use.** Screening is of an immutable, content-addressed blob; the verdict binds to the hash. A consumer must read by hash through `ScreenedBlobAccess`, never from a path the sender can change.
- **Scanner as target.** The scratch directory is private to the service account, never the agent mount or a guest share; files are deleted after scan; engines run as their own unprivileged accounts.
- **Denial of service.** Per-provider and per-surface concurrency limits, queue depth caps, per-requester rates (`screening.rate.per_requester_per_hour`, PROPOSED, default 120), and the size and ratio caps. Overload returns `Deny` (`BUSY`) to the requester, not `Pass`.
- **Poisoning the verdict cache.** The cache key includes the policy version and engine set hash; a policy or provider change invalidates cached `Pass` rows by requiring re-screening before reuse.
- **A provider module lying.** A provider is trusted to report honestly (module trust under doc 42; signed module with declared permissions). A provider cannot lower another provider's result. A provider returning `Clean` for the self-test's antivirus test string turns the self-test red and the provider unavailable.
- **Screening is not signing.** Neither replaces the other.
- **Injection.** No file text, filename or detection name goes to a model through this module; owner-facing strings are escaped and bounded.

## 15. Testing

xUnit v3 everywhere; real PostgreSQL for the verdict store (doc 34); `Avalonia.Headless` for the Held files pane.

- **Fail closed:** provider throws, times out, is missing, is stale; every one is `Deny`. A fake that returns after the deadline does not flip the verdict. The module disabled with a hard consumer fails start; with a soft consumer, external files are refused.
- **Single origin of Pass:** source scan for a second construction site; reflection test that `ScreenDecision` has three members and no abstractions member touches `SourceTrust` or `TaintState`.
- **Policy matrix:** table-driven test over every combination of two or three providers and outcomes, for both policies; floors (no `Pass` on any `Error`, none with no provider).
- **Sniffing:** corpus of real-type files with wrong extensions and wrong MIME; mismatch recorded.
- **Polyglot:** GIF plus zip, PDF with prepended zip, PE after PNG, OLE in OOXML, SVG with script; and the negative corpus (20 real scanned PDFs with embedded JPEG, OOXML with embedded PNG) must not be flagged.
- **Archive:** zip bomb (high ratio), many entries, declared-size lie, overlapping entries, zip-slip, symlink, absolute path, case-colliding names, encrypted member, nested archive at depths 0 to 5, tar bomb through gzip, unknown format with no inspector, cumulative budget across nested archives. Outbound symmetry test.
- **Two-pass:** a zip with one malicious member among clean ones fails on the member pass although the container passes its own check (Resibo's testing hook).
- **Remediation:** a stripped archive is a derived blob with its own verdict; the original is unchanged and stays quarantined.
- **Taint:** a `Pass` file read in a reader session hard-taints it; release from quarantine does not change `source_trust`; an agent session cannot read a held blob (blob read decorator).
- **Release:** a reader, an actor, a provider or an unsigned request cannot release; a malicious-confirmed verdict has no release path.
- **Re-screening:** a definition change flips a retained `Pass` and raises the event; sessions that read the blob are listed.
- **Integration (consumer-level, one per surface):** mail attachment hold; GitHub artifact; import job skips a held record and continues; VM and browser download denial; Soul bundle with `scripts/`; module package with a bad archive.
- **Kodo contract tests** (`ModuleHealthContractTests`), including the self-test mutation probe.
- **Architecture tests:** the module references only the permitted projects; no third-party package in the main module or abstractions; provider modules reference only the abstractions.

## 16. Work packages

Small and independent, each its own file or project with tests. Order shows dependencies.

| WP | Content | Needs |
|---|---|---|
| 1 | `Bazaar.Server.ContentScreening.Abstractions`: records, enums, interfaces; reflection tests (three decisions, no trust members) | none |
| 2 | File type sniffer and class table, with fixture corpus | 1 |
| 3 | Polyglot detector with the false-positive negatives | 1, 2 |
| 4 | Zip and tar and gzip inspectors; declared-size preflight and structural hazards | 1 |
| 5 | Bounded counting streams and actual-size enforcement; cumulative budget | 4 |
| 6 | Verdict policy resolver (consensus and stricter), engine families, freshness; policy matrix tests | 1 |
| 7 | Pipeline orchestrator, spooling, cache lookup, never-throw guard | 2, 3, 5, 6 |
| 8 | Migrations `0001_verdicts.sql`, verdict store, audit writer with `Principal` | 1 |
| 9 | Settings registry rows, floors, surface policy rows | 1 (and doc 03 registry) |
| 10 | Quarantine sink adapter, blob read decorator, `ScreenedBlobAccess` | 7, 8 |
| 11 | Owner release service over the approvals module; Held files model | 8, 10 |
| 12 | Rescan service and queue | 7, 8 |
| 13 | Kodo health contract, probes, self-test with the mutation probe | 7 |
| 14 | `Bazaar.Server.ContentScreening.ClamAv` provider module (clamd `INSTREAM`; fixture daemon fake) | 1 |
| 15 | `Bazaar.Server.ContentScreening.VirusTotal` provider module (hash lookup; upload off) | 1 |
| 16 | `Bazaar.Server.ContentScreening.WindowsDefender` provider module | 1 |
| 17 | `Bazaar.Server.ContentScreening.Archives.SevenZip` inspector module, out-of-process | 4, 5 |
| 18 | `Bazaar.Client.ContentScreening` Held files pane (headless tests) | 11 |
| 19 | Remediation (derived blob) | 7 |
| 20 | Consumer integrations, one package each: mail (doc 22), GitHub (21), portability (44), downloads (15, 16), Souls (07), modules (41b, 42, 48), outbound exports (33) | 7, 10 |
| 21 | Web admin read port for doc 51 | 8 |

## 17. Open questions

1. **ClamAV as a dependency module.** The default denies everything external when no provider is installed. Doc 42's zero-external-installs rule suggests packaging a ClamAV daemon as a `Deps` module, which is third-party software with its own licence (GPL) and size. Owner decision: bundle, offer in the wizard, or require the operator to point at a daemon.
2. **Default verdict policy.** Consensus (Resibo's) or any-malicious-denies. Recommended: consensus, with the stricter one a setting.
3. **Which provider modules ship in v1.** Proposed: ClamAv and VirusTotal (hash only). Owner picks; Windows Defender, YARA and a hash-list provider are optional.
4. **Browser and VM download hook.** Inline in the per-agent proxy, in the Chromium download handler, at the guest boundary, or all three. Needs a spike with doc 16 and doc 15.
5. **Who implements the quarantine seam.** Doc 15 work item 9 plans a quarantine service; today it is code inside `Bazaar.Server/Security/Quarantine`. Should the sink adapter wait for a quarantine module?
6. **Cloud content upload.** Whether to offer it at all, and the counsel review of sending files that may hold personal data to a third party.
7. **Archive limit defaults.** The numbers are Bazaar's proposal, not Resibo's. Needs benchmark data from Bazaar imports and attachments before they are called final.
8. **Doc 46 naming.** Doc 46 names the interface `IModuleHealth`; the code has `IModuleHealthContract`. This doc follows the code; the owner or doc 46's owner should confirm.
9. **Surface registry.** Whether other modules may register their own surface ids (`IScreeningSurfaceRegistry`) in v1, or only the fixed list.
10. **Erasure and held bytes.** Same open point as doc 22 Q3: counsel decides whether quarantined bytes are exempt from erasure.
