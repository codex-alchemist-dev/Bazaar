using System.Globalization;

namespace Bazaar.Server.Portability;

/// <summary>
/// Lifecycle of an import job (doc 44 sections 8.1, 8.7 and 8.9). The store keeps the snake_case name of each state in
/// <c>import_job.state</c>. <c>Verifying</c> is the step between the last batch and the verdict (section 8.1, stage 10).
/// </summary>
public enum ImportJobState
{
    /// <summary>Uploaded and recorded. No dry run is recorded yet.</summary>
    Staged,

    /// <summary>A dry run is recorded. Commit is allowed only against it (section 8.1, stage 7).</summary>
    DryRun,

    /// <summary>Batches are being committed.</summary>
    Committing,

    /// <summary>Stopped between batches. A resume skips the committed batches (section 8.7).</summary>
    Paused,

    /// <summary>No batch progress for a set time (section 8.7). Health alerts follow (section 11).</summary>
    Stalled,

    /// <summary>Every batch is committed and the verifier is re-reading what was written.</summary>
    Verifying,

    /// <summary>Verified. Terminal.</summary>
    Verified,

    /// <summary>The verifier found a mismatch. Terminal, with an alert (section 8.1, stage 10).</summary>
    FailedVerify,

    /// <summary>A batch or a stage failed. Terminal.</summary>
    Failed,

    /// <summary>Stopped by the owner. Committed batches stay in place (section 8.7). Terminal.</summary>
    Cancelled,
}

/// <summary>The state rules of an import job. A store uses them to refuse a transition the lifecycle does not allow.</summary>
public static class ImportJobStates
{
    /// <summary>True for the states a job never leaves.</summary>
    public static bool IsTerminal(ImportJobState state) =>
        state is ImportJobState.Verified or ImportJobState.FailedVerify or ImportJobState.Failed or ImportJobState.Cancelled;

    /// <summary>True when a job in <paramref name="from"/> may move to <paramref name="to"/>.</summary>
    public static bool CanTransition(ImportJobState from, ImportJobState to) => Array.IndexOf(NextOf(from), to) >= 0;

    /// <summary>Every state a job may be in for a move to <paramref name="to"/>, in enum order. A store uses it for a compare-and-set.</summary>
    public static IReadOnlyList<ImportJobState> SourcesFor(ImportJobState to) =>
        [.. Enum.GetValues<ImportJobState>().Where(from => CanTransition(from, to))];

    private static ImportJobState[] NextOf(ImportJobState from) => from switch
    {
        ImportJobState.Staged => [ImportJobState.DryRun, ImportJobState.Failed, ImportJobState.Cancelled],
        ImportJobState.DryRun => [ImportJobState.Staged, ImportJobState.Committing, ImportJobState.Failed, ImportJobState.Cancelled],
        ImportJobState.Committing => [ImportJobState.Paused, ImportJobState.Stalled, ImportJobState.Verifying, ImportJobState.Failed, ImportJobState.Cancelled],
        ImportJobState.Paused => [ImportJobState.Committing, ImportJobState.Failed, ImportJobState.Cancelled],
        ImportJobState.Stalled => [ImportJobState.Committing, ImportJobState.Paused, ImportJobState.Failed, ImportJobState.Cancelled],
        ImportJobState.Verifying => [ImportJobState.Verified, ImportJobState.FailedVerify, ImportJobState.Failed],
        _ => [],
    };
}

/// <summary>One file in an upload's source manifest: name, size in bytes and SHA-256 in lowercase hex (doc 44 section 8.1, stage 1).</summary>
public sealed record ImportManifestFile(string Name, long SizeBytes, string Sha256);

/// <summary>What staging records when it creates a job (doc 44 section 8.1, stage 2). The job starts in <see cref="ImportJobState.Staged"/>.</summary>
public sealed record NewImportJob(
    string JobId,
    string ActorId,
    string ImporterId,
    string ImporterVersion,
    long MaxUploadBytes,
    IReadOnlyList<ImportManifestFile> SourceManifest,
    DateTimeOffset CreatedAt);

/// <summary>A stored job with its state and the marks the store keeps (doc 44 section 8.9).</summary>
public sealed record ImportJobRecord(
    string JobId,
    string ActorId,
    string ImporterId,
    string ImporterVersion,
    ImportJobState State,
    long MaxUploadBytes,
    IReadOnlyList<ImportManifestFile> SourceManifest,
    string? MappingHash,
    string? DryRunHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastProgressAt,
    DateTimeOffset? EndedAt,
    string? FailureReason);

