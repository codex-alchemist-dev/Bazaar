using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.Studio.Abstractions;
using Bazaar.Contracts.Engines;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;

namespace Bazaar.Client.Studio;

/// <summary>
/// <see cref="IRelayEventSource"/> over the client's wire connection (doc 02, doc 20): subscribes to <see cref="EngineRelayWire.Stream"/>
/// (the client resubscribes on every reconnect), decodes <see cref="EngineRelayWire.EventType"/> events and yields them. The stream is
/// state with no replay, so events missed while offline are gone and the server already failed those calls. A frame that does not
/// decode is dropped. At most <see cref="Capacity"/> events wait for the loop; past that the oldest is dropped, because the server
/// stops waiting at the deadline anyway. One reader at a time.
/// </summary>
public sealed class WireRelayEventSource : IRelayEventSource
{
    /// <summary>Events held while the loop is busy.</summary>
    public const int Capacity = 64;

    private readonly WireClient _client;
    private readonly IWireCodec _codec;
    private readonly Channel<EngineRelayRequestEvent> _events = Channel.CreateBounded<EngineRelayRequestEvent>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    /// <summary>Creates the source over <paramref name="client"/>, decoding with <paramref name="codec"/> (the client's codec).</summary>
    public WireRelayEventSource(WireClient client, IWireCodec codec)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<EngineRelayRequestEvent> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using IDisposable registration = _client.RegisterStreamSink(EngineRelayWire.Stream, Offer);
        await _client.SubscribeAsync(EngineRelayWire.Stream, cancellationToken).ConfigureAwait(false);
        try
        {
            await foreach (var evt in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            await _client.UnsubscribeAsync(EngineRelayWire.Stream, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Decodes one envelope from the relay stream and queues it. Anything else, or a frame that does not decode, is dropped.</summary>
    internal void Offer(Envelope envelope)
    {
        if (!string.Equals(envelope.T, EngineRelayWire.EventType, StringComparison.Ordinal) || envelope.K != EnvelopeKind.Event)
        {
            return;
        }

        try
        {
            _events.Writer.TryWrite(EngineRelayFrameMapping.ToEvent(_codec.DecodePayload<EngineRelayRequestFrame>(envelope)));
        }
        catch (Exception ex) when (ex is WireFormatException or FormatException)
        {
            // A frame the client cannot read is the server's problem; the server times the call out.
        }
    }
}
