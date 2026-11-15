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
/// Write side of the commit path. Each call writes one batch and succeeds or fails as a whole:
/// an implementation throws when the batch cannot be written and records nothing for that call.
/// The real implementations sit behind GC (doc 04) and VaultWriter (doc 18); this module names no storage.
/// </summary>
public interface IImportTargetWriter
{
    /// <summary>Source keys already written by an earlier commit. A re-run skips them.</summary>
    IReadOnlySet<string> CommittedSourceKeys { get; }

    /// <summary>Appends one batch of GC entries. Throws if the batch cannot be written.</summary>
    void AppendGcMessages(string jobId, int batchIndex, IReadOnlyList<ImportItem> items);

    /// <summary>Writes one batch of Vault Markdown files. Throws if the batch cannot be written.</summary>
    void WriteVaultFiles(string jobId, int batchIndex, IReadOnlyList<ImportItem> items);
}

/// <summary>The first batch that failed: its index, the target it was writing to, and the writer's message.</summary>
public sealed record ImportBatchFailure(int BatchIndex, ImportTargetKind Target, string Message);

/// <summary>
/// Outcome of a commit. <see cref="BatchCount"/> is the number of batches written successfully.
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
/// Commit path for a ready import dry-run (doc 44 section 8.1, stages 8 and 9). Pure orchestration:
/// it chooses batches and targets, and delegates every write to <see cref="IImportTargetWriter"/>.
/// </summary>
public static class ImportCommitter
{
    /// <summary>Records with this kind become GC entries. Every other kind becomes a Vault file.</summary>
    public const string GcEntryKind = "chat-entry";

    /// <summary>
    /// Commits the new records of a dry-run in batches of at most <paramref name="batchSize"/> records.
    /// Batches never mix targets. The first failing batch stops the job and is reported; it is not retried.
    /// Records whose source key the writer already holds are skipped, so a re-run after success writes nothing.
    /// </summary>
    public static ImportCommitResult Commit(
        ImportPipelineResult dryRun,
        string currentMappingHash,
        IImportTargetWriter writer,
        string jobId,
        int batchSize)
    {
        ArgumentNullException.ThrowIfNull(dryRun);
        ArgumentNullException.ThrowIfNull(currentMappingHash);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        if (!ImportPipeline.CanCommit(dryRun, currentMappingHash))
        {
            return Refused("dry-run is not ready or its mapping hash does not match the current mapping");
        }

        var alreadyWritten = writer.CommittedSourceKeys;
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

        var batches = Batch(pending, batchSize);
        var committedKeys = new List<string>();
        for (var index = 0; index < batches.Count; index++)
        {
            var (target, items) = batches[index];
            try
            {
                if (target == ImportTargetKind.GcMessage)
                {
                    writer.AppendGcMessages(jobId, index, items);
                }
                else
                {
                    writer.WriteVaultFiles(jobId, index, items);
                }
            }
            catch (Exception ex)
            {
                var failure = new ImportBatchFailure(index, target, ex.Message);
                return new ImportCommitResult(false, null, index, committedKeys, skipped, failure);
            }

            committedKeys.AddRange(items.Select(item => item.Record.SourceKey));
        }

        return new ImportCommitResult(false, null, batches.Count, committedKeys, skipped, null);
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
