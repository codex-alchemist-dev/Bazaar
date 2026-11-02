using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Read;

namespace Bazaar.Server.Gc.Consumers;

/// <summary>
/// Drives one <see cref="IGcConsumer"/> over one GC. It reads the log after the consumer's cursor, hands the batch to the consumer,
/// and advances the cursor only after the consumer returns. A batch that fails is delivered again on the next run (at-least-once).
/// </summary>
/// <remarks>
/// The read goes through <see cref="IGcLogReader"/> with the viewer the caller passes, so the viewer rules apply to consumers too:
/// a GC the viewer may not read raises <see cref="GcAccessDeniedException"/> and the cursor does not move.
/// </remarks>
public sealed class GcConsumerRunner(IGcLogReader reader, IConsumerCursorStore cursors)
{
    /// <summary>Events per batch when the caller does not choose a size.</summary>
    public const int DefaultBatchSize = 500;

    /// <summary>
    /// Delivers every event after the consumer's cursor, up to the GC's head at the time of each read, and returns how many events
    /// were delivered. Stops at the first batch that the reader returns short (the consumer has caught up) or at the first failure.
    /// </summary>
    public async Task<long> RunToHeadAsync(
        IGcConsumer consumer, Guid gcId, GcPrincipal viewer, int batchSize = DefaultBatchSize, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchSize, IGcLogReader.MaxLimit);

        long delivered = 0;
        while (true)
        {
            var afterSeq = await cursors.GetAsync(consumer.Name, gcId, ct).ConfigureAwait(false);
            var batch = new List<GcEvent>(batchSize);
            await foreach (var e in reader.ReadAsync(gcId, afterSeq, batchSize, viewer, ct).ConfigureAwait(false))
                batch.Add(e);
            if (batch.Count == 0)
                return delivered;

            await consumer.HandleAsync(gcId, batch, ct).ConfigureAwait(false);
            await cursors.AdvanceAsync(consumer.Name, gcId, batch[^1].Seq, ct).ConfigureAwait(false);
            delivered += batch.Count;

            if (batch.Count < batchSize)
                return delivered;
        }
    }
}
