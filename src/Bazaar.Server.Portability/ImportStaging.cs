namespace Bazaar.Server.Portability;

/// <summary>
/// Staging retention setting (doc 44 section 8.6). The minimum and the default are 72 hours, because staging holds
/// plaintext personal content and is kept until it is safe to delete. A longer period is an admin setting, and every
/// change is audited through <see cref="ChangeAsync"/>. This reads the doc's "default 0, floor 72" as one rule: 72 hours.
/// </summary>
public sealed record StagingRetention
{
    /// <summary>The shortest retention, and the default.</summary>
    public const int MinimumHours = 72;

    /// <summary>The audit kind written when the setting changes.</summary>
    public const string ChangeAuditKind = "portability.import.staging_retention_changed";

    public StagingRetention(int hours)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(hours, MinimumHours);
        Hours = hours;
    }

    public static StagingRetention Default { get; } = new(MinimumHours);

    public int Hours { get; }

    /// <summary>When staging for a job verified at <paramref name="verifiedAt"/> may be deleted.</summary>
    public DateTimeOffset DeleteAfter(DateTimeOffset verifiedAt) => verifiedAt.AddHours(Hours);

    public bool IsDue(DateTimeOffset verifiedAt, DateTimeOffset now) => now >= DeleteAfter(verifiedAt);

    /// <summary>Applies a new retention and audits the change. The audit entry is written first; if it fails nothing changes.</summary>
    public async Task<StagingRetention> ChangeAsync(int newHours, string actor, IAuditSink audit, TimeProvider now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(actor);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(now);

        var next = new StagingRetention(newHours);
        var detail = Hours.ToString(System.Globalization.CultureInfo.InvariantCulture) + "->" + newHours.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await audit.AppendAsync(new ImportAuditRecord(ChangeAuditKind, actor, 1, 1, detail, now.GetUtcNow()), ct);
        return next;
    }
}

/// <summary>Staging area for uploaded sources and parse output.</summary>
public interface IImportStagingStore
{
    /// <summary>Verified jobs whose staging still exists, with their verification time.</summary>
    Task<IReadOnlyList<(string JobId, DateTimeOffset VerifiedAt)>> ListVerifiedAsync(CancellationToken ct);

    /// <summary>Deletes the staged source and parse output of one job. The job keeps its manifest, hashes and counts.</summary>
    Task DeleteAsync(string jobId, CancellationToken ct);
}

/// <summary>Deletes staging that has outlived its retention.</summary>
public static class ImportStagingSweeper
{
    /// <summary>Returns the job ids whose staging was deleted. A failing delete is skipped and retried on the next sweep.</summary>
    public static async Task<IReadOnlyList<string>> SweepAsync(IImportStagingStore store, StagingRetention retention, TimeProvider now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(retention);
        ArgumentNullException.ThrowIfNull(now);

        var deleted = new List<string>();
        foreach (var (jobId, verifiedAt) in await store.ListVerifiedAsync(ct))
        {
            if (!retention.IsDue(verifiedAt, now.GetUtcNow()))
            {
                continue;
            }
            try
            {
                await store.DeleteAsync(jobId, ct);
                deleted.Add(jobId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Left in place; the next sweep retries.
            }
        }
        return deleted;
    }
}

/// <summary>Batch sizing by items and bytes (doc 44 section 8.7). A batch closes at whichever cap is reached first.</summary>
public static class ImportBatcher
{
    public const int DefaultBatchItems = 500;

    public const long DefaultBatchBytes = 8L * 1024 * 1024;

    /// <summary>Splits items into consecutive batches. A single item over the byte cap gets a batch of its own.</summary>
    public static IReadOnlyList<IReadOnlyList<T>> Split<T>(IEnumerable<T> items, Func<T, long> sizeOf, int maxItems = DefaultBatchItems, long maxBytes = DefaultBatchBytes)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(sizeOf);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxItems, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1L);

        var batches = new List<IReadOnlyList<T>>();
        var current = new List<T>();
        long bytes = 0;
        foreach (var item in items)
        {
            var size = sizeOf(item);
            if (current.Count > 0 && (current.Count >= maxItems || bytes + size > maxBytes))
            {
                batches.Add(current);
                current = [];
                bytes = 0;
            }
            current.Add(item);
            bytes += size;
        }
        if (current.Count > 0)
        {
            batches.Add(current);
        }
        return batches;
    }
}

/// <summary>Marks a job with no progress for too long as stalled (doc 44 section 8.7).</summary>
public static class ImportStallDetector
{
    public static bool IsStalled(DateTimeOffset lastProgressAt, DateTimeOffset now, TimeSpan threshold)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(threshold, TimeSpan.Zero);
        return now - lastProgressAt >= threshold;
    }
}

/// <summary>
/// Caps writes per second so import stays behind live GC traffic. <see cref="NextDelay"/> says how long to wait before
/// the next write of <c>count</c> items; the caller sleeps that long.
/// </summary>
public sealed class ImportRateLimiter
{
    private readonly double _itemsPerSecond;
    private DateTimeOffset _nextFree;

    public ImportRateLimiter(double itemsPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(itemsPerSecond, 0);
        _itemsPerSecond = itemsPerSecond;
    }

    public TimeSpan NextDelay(int count, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var start = _nextFree > now ? _nextFree : now;
        _nextFree = start + TimeSpan.FromSeconds(count / _itemsPerSecond);
        return start - now;
    }
}