/// <summary>
/// One committed batch (doc 44 section 8.9): its number (1-based), item counts by target, the first and last source key
/// (the checkpoint a resume reads) and the batch hash from <see cref="ImportBatchHash"/>.
/// </summary>
public sealed record ImportBatchRecord(
    string JobId,
    int BatchNumber,
    IReadOnlyDictionary<ImportTargetKind, int> ItemCounts,
    string FirstSourceKey,
    string LastSourceKey,
    string BatchHash,
    DateTimeOffset CommittedAt)
{
    /// <summary>Items in the batch, all targets together.</summary>
    public int ItemCount => ItemCounts.Values.Sum();
}

/// <summary>
/// One row of the source-key index (doc 44 section 8.4): the importer, the source key, the hash of the content written, the
/// target it became, and the job that wrote it. The target version is part of the key, so a changed source adds a row.
/// </summary>
public sealed record ImportItemRecord(
    string ImporterId,
    string SourceKey,
    string ContentHash,
    ImportTargetKind TargetKind,
    string TargetId,
    string TargetVersion,
    string JobId);

/// <summary>
/// Durable record of import jobs, batches and items (doc 44 sections 8.1, 8.4, 8.7 and 8.9). Each method is one step: a batch
/// and its items are recorded together or not at all. Records are never deleted.
/// </summary>
public interface IImportJobStore
{
    /// <summary>Creates a job in <see cref="ImportJobState.Staged"/>. Throws <see cref="ImportStoreConflictException"/> when the id exists.</summary>
    Task CreateJobAsync(NewImportJob job, CancellationToken ct);

    /// <summary>The job, or null when no job has that id.</summary>
    Task<ImportJobRecord?> GetJobAsync(string jobId, CancellationToken ct);

    /// <summary>Records the dry-run and mapping hashes (section 8.1, stage 7). Returns false when the job is missing or already past dry run.</summary>
    Task<bool> RecordDryRunAsync(string jobId, string mappingHash, string dryRunHash, CancellationToken ct);

    /// <summary>
    /// Moves a job to <paramref name="to"/> when the lifecycle allows it (see <see cref="ImportJobStates"/>). Entering
    /// <see cref="ImportJobState.Committing"/> sets the start time once; entering a terminal state sets the end time.
    /// Returns false when the job is missing or the move is not allowed from its current state.
    /// </summary>
    Task<bool> TransitionAsync(string jobId, ImportJobState target, DateTimeOffset at, string? reason, CancellationToken ct);

    /// <summary>
    /// Records one committed batch and its items in one transaction, and sets the job's last progress time to the batch time.
    /// The job must be <see cref="ImportJobState.Committing"/>. Throws <see cref="ImportStoreConflictException"/> when the batch
    /// number or an item's (importer, source key, target version) is already recorded; nothing is written in that case.
    /// </summary>
    Task RecordBatchAsync(ImportBatchRecord batch, IReadOnlyList<ImportItemRecord> items, CancellationToken ct);

    /// <summary>The job's batches in batch-number order.</summary>
    Task<IReadOnlyList<ImportBatchRecord>> ListBatchesAsync(string jobId, CancellationToken ct);

    /// <summary>Source keys this job already wrote. A resume skips them. Same shape as <see cref="IImportTargetWriter.ReadCommittedSourceKeysAsync"/>.</summary>
    Task<IReadOnlySet<string>> ReadCommittedSourceKeysAsync(string jobId, CancellationToken ct);

    /// <summary>The most recently recorded item for an importer's source key, or null. A re-import compares its content hash with this one.</summary>
    Task<ImportItemRecord?> GetLatestItemAsync(string importerId, string sourceKey, CancellationToken ct);
}

/// <summary>A write hit a uniqueness rule (a duplicate job, batch number or item version). Nothing from that write is kept.</summary>
public sealed class ImportStoreConflictException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Input checks shared by every <see cref="IImportJobStore"/>, so a fake and a database refuse the same values.</summary>
public static class ImportStoreRules
{
    /// <summary>Longest job id the store accepts.</summary>
    public const int MaxJobIdLength = 128;

