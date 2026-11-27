using System.Security.Cryptography;
using System.Text.Json;
using Bazaar.Contracts.Telemetry;
using Bazaar.Diagnostics.Fingerprinting;
using Bazaar.Diagnostics.Grouping;
using Bazaar.Diagnostics.Ingestion;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Diagnostics;

/// <summary>The outcome of one submission, as the wire layer needs it to answer <c>crash.ack</c> or a refusal.</summary>
public abstract record CrashIntakeResult;

/// <summary>The report was stored. <see cref="GroupKey"/> is the fingerprint for <c>crash.ack</c>.</summary>
/// <param name="ReportId">The report's id.</param>
/// <param name="GroupKey">The server fingerprint of the group it joined.</param>
/// <param name="Duplicate">True when this id was already stored; nothing was written again.</param>
public sealed record CrashIntakeAccepted(Guid ReportId, string GroupKey, bool Duplicate) : CrashIntakeResult;

/// <summary>The report was refused. <see cref="Code"/> is the code a client shows (doc 43 section 12.1).</summary>
/// <param name="Step">The pipeline step that refused it.</param>
/// <param name="Code">The refusal code.</param>
public sealed record CrashIntakeRefused(IngestionStep Step, IngestionRefusalCode Code) : CrashIntakeResult;

/// <summary>The outcome of <see cref="CrashIntakeService.AttachLateAsync"/>: attached, or dropped with the code that says why.</summary>
/// <param name="Attached">True when the screenshot is stored with its report.</param>
/// <param name="Code">The refusal code when it was dropped, or null when attached.</param>
public sealed record CrashLateAttachmentResult(bool Attached, IngestionRefusalCode? Code);

/// <summary>Intake could not commit because the group kept changing. The caller retries the submission; nothing was stored.</summary>
public sealed class CrashIntakeContentionException : Exception
{
    /// <summary>Creates the exception.</summary>
    public CrashIntakeContentionException(string fingerprint)
        : base($"Group {fingerprint} changed too often to commit.")
    {
    }
}

/// <summary>
/// Steps 7 and 8 of doc 43 section 12.1: after the pure checks pass, store the report sealed, upsert its group, and decide the
/// GitHub effects. The effects are written to the outbox in the same commit as the state change, so a crash between the two can
/// neither lose a filing nor file twice. Audit entries follow the commit and carry ids, hashes and counts only.
/// </summary>
public sealed class CrashIntakeService
{
    private readonly CrashIngestionPipeline _pipeline;
    private readonly ICrashIntakeStore _store;
    private readonly ICrashPayloadSealer _sealer;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _clock;
    private readonly CrashFilingOptions _options;

