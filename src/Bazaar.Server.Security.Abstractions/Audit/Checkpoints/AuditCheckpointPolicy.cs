namespace Bazaar.Server.Security.Audit.Checkpoints;

/// <summary>
/// When the audit service writes a checkpoint (design doc 48, section 5.2): every N rows or every T minutes, whichever
/// comes first. Both values are operator settings. The floors and bounds below are not settings; a setting may
/// tighten them but never loosen them.
/// </summary>
public sealed record AuditCheckpointPolicy(int EveryRows, TimeSpan EveryInterval)
{
    /// <summary>Smallest row count between checkpoints: one row.</summary>
    public const int MinEveryRows = 1;

    /// <summary>Largest row count between checkpoints. Keeps a Merkle tree small enough to rebuild in memory.</summary>
    public const int MaxEveryRows = 1_000_000;

    /// <summary>Shortest time between checkpoints.</summary>
    public static readonly TimeSpan MinEveryInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Longest time between checkpoints. Section 5.2 requires a signed checkpoint at least daily, so the time rule
    /// can never be longer than a day.
    /// </summary>
    public static readonly TimeSpan MaxEveryInterval = TimeSpan.FromDays(1);

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> when a value is outside the floors and bounds above.</summary>
    public void Validate()
    {
        if (EveryRows < MinEveryRows || EveryRows > MaxEveryRows)
        {
            throw new ArgumentOutOfRangeException(nameof(EveryRows), EveryRows, $"Must be between {MinEveryRows} and {MaxEveryRows}.");
        }

        if (EveryInterval < MinEveryInterval || EveryInterval > MaxEveryInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(EveryInterval), EveryInterval, $"Must be between {MinEveryInterval} and {MaxEveryInterval}.");
        }
    }

    /// <summary>
    /// True when a checkpoint is due: at least <see cref="EveryRows"/> rows since the last one, or at least
    /// <see cref="EveryInterval"/> since <paramref name="lastCheckpointAt"/>. Zero rows never triggers a checkpoint,
    /// because an empty range has no last sequence number to record.
    /// </summary>
    public bool ShouldCheckpoint(long rowsSinceLast, DateTimeOffset lastCheckpointAt, DateTimeOffset now)
    {
        if (rowsSinceLast < 1)
        {
            return false;
        }

        if (rowsSinceLast >= EveryRows)
        {
            return true;
        }

        return now - lastCheckpointAt >= EveryInterval;
    }
}
