using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Wire.Connection;
using Bazaar.Server.Wire.Sync;

namespace Bazaar.Server.GcReactions;

/// <summary>
/// Pushes live-only events (a reaction, a typing signal) to the connections subscribed to a GC's stream. Those connections passed
/// the GC's access check when they subscribed and lose the subscription on <c>stream.revoked</c>, so no second check is needed.
/// A full queue drops the event: both kinds are best-effort by design (docs/design/02), and a client refills reaction state with
/// <c>gc.reactions</c>.
/// </summary>
public sealed class GcLiveBroadcaster(IWireConnectionDirectory connections, SyncOptions sync, TimeProvider time)
{
    /// <summary>Sends one event of <paramref name="type"/> on <paramref name="stream"/> to every subscribed connection except those of <paramref name="exceptPrincipal"/>.</summary>
    /// <returns>How many connections the event was queued to.</returns>
    public int Publish<T>(Guid gcId, string type, string stream, long? n, T payload, Guid? exceptPrincipal = null)
    {
        var subscribedTo = WireStreams.Gc(gcId);
        var now = time.GetUtcNow();
        var sent = 0;
        foreach (var connection in connections.Connections)
        {
            if (exceptPrincipal is { } except && connection.Principal.Id == except)
                continue;
            if (connection.Subscriptions is not { } subscriptions || !subscriptions.Streams.Contains(subscribedTo))
                continue;

            var frame = new Envelope(sync.ProtocolMajor, type, EnvelopeKind.Event, null, null, stream, n, now, Encode(connection.Codec, payload));
            if (connection.TryEnqueueEvent(frame))
                sent++;
        }

        return sent;
    }

    private static ReadOnlyMemory<byte> Encode<T>(IWireCodec codec, T payload) => codec.EncodePayload(payload);
}
