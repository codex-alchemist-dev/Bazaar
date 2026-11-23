using Bazaar.Wire.Abstractions;

namespace Bazaar.Client.Transport.AndroidHttp3;

/// <summary>
/// Walks the transport ladder (doc 90 section 9): the server's offers ordered by the client's transport preference
/// (<see cref="WireNegotiator.OrderOffers"/>), and for each offer every connector of that transport in registration order,
/// so <see cref="CronetHttp3Transport"/> registered ahead of <c>grpc</c> over HTTP/2 comes first, then <c>ws</c>.
/// A <see cref="WireFallbackException"/> or an I/O failure moves on; a refusal by the server stops the walk.
/// </summary>
public sealed class WireConnectLadder
{
    private readonly IReadOnlyList<IWireTransportConnector> _connectors;
    private readonly IReadOnlyList<string> _transportOrder;
    private readonly IWireConnectionObserver _observer;

    /// <summary>Creates a ladder.</summary>
    /// <param name="connectors">Connectors in ladder order; several may share a transport id.</param>
    /// <param name="transportOrder">The client's transport preference, such as <c>grpc</c> then <c>ws</c>.</param>
    /// <param name="observer">Told about each fallback; null ignores them.</param>
    public WireConnectLadder(IReadOnlyList<IWireTransportConnector> connectors, IReadOnlyList<string> transportOrder, IWireConnectionObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(connectors);
        ArgumentNullException.ThrowIfNull(transportOrder);
        _connectors = connectors;
        _transportOrder = transportOrder;
        _observer = observer ?? NullWireConnectionObserver.Instance;
    }

    /// <summary>Connects through the first rung that works.</summary>
    /// <exception cref="IOException">Every rung failed; the inner exception holds each failure.</exception>
    public async ValueTask<IWireConnection> ConnectAsync(IReadOnlyList<TransportOffer> offers, IReadOnlyList<string> codecs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offers);
        ArgumentNullException.ThrowIfNull(codecs);
        var failures = new List<Exception>();
        var ordered = WireNegotiator.OrderOffers(offers, _transportOrder, t => _connectors.Any(c => c.IsSupported && c.Transport == t));
        foreach (var offer in ordered)
        {
            var usable = codecs.Where(c => offer.Codecs.Contains(c, StringComparer.Ordinal)).ToList();
            if (usable.Count == 0) continue;
            foreach (var connector in _connectors.Where(c => c.IsSupported && c.Transport == offer.Transport))
            {
                try
                {
                    return await connector.ConnectAsync(offer, usable, cancellationToken).ConfigureAwait(false);
                }
                catch (WireConnectRefusedException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.Net.Http.HttpRequestException)
                {
                    failures.Add(ex);
                    _observer.OnFallback(connector is CronetHttp3Transport ? CronetHttp3Transport.Name : connector.Transport, ex.Message);
                }
            }
        }

        throw new IOException("No transport on the ladder could connect.", new AggregateException(failures));
    }
}
