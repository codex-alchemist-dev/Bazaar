using Bazaar.Contracts.Gc;

namespace Bazaar.Server.Gc.Consumers;

/// <summary>
/// A software consumer of the GC log (docs/design/01, Consumers): notifier, unread counter, search indexer, Kanban view.
/// <see cref="GcConsumerRunner"/> feeds it each GC's events after its cursor, in seq order.
/// </summary>
public interface IGcConsumer
{
    /// <summary>The cursor key in <c>consumer_cursor</c>. Changing it makes the consumer start again from seq 0.</summary>
    string Name { get; }

    /// <summary>
    /// Handles one contiguous batch of events after the consumer's cursor, in seq order. Returning normally commits the batch;
    /// throwing leaves the cursor where it was, so the batch is delivered again. Implementations must therefore be idempotent on
    /// <c>(consumer, gc_id, seq)</c>.
    /// </summary>
    Task HandleAsync(Guid gcId, IReadOnlyList<GcEvent> events, CancellationToken ct);
}
