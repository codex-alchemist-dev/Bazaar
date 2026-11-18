namespace Bazaar.Client.OfflineSync;

/// <summary>Size limits for the offline queue (doc 56 section 6). Operator-tunable; the defaults are 50,000 writes or 256 MiB, whichever is hit first.</summary>
/// <param name="MaxWrites">Most writes the queue holds before it refuses new ones.</param>
/// <param name="MaxBytes">Most record bytes (framing included) the queue holds before it refuses new ones.</param>
public sealed record OfflineQueueLimits(int MaxWrites = 50_000, long MaxBytes = 256L * 1024 * 1024)
{
    /// <summary>The default limits.</summary>
    public static OfflineQueueLimits Default { get; } = new();
}
