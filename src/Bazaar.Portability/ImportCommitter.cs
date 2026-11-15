namespace Bazaar.Portability;

/// <summary>Where a committed record lands (doc 44 section 8.2).</summary>
public enum ImportTargetKind
{
    GcMessage,
    VaultFile,
}

/// <summary>Provenance attached to every object a commit creates (doc 44 section 8.1, stage 9).</summary>
public sealed record ImportProvenance(string JobId, string SourceKey);

/// <summary>One record to write, with the provenance the written object must carry.</summary>
public sealed record ImportItem(SourceRecord Record, ImportProvenance Provenance);

/// <summary>
/// Write side of the commit path. Each call writes one batch and succeeds or fails as a whole: an implementation
/// throws when the batch cannot be written and records nothing for that call.
/// </summary>
public interface IImportTargetWriter
{
    /// <summary>Source keys this job already wrote (GC and Vault). A re-run skips them.</summary>
    Task<IReadOnlySet<string>> ReadCommittedSourceKeysAsync(string jobId, CancellationToken ct);

    /// <summary>Appends one batch of GC entries. Throws if the batch cannot be written.</summary>
    Task AppendGcMessagesAsync(string jobId, int batchIndex, IReadOnlyList<ImportItem> items, CancellationToken ct);

    /// <summary>Writes one batch of Vault Markdown files. Throws if the batch cannot be written.</summary>
    Task WriteVaultFilesAsync(string jobId, int batchIndex, IReadOnlyList<ImportItem> items, CancellationToken ct);

    /// <summary>Source keys currently readable in the GC for this job.</summary>
    Task<IReadOnlyList<string>> ReadGcSourceKeysAsync(string jobId, CancellationToken ct);

    /// <summary>Source keys currently readable in the Vault for this job.</summary>
    Task<IReadOnlyList<string>> ReadVaultSourceKeysAsync(string jobId, CancellationToken ct);
}

/// <summary>Which step of a batch failed.</summary>
public enum ImportBatchStage
{
    /// <summary>The target write failed. Nothing was written for the batch.</summary>
    Write,

    /// <summary>The batch was written but its audit entry was not. The job stops and the batch needs review.</summary>
    Audit,
}

/// <summary>The first failure: batch index (0-based), target, stage and the error message.</summary>
public sealed record ImportBatchFailure(int BatchIndex, ImportTargetKind Target, ImportBatchStage Stage, string Message);

/// <summary>
/// Outcome of a commit. <see cref="BatchCount"/> is the number of batches whose write succeeded.
/// <see cref="CommittedSourceKeys"/> lists the records written by this run. Refused means nothing was written.
/// </summary>
public sealed record ImportCommitResult(
    bool Refused,
    string? RefusalReason,
    int BatchCount,
    IReadOnlyList<string> CommittedSourceKeys,
    int SkippedCount,
    ImportBatchFailure? FirstFailure)
{
    public bool Succeeded => !Refused && FirstFailure is null;
}

/// <summary>
/// Commit path for a ready import dry-run (doc 44 section 8.1, stages 8 and 9). It chooses batches and targets,
/// delegates writes to <see cref="IImportTargetWriter"/>, and audits each written batch through <see cref="IAuditSink"/>.
/// </summary>
public static class ImportCommitter
{
    /// <summary>Records with this kind become GC entries. Every other kind becomes a Vault file.</summary>
    public const string GcEntryKind = "chat-entry";

    /// <summary>
    /// Commits the new records of a dry-run in batches of at most <paramref name="batchSize"/> records. Batches never mix
    /// targets. The first failing batch stops the job and is reported; it is not retried. A write that succeeds but whose
    /// audit entry fails also stops the job. Records the writer already holds for this job are skipped.
    /// </summary>
    public static async Task<ImportCommitResult> CommitAsync(
        ImportPipelineResult dryRun,
        string currentMappingHash,
        IImportTargetWriter writer,
        IAuditSink audit,
        string jobId,
        int batchSize,
        CancellationToken ct,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(dryRun);
        ArgumentNullException.ThrowIfNull(currentMappingHash);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        if (!ImportPipeline.CanCommit(dryRun, currentMappingHash))
        {
            return Refused("dry-run is not ready or its mapping hash does not match the current mapping");
        }

        var alreadyWritten = await writer.ReadCommittedSourceKeysAsync(jobId, ct);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new List<ImportItem>();
        var skipped = 0;
        foreach (var record in dryRun.Preview!.New)
        {
            if (alreadyWritten.Contains(record.SourceKey) || !seen.Add(record.SourceKey))
            {
                skipped++;
                continue;
            }
            pending.Add(new ImportItem(record, new ImportProvenance(jobId, record.SourceKey)));
        }

        var now = clock ?? TimeProvider.System;
        var batches = Batch(pending, batchSize);
        var committedKeys = new List<string>();
        var written = 0;
        for (var index = 0; index < batches.Count; index++)
        {
            var (target, items) = batches[index];
            try
            {
                if (target == ImportTargetKind.GcMessage)
                {
                    await writer.AppendGcMessagesAsync(jobId, index, items, ct);
                }
                else
                {
                    await writer.WriteVaultFilesAsync(jobId, index, items, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failure = new ImportBatchFailure(index, target, ImportBatchStage.Write, ex.Message);
                return new ImportCommitResult(false, null, written, committedKeys, skipped, failure);
            }

            written++;
            committedKeys.AddRange(items.Select(item => item.Record.SourceKey));

            var entry = new ImportAuditRecord(
                ImportAuditRecord.BatchKind,
                jobId,
                index + 1,
                items.Count,
                ImportBatchHash.Of(items),
                now.GetUtcNow());
            try
            {
                await audit.AppendAsync(entry, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failure = new ImportBatchFailure(index, target, ImportBatchStage.Audit, ex.Message);
                return new ImportCommitResult(false, null, written, committedKeys, skipped, failure);
            }
        }

        return new ImportCommitResult(false, null, written, committedKeys, skipped, null);
    }

    /// <summary>The target a record is written to.</summary>
    public static ImportTargetKind TargetFor(SourceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return string.Equals(record.Kind, GcEntryKind, StringComparison.Ordinal)
            ? ImportTargetKind.GcMessage
            : ImportTargetKind.VaultFile;
    }

    private static ImportCommitResult Refused(string reason) =>
        new(true, reason, 0, [], 0, null);

    /// <summary>Splits items, in order, into batches of at most batchSize that share one target.</summary>
    private static List<(ImportTargetKind Target, List<ImportItem> Items)> Batch(List<ImportItem> items, int batchSize)
    {
        var batches = new List<(ImportTargetKind Target, List<ImportItem> Items)>();
        foreach (var item in items)
        {
            var target = TargetFor(item.Record);
            if (batches.Count == 0 || batches[^1].Target != target || batches[^1].Items.Count >= batchSize)
            {
                batches.Add((target, new List<ImportItem>()));
            }
            batches[^1].Items.Add(item);
        }
        return batches;
    }
}
