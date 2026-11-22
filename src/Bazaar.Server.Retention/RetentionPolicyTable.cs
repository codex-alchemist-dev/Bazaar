namespace Bazaar.Server.Retention;

/// <summary>
/// The retention table. Invalid rows fail at construction, so a bad setting stops the host instead of deleting data.
/// A GC log category may never be purged: its <see cref="ErasureMethod"/> must be <see cref="ErasureMethod.None"/> or
/// <see cref="ErasureMethod.CryptoErase"/> (doc 01, doc 48 section 9).
/// </summary>
public sealed class RetentionPolicyTable : IRetentionPolicy
{
    /// <summary>The category ids that are never purged, whatever the settings say.</summary>
    public static readonly IReadOnlySet<string> NeverPurged = new HashSet<string>(StringComparer.Ordinal) { "gc_log" };

    private readonly Dictionary<string, RetentionCategory> _rows;

    public RetentionPolicyTable(IEnumerable<RetentionCategory> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _rows = new Dictionary<string, RetentionCategory>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(row.Category);
            if (row.Period is { } period && period <= TimeSpan.Zero)
                throw new ArgumentException($"Retention period for '{row.Category}' must be positive.", nameof(rows));
            if (row.SoftDeleteWindow < TimeSpan.Zero)
                throw new ArgumentException($"Soft-delete window for '{row.Category}' cannot be negative.", nameof(rows));
            if (NeverPurged.Contains(row.Category) && row.Method == ErasureMethod.SoftDeleteThenPurge)
                throw new ArgumentException($"'{row.Category}' is never purged; use crypto-erase or keep.", nameof(rows));
            if (!_rows.TryAdd(row.Category, row))
                throw new ArgumentException($"Duplicate retention category '{row.Category}'.", nameof(rows));
        }
    }

    public RetentionCategory? Find(string category) => _rows.GetValueOrDefault(category);
}
