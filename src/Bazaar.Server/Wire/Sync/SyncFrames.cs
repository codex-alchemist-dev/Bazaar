using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;

namespace Bazaar.Server.Wire.Sync;

/// <summary>Builds the server-originated event frames the sync layer sends.</summary>
internal sealed class SyncFrames(IWireCodec codec, SyncOptions options, TimeProvider time)
{
    public Envelope Message(string stream, WireGcMessage message) =>
        Event(WireTypes.GcMessage, stream, message.N, codec.EncodePayload(message));

    public Envelope Lagged(string stream, long lastSent) =>
        Event(WireTypes.Lagged, stream, null, codec.EncodePayload(new Lagged(stream, lastSent)));

    public Envelope Revoked(string stream) =>
        Event(WireTypes.StreamRevoked, stream, null, codec.EncodePayload(new StreamRevoked(stream)));

    public Envelope Resync(string stream, GcHead head, int unread, IReadOnlyList<WireGcMessage> tail) =>
        Event(WireTypes.Resync, stream, null, codec.EncodePayload(new Resync(stream, head.LatestN, head.LatestEv, unread, tail)));

    private Envelope Event(string type, string stream, long? n, ReadOnlyMemory<byte> payload) =>
        new(options.ProtocolMajor, type, EnvelopeKind.Event, null, null, stream, n, time.GetUtcNow(), payload);
}
