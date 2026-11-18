namespace Bazaar.Server.Retention;

/// <summary>
/// One category of data with a retention period (doc 48 section 9). A category knows its own period setting, the persons whose
/// data it holds, and how to purge one bounded batch. <see cref="RetentionSweeper"/> owns the legal-hold check, the audit rows and
/// the loop, so a category implements none of that. Adding a category is one class plus one registration; only add one when the
/// docs specify its table and its behaviour.
/// </summary>
public interface IRetentionCategory
{
    /// <summary>A stable name used in audit rows, for example <c>gateway_usage</c>. Never content.</summary>
    string Name { get; }

    /// <summary>How long rows are kept, or null for indefinite (the sweep is then a no-op).</summary>
    Task<TimeSpan?> GetPeriodAsync(CancellationToken ct);

    /// <summary>The largest number of rows one purge call may remove. Must be at least one.</summary>
    Task<int> GetBatchSizeAsync(CancellationToken ct);

    /// <summary>
    /// Persons whose data this category holds, for the person-hold check. Empty when rows are not tied to a person. If any is
    /// under a hold, the whole category is skipped (fail closed).
    /// </summary>
    Task<IReadOnlyCollection<Guid>> GetCoveredPersonsAsync(CancellationToken ct);

    /// <summary>Removes at most <paramref name="batchSize"/> rows older than <paramref name="cutoff"/>. Returns the number removed.</summary>
    Task<int> PurgeBatchAsync(DateTimeOffset cutoff, int batchSize, CancellationToken ct);
}
