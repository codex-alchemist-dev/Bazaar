using Bazaar.Wire.Abstractions;

namespace Bazaar.Client.Transport.AndroidHttp3;

/// <summary>
/// The <c>AndroidHttp3</c> transport (doc 90 section 9): <c>grpc</c> framing over a Cronet HTTP/3 bidirectional stream
/// to the server's existing <c>grpc</c> endpoint. It advertises the wire id <c>grpc</c> and is registered ahead of the
/// plain <c>grpc</c> connector, so <see cref="WireConnectLadder"/> tries it first. Anything that stops HTTP/3 (no Cronet,
/// UDP blocked, Cronet settling on h2, a non-200 answer) throws <see cref="WireFallbackException"/>.
/// </summary>
public sealed class CronetHttp3Transport : IWireTransportConnector
{
    /// <summary>Human-readable module name, for observers and logs.</summary>
    public const string Name = "AndroidHttp3";

    private readonly ICronetEngine _engine;
    private readonly CronetHttp3Options _options;

    /// <summary>Creates the transport over a Cronet engine.</summary>
    public CronetHttp3Transport(ICronetEngine engine, CronetHttp3Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _options = options ?? new CronetHttp3Options();
    }

    /// <inheritdoc />
    public string Transport => WireNames.Grpc;

    /// <inheritdoc />
    public bool IsSupported => _engine.IsAvailable;

    /// <inheritdoc />
    public async ValueTask<IWireConnection> ConnectAsync(TransportOffer offer, IReadOnlyList<string> codecs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(codecs);
        if (codecs.Count == 0) throw new ArgumentException("At least one codec is required.", nameof(codecs));
        if (!_engine.IsAvailable) throw new WireFallbackException("Cronet is not available on this device.");

        var observer = _options.Observer ?? NullWireConnectionObserver.Instance;
        var url = BuildUrl(offer);
        var headers = new List<KeyValuePair<string, string>>
        {
            new("content-type", "application/grpc"),
            new("te", "trailers"),
            new(GrpcMetadata.Codec, string.Join(',', codecs)),
        };
        var credential = _options.CredentialProvider is null ? null : await _options.CredentialProvider(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(credential)) headers.Add(new(GrpcMetadata.Authorization, "Bearer " + credential));
        if (_options.DeviceId is not null) headers.Add(new(GrpcMetadata.Device, _options.DeviceId));

        ICronetBidirectionalStream? stream = null;
        try
        {
            stream = await _engine.OpenStreamAsync(new CronetStreamRequest { Url = url, Headers = headers }, cancellationToken).ConfigureAwait(false);
            var head = await stream.ReadResponseHeadAsync(cancellationToken).ConfigureAwait(false);
            observer.OnNegotiated(head.NegotiatedProtocol);
            if (!string.Equals(head.NegotiatedProtocol, "h3", StringComparison.Ordinal))
            {
                throw new WireFallbackException("Cronet negotiated " + head.NegotiatedProtocol + ", not h3.");
            }

            if (head.HttpStatus != 200) throw new WireFallbackException("HTTP status " + head.HttpStatus.ToString(System.Globalization.CultureInfo.InvariantCulture) + " from the endpoint.");
            if (!head.Headers.TryGetValue(GrpcMetadata.Codec, out var codec))
            {
                // A refusal arrives as a trailers-only response: the status is in the headers.
                if (GrpcMetadata.IsNonZeroStatus(head.Headers))
                {
                    var code = GrpcMetadata.CloseCodeOfStatus(head.Headers);
                    throw new WireConnectRefusedException("The server refused the connection.", code);
                }

                throw new WireFallbackException("The endpoint did not answer like the gRPC session service.");
            }

            if (!codecs.Contains(codec, StringComparer.Ordinal))
            {
                throw new InvalidDataException("The server chose a codec the client did not offer.");
            }

            return new CronetWireConnection(stream, new WireProfile(Transport, codec), _options.MaxFrameBytes, observer);
        }
        catch (Exception ex) when (ex is not (WireFallbackException or WireConnectRefusedException or InvalidDataException or OperationCanceledException))
        {
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
            throw new WireFallbackException("Cronet could not open the stream: " + ex.GetType().Name, ex);
        }
        catch
        {
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static Uri BuildUrl(TransportOffer offer)
    {
        var address = Uri.TryCreate(offer.Endpoint, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri
            : new Uri("https://" + offer.Endpoint, UriKind.Absolute);
        if (address.Scheme != Uri.UriSchemeHttps) throw new WireFallbackException("HTTP/3 needs an https endpoint.");
        return new UriBuilder(address) { Path = GrpcMetadata.MethodPath, Query = string.Empty }.Uri;
    }
}

/// <summary>Settings of <see cref="CronetHttp3Transport"/>. The client head fills them; the connection layer owns the credential.</summary>
public sealed class CronetHttp3Options
{
    /// <summary>Supplies the bearer credential for each connect; null sends none. Never logged.</summary>
    public Func<CancellationToken, ValueTask<string?>>? CredentialProvider { get; set; }

    /// <summary>The device id sent as <c>bazaar-device</c>, or null.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Largest frame in bytes in either direction (<c>wire.max_frame_kib</c>, default 256 KiB); checked on the length before any decode.</summary>
    public int MaxFrameBytes { get; set; } = 256 * 1024;

    /// <summary>Receives negotiation, network, reconnect and fallback events; null ignores them.</summary>
    public IWireConnectionObserver? Observer { get; set; }
}
