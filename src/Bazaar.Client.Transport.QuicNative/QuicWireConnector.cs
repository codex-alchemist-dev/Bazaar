using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Bazaar.Wire.Abstractions;

namespace Bazaar.Client.Transport.QuicNative;

/// <summary>Settings of the <c>quic</c> connector (doc 90 sections 8 and 9).</summary>
public sealed class QuicWireConnectorOptions
{
    /// <summary>
    /// Certificate pinning hook (doc 39, as Conduit offers). When set it decides alone whether the server certificate is
    /// accepted (the platform chain check is not used); when null the platform validation applies.
    /// </summary>
    public Func<X509Certificate2, bool>? PinCertificate { get; init; }

    /// <summary>Largest frame in bytes (<c>wire.max_frame_kib</c>).</summary>
    public int MaxFrameBytes { get; init; } = 256 * 1024;

    /// <summary>Wire heartbeat interval (<c>wire.heartbeat_interval_s</c>); QUIC keep-alive is half of it.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Time allowed for the connect and codec handshake.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>Pin helpers for <see cref="QuicWireConnectorOptions.PinCertificate"/>.</summary>
public static class QuicCertificatePin
{
    /// <summary>Accepts exactly the certificates whose SHA-256 hash (of the DER bytes) is in <paramref name="sha256Hashes"/>.</summary>
    public static Func<X509Certificate2, bool> Sha256(params IReadOnlyList<byte[]> sha256Hashes)
    {
        ArgumentNullException.ThrowIfNull(sha256Hashes);
        var pins = sha256Hashes.Select(static h => (byte[])h.Clone()).ToArray();
        return certificate => pins.Any(pin => CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), pin));
    }
}

/// <summary>
/// The <c>quic</c> client transport: opens a <c>System.Net.Quic</c> connection with ALPN <c>bazaar-wire/1</c>, one
/// bidirectional control stream, sends the codec preface and returns the connection once the server echoes its choice.
/// </summary>
public sealed class QuicWireConnector : IWireTransportConnector
{
    private readonly QuicWireConnectorOptions _options;

    /// <summary>Creates a connector.</summary>
    public QuicWireConnector(QuicWireConnectorOptions? options = null) => _options = options ?? new QuicWireConnectorOptions();

    /// <inheritdoc />
    public string Transport => WireNames.Quic;

    /// <inheritdoc />
    public bool IsSupported => QuicConnection.IsSupported;

    /// <inheritdoc />
    public async ValueTask<IWireConnection> ConnectAsync(TransportOffer offer, IReadOnlyList<string> codecs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(codecs);
        if (offer.Transport != WireNames.Quic) throw new ArgumentException("Offer is not for the quic transport.", nameof(offer));
        if (offer.Alpn is not null && !string.Equals(offer.Alpn, QuicWireSupport.Alpn, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"The offer names ALPN '{offer.Alpn}', but this client only speaks '{QuicWireSupport.Alpn}'.");
        }

        if (!IsSupported) throw new PlatformNotSupportedException("System.Net.Quic is not available here (MsQuic missing).");
        var (host, port) = QuicWireSupport.ParseEndpoint(offer.Endpoint);
        var pin = _options.PinCertificate;
        var auth = new SslClientAuthenticationOptions
        {
            ApplicationProtocols = QuicWireSupport.CreateApplicationProtocols(),
            TargetHost = host,
        };
        if (pin is not null)
        {
            auth.RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null && pin(certificate as X509Certificate2 ?? new X509Certificate2(certificate));
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_options.ConnectTimeout);
        QuicConnection? connection = null;
        QuicStream? stream = null;
        try
        {
            connection = await QuicConnection.ConnectAsync(
                new QuicClientConnectionOptions
                {
                    RemoteEndPoint = new System.Net.DnsEndPoint(host, port),
                    DefaultStreamErrorCode = StreamWireConnection.BadFrame,
                    DefaultCloseErrorCode = 0,
                    MaxInboundBidirectionalStreams = 0,
                    MaxInboundUnidirectionalStreams = 0,
                    KeepAliveInterval = _options.HeartbeatInterval / 2,
                    IdleTimeout = _options.HeartbeatInterval * 3,
                    ClientAuthenticationOptions = auth,
                },
                limit.Token).ConfigureAwait(false);
            stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, limit.Token).ConfigureAwait(false);
            var codec = await WirePrefaceHandshake.ClientAsync(stream, codecs, limit.Token).ConfigureAwait(false);
            var owned = connection;
            return new StreamWireConnection(
                stream,
                new WireProfile(WireNames.Quic, codec),
                new StreamWireConnectionOptions
                {
                    MaxFrameBytes = _options.MaxFrameBytes,
                    PreHelloMaxBytes = int.MaxValue, // the 8 KiB pre-hello cap guards the server; the client reads welcome
                    PreHelloTimeout = null,
                    CloseAsync = code => owned.CloseAsync(code),
                    CloseCodeFromException = QuicWireSupport.CloseCodeFromException,
                    ReleaseAsync = owned.DisposeAsync,
                });
        }
        catch (Exception ex)
        {
            // Describe before disposing: the negotiated ALPN is gone once the connection is.
            var diagnostics = QuicWireSupport.DescribeFailure("connect", offer.Endpoint, auth.ApplicationProtocols ?? [], connection, ex);
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
            var enriched = QuicWireSupport.WithDiagnostics(ex, diagnostics);
            if (ReferenceEquals(enriched, ex)) throw;
            throw enriched;
        }
    }
}