    /// <summary>Creates the service over its seams.</summary>
    public CrashIntakeService(
        CrashIngestionPipeline pipeline,
        ICrashIntakeStore store,
        ICrashPayloadSealer sealer,
        IAuditLog audit,
        TimeProvider clock,
        CrashFilingOptions options)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sealer);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        _pipeline = pipeline;
        _store = store;
        _sealer = sealer;
        _audit = audit;
        _clock = clock;
        _options = options;
    }

    /// <summary>Runs one submission through checks, storage, grouping and the filing decision.</summary>
    public async Task<CrashIntakeResult> ProcessAsync(IngestionSubmission submission, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var report = submission.Report;
        var now = _clock.GetUtcNow();

        var outcome = _pipeline.Process(submission);
        if (outcome is IngestionRefused refused)
        {
            await _audit.AppendAsync(CrashAudit.Refused(now, report.ReportId, refused.Step.ToWireName(), refused.Code.ToWireName()), ct).ConfigureAwait(false);
            return new CrashIntakeRefused(refused.Step, refused.Code);
        }

        var accepted = (IngestionAccepted)outcome;
        var body = JsonSerializer.SerializeToUtf8Bytes(report, CrashContractsJsonContext.Default.CrashReport);
        var storedReport = new StoredCrashReport(
            report.ReportId,
            report.InstallId,
            accepted.Fingerprint,
            report.CreatedAt,
            now,
            report.Client.Version,
            accepted.SymbolsState,
            Convert.ToHexStringLower(SHA256.HashData(body)),
            await _sealer.SealAsync(report.InstallId, SealedPayloadKind.Report, report.ReportId, body, ct).ConfigureAwait(false));

        StoredCrashAttachment? attachment = null;
        if (accepted.Attachment is { Kept: true } && submission.Attachment is { } upload)
        {
            attachment = new StoredCrashAttachment(
                upload.Begin.AttachmentId,
                report.ReportId,
                upload.Begin.ContentType,
                upload.End.Sha256,
                await _sealer.SealAsync(report.InstallId, SealedPayloadKind.Attachment, upload.Begin.AttachmentId, upload.Data, ct).ConfigureAwait(false));
        }
        else if (accepted.Attachment is { Kept: false, DropReason: { } dropReason })
        {
            await _audit.AppendAsync(CrashAudit.AttachmentDropped(now, report.ReportId, dropReason.ToWireName()), ct).ConfigureAwait(false);
        }

        var occurrence = CrashReportMapping.ToOccurrence(report);
        var facts = FactsOf(report, accepted.SymbolsState);

        for (var attempt = 0; attempt < _options.MaxCommitAttempts; attempt++)
        {
            var current = await _store.GetGroupAsync(accepted.Fingerprint, ct).ConfigureAwait(false);
            var filedToday = await _store.CountIssueFilingsAsync(DateOnly.FromDateTime(now.UtcDateTime), ct).ConfigureAwait(false);
            var policy = new FilingPolicy(_options.FilingEnabled ? _options.IssuesPerDay : 0, filedToday);
            var update = GroupTransition.Apply(current?.State, occurrence, policy, now);
            var filingInFlight = current is not null && await _store.HasPendingFileIssueAsync(accepted.Fingerprint, ct).ConfigureAwait(false);

            var outbox = new List<PendingCrashAction>();
            var mismatches = new List<RecordMismatch>();
            foreach (var action in update.Actions)
            {
                switch (action)
                {
                    case RecordMismatch mismatch:
                        mismatches.Add(mismatch);
                        break;
                    case FileIssue when filingInFlight:
                        // A filing is already in flight for this group. A second one would file twice and spend the budget twice.
                        break;
                    case QueueDeferred:
                        // A deferred group waits in its state. The release pass files it when the budget returns; no outbox row is needed.
                        break;
                    default:
                        outbox.Add(new PendingCrashAction(Guid.NewGuid(), accepted.Fingerprint, action, now, 0, now));
                        break;
                }
            }

            var commit = new CrashCommit(
                storedReport,
                attachment,
                new StoredCrashGroup(update.State, current?.Facts ?? facts, (current?.Version ?? 0) + 1),
                current?.Version,
                outbox);

            switch (await _store.CommitAsync(commit, ct).ConfigureAwait(false))
            {
                case CrashCommitStatus.DuplicateReport:
                    await _audit.AppendAsync(CrashAudit.Duplicate(now, report.ReportId), ct).ConfigureAwait(false);
                    return new CrashIntakeAccepted(report.ReportId, accepted.Fingerprint, Duplicate: true);

                case CrashCommitStatus.VersionConflict:
                    continue;

                default:
                    await AuditCommittedAsync(now, report.ReportId, storedReport, accepted, update, current, mismatches, ct).ConfigureAwait(false);
                    return new CrashIntakeAccepted(report.ReportId, accepted.Fingerprint, Duplicate: false);
            }
        }

        throw new CrashIntakeContentionException(accepted.Fingerprint);
    }

    /// <summary>
    /// Attaches a screenshot that finished after its report was stored (doc 43 section 5.2). It runs the same step-4 checks as an
    /// attachment that came with the report, seals the bytes for the same install, and stores them only if the report still has none.
    /// </summary>
    /// <param name="installId">The install the report belongs to.</param>
    /// <param name="reportId">The stored report the screenshot names.</param>
    /// <param name="upload">The assembled screenshot.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>Attached, or the refusal code the screenshot was dropped with.</returns>
    public async Task<CrashLateAttachmentResult> AttachLateAsync(Guid installId, Guid reportId, IngestionAttachment upload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        var now = _clock.GetUtcNow();
        var attachmentId = upload.Begin.AttachmentId;

        var decision = CrashIngestionPipeline.CheckAttachment(reportId, attachmentId, upload);
        if (!decision.Kept)
        {
            var code = decision.DropReason!.Value;
            await _audit.AppendAsync(CrashAudit.AttachmentDropped(now, reportId, code.ToWireName()), ct).ConfigureAwait(false);
            return new CrashLateAttachmentResult(false, code);
        }

        var stored = new StoredCrashAttachment(
            attachmentId,
            reportId,
            upload.Begin.ContentType,
            upload.End.Sha256,
            await _sealer.SealAsync(installId, SealedPayloadKind.Attachment, attachmentId, upload.Data, ct).ConfigureAwait(false));

        if (!await _store.AttachLateAsync(stored, ct).ConfigureAwait(false))
        {
            await _audit.AppendAsync(CrashAudit.AttachmentDropped(now, reportId, IngestionRefusalCode.AttachmentNotReferenced.ToWireName()), ct).ConfigureAwait(false);
            return new CrashLateAttachmentResult(false, IngestionRefusalCode.AttachmentNotReferenced);
        }

        await _audit.AppendAsync(CrashAudit.AttachmentAttached(now, reportId, attachmentId), ct).ConfigureAwait(false);
        return new CrashLateAttachmentResult(true, null);
    }

    private async Task AuditCommittedAsync(
        DateTimeOffset now,
        Guid reportId,
        StoredCrashReport stored,
        IngestionAccepted accepted,
        GroupUpdate update,
        StoredCrashGroup? before,
        IReadOnlyList<RecordMismatch> mismatches,
        CancellationToken ct)
    {
        var attachment = accepted.Attachment is null ? "none" : accepted.Attachment.Kept ? "kept" : "dropped";
        await _audit.AppendAsync(CrashAudit.Accepted(now, reportId, stored.Fingerprint, stored.BodySha256, accepted.SymbolsState == SymbolsState.Resolved ? "resolved" : "missing", attachment), ct).ConfigureAwait(false);

        foreach (var mismatch in mismatches)
        {
            await _audit.AppendAsync(CrashAudit.FingerprintMismatch(now, reportId, mismatch.DeviceFingerprint, mismatch.ServerFingerprint), ct).ConfigureAwait(false);
        }

        if (before is null || before.State.Status != update.State.Status)
        {
            await _audit.AppendAsync(CrashAudit.GroupStatus(now, stored.Fingerprint, update.State.Status.ToString(), update.State.OccurrenceCount), ct).ConfigureAwait(false);
        }
    }

    private static CrashIssueFacts FactsOf(CrashReport report, SymbolsState symbols)
    {
        var input = CrashReportMapping.ToFingerprintInput(report);
        var top = input.Frames.Take(IssueBody.MaxFrames).ToArray();
        int? line = null;
        if (symbols == SymbolsState.Resolved && report.FlaggedFrame is { } flagged && flagged < report.Stack.Count)
        {
            line = report.Stack[flagged].Line;
        }

        return new CrashIssueFacts(report.Error.Code, report.Error.Type, top, line, report.Error.Message);
    }
}