    /// <summary>True for a 64-character lowercase hex SHA-256 digest.</summary>
    public static bool IsSha256Hex(string value) =>
        value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Refuses a job that the store cannot hold: empty or long ids, an empty manifest, a bad digest, or an upload over its limit.</summary>
    public static void ValidateNewJob(NewImportJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        ValidateJobId(job.JobId);
        RequireText(job.ActorId, nameof(job.ActorId));
        RequireText(job.ImporterId, nameof(job.ImporterId));
        RequireText(job.ImporterVersion, nameof(job.ImporterVersion));
        ArgumentNullException.ThrowIfNull(job.SourceManifest);
        ArgumentOutOfRangeException.ThrowIfLessThan(job.MaxUploadBytes, 1L, nameof(job.MaxUploadBytes));
        if (job.SourceManifest.Count == 0)
        {
            throw new ArgumentException("A job needs at least one manifest file.", nameof(job));
        }

        long total = 0;
        foreach (var file in job.SourceManifest)
        {
            RequireText(file.Name, "manifest file name");
            ArgumentOutOfRangeException.ThrowIfNegative(file.SizeBytes);
            if (!IsSha256Hex(file.Sha256))
            {
                throw new ArgumentException("Manifest file '" + file.Name + "' has no SHA-256 digest in lowercase hex.", nameof(job));
            }
            total += file.SizeBytes;
        }

        if (total > job.MaxUploadBytes)
        {
            throw new ArgumentException("The manifest is " + total.ToString(CultureInfo.InvariantCulture) + " bytes, over the upload limit.", nameof(job));
        }
    }

    /// <summary>Checks a job id against the store's rules.</summary>
    public static void ValidateJobId(string jobId)
    {
        RequireText(jobId, nameof(jobId));
        if (jobId.Length > MaxJobIdLength)
        {
            throw new ArgumentException("A job id is at most " + MaxJobIdLength.ToString(CultureInfo.InvariantCulture) + " characters.", nameof(jobId));
        }
    }

    /// <summary>
    /// Refuses a batch whose items do not match it: a different job, counts that differ from the items, a first or last key
    /// that is not the first or last item, a bad batch hash, a batch number below 1, or two items with the same identity.
    /// </summary>
    public static void ValidateBatch(ImportBatchRecord batch, IReadOnlyList<ImportItemRecord> items)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(items);
        ValidateJobId(batch.JobId);
        ArgumentOutOfRangeException.ThrowIfLessThan(batch.BatchNumber, 1, nameof(batch));
        ArgumentNullException.ThrowIfNull(batch.ItemCounts);
        if (batch.ItemCounts.Count == 0 || batch.ItemCounts.Values.Any(n => n < 1))
        {
            throw new ArgumentException("A batch counts at least one item for each target it holds.", nameof(batch));
        }
        if (items.Count == 0)
        {
            throw new ArgumentException("A batch holds at least one item.", nameof(items));
        }
        if (!IsSha256Hex(batch.BatchHash))
        {
            throw new ArgumentException("The batch hash must be a SHA-256 digest in lowercase hex.", nameof(batch));
        }
        RequireText(batch.FirstSourceKey, nameof(batch.FirstSourceKey));
        RequireText(batch.LastSourceKey, nameof(batch.LastSourceKey));
        if (batch.FirstSourceKey != items[0].SourceKey || batch.LastSourceKey != items[^1].SourceKey)
        {
            throw new ArgumentException("The first and last source keys must be the first and last items.", nameof(batch));
        }

        var counted = items.GroupBy(i => i.TargetKind).ToDictionary(g => g.Key, g => g.Count());
        if (counted.Count != batch.ItemCounts.Count || batch.ItemCounts.Any(kv => !counted.TryGetValue(kv.Key, out var n) || n != kv.Value))
        {
            throw new ArgumentException("The item counts do not match the items.", nameof(batch));
        }

        var identities = new HashSet<(string, string, string)>();
        foreach (var item in items)
        {
            if (item.JobId != batch.JobId)
            {
                throw new ArgumentException("Item '" + item.SourceKey + "' belongs to another job.", nameof(items));
            }
            RequireText(item.ImporterId, nameof(item.ImporterId));
            RequireText(item.SourceKey, nameof(item.SourceKey));
            RequireText(item.TargetId, nameof(item.TargetId));
            RequireText(item.TargetVersion, nameof(item.TargetVersion));
            if (!IsSha256Hex(item.ContentHash))
            {
                throw new ArgumentException("Item '" + item.SourceKey + "' has no content hash in lowercase hex.", nameof(items));
            }
            if (!identities.Add((item.ImporterId, item.SourceKey, item.TargetVersion)))
            {
                throw new ArgumentException("Item '" + item.SourceKey + "' appears twice in one batch.", nameof(items));
            }
        }
    }

    private static void RequireText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A value is required for " + name + ".", name);
        }
    }
}
