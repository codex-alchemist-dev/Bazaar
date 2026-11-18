namespace Bazaar.OfflineSync;

/// <summary>A merge rule for one data class. Pure with respect to the write and the store (doc 52 section 5).</summary>
public interface IOfflineMergeRule
{
    /// <summary>The data class this rule handles.</summary>
    OfflineDataClass DataClass { get; }

    /// <summary>Merges one write and returns its outcome. Must be idempotent per <see cref="OfflineWrite.WriteId"/>.</summary>
    ValueTask<OfflineWriteOutcome> MergeAsync(OfflineWrite write, CancellationToken cancellationToken);
}

/// <summary>The server's record of seen writes and device sequences. Implementations must make <see cref="TryRecordAsync"/> atomic.</summary>
public interface IOfflineSyncStore
{
    /// <summary>The highest device sequence applied for <paramref name="deviceId"/>; 0 when none.</summary>
    ValueTask<long> GetAckedSequenceAsync(Guid deviceId, CancellationToken cancellationToken);

    /// <summary>Returns the stored outcome for a write seen before, or null.</summary>
    ValueTask<OfflineWriteOutcome?> FindOutcomeAsync(Guid deviceId, Guid writeId, CancellationToken cancellationToken);

    /// <summary>Atomically records the outcome and advances the device sequence to <paramref name="sequence"/>.</summary>
    ValueTask RecordAsync(Guid deviceId, long sequence, OfflineWriteOutcome outcome, CancellationToken cancellationToken);
}

/// <summary>Receives one audit record per non-duplicate outcome (doc 49: device, base version, server version, outcome).</summary>
public interface IOfflineMergeAudit
{
    /// <summary>Records a merge.</summary>
    ValueTask RecordAsync(OfflineWrite write, OfflineWriteOutcome outcome, CancellationToken cancellationToken);
}

/// <summary>The server clock, injectable for tests.</summary>
public interface IOfflineClock
{
    /// <summary>Current time in Unix milliseconds.</summary>
    long NowMillis { get; }
}
