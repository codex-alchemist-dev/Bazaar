namespace Bazaar.Server.Gc.Consumers;

/// <summary>
/// Each consumer's position in one GC's log (<c>consumer_cursor</c>, docs/design/01, Consumers / read state). Positions only move
/// forward; a lower seq never moves a cursor back.
/// </summary>
public interface IConsumerCursorStore
{
    /// <summary>The consumer's last processed seq in the GC, or 0 when the consumer has never advanced there.</summary>
    Task<long> GetAsync(string consumer, Guid gcId, CancellationToken ct);

    /// <summary>
    /// Moves the cursor to <paramref name="seq"/> when that is ahead of the stored value. Returns the stored value after the call:
    /// it is larger than <paramref name="seq"/> when the cursor was already ahead, and then nothing changed.
    /// </summary>
    Task<long> AdvanceAsync(string consumer, Guid gcId, long seq, CancellationToken ct);
}
