using Bazaar.Contracts.Kodo;
using Bazaar.Contracts.Wire;
using Bazaar.Server.Wire.Connection;
using Bazaar.Server.Wire.Sync;

namespace Bazaar.Server.Kodo;

/// <summary>
/// Sends the monitor's <c>state_changed</c> and <c>heartbeat</c> events to every connection subscribed to the <c>kodo</c> stream
/// (doc 46 section 15.2, PROPOSED), the way the presence publisher does. The payload is the UTF-8 JSON of the stream event, which is
/// the frame shape <c>KodoStreamTracker</c> reads. Delivery is best effort: a full outbound queue drops the frame, the client sees
/// the revision gap on the next one and subscribes again for a fresh snapshot. A snapshot event is never broadcast, because each
/// subscriber gets its own from <see cref="KodoStreamGate"/>. With no wire directory (a host without a socket) events go nowhere.
/// </summary>
public sealed class KodoWireStreamPublisher(IWireConnectionDirectory? connections, SyncOptions sync, TimeProvider time) : IKodoStreamPublisher
{
    /// <inheritdoc />
    public ValueTask PublishAsync(KodoStreamEventDto streamEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);
        if (connections is null || streamEvent.Type == KodoStreamEventDto.SnapshotType)
        {
            return ValueTask.CompletedTask;
        }

        byte[] payload = KodoWireCodec.EncodeEvent(streamEvent);
        DateTimeOffset now = time.GetUtcNow();
        foreach (IWireConnection connection in connections.Connections)
        {
            if (connection.Subscriptions is not { } subscriptions || !subscriptions.Streams.Contains(WireStreams.Kodo))
            {
                continue;
            }

            connection.TryEnqueueEvent(new Envelope(
                sync.ProtocolMajor,
                WireTypes.KodoEvent,
                EnvelopeKind.Event,
                null,
                null,
                WireStreams.Kodo,
                null,
                now,
                payload));
        }

        return ValueTask.CompletedTask;
    }
}
