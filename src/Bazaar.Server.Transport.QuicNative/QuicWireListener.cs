using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Bazaar.Wire.Abstractions;

namespace Bazaar.Server.Transport.QuicNative;

/// <summary>Settings of the <c>quic</c> listener (doc 90 sections 8 and 9).</summary>
public sealed class QuicWireListenerOptions
{
    /// <summary>Local address and UDP port to listen on.</summary>
    public required IPEndPoint ListenEndPoint { get; init; }

    /// <summary>Server certificate; TLS 1.3 is the only version QUIC runs.</summary>
    public required X509Certificate2 Certificate { get; init; }

    /// <summary>Public <c>host:port</c> published in <see cref="QuicWireListener.Offers"/>; null publishes the bound endpoint.</summary>
    public string? AdvertisedEndpoint { get; init; }

    /// <summary>Codecs the operator enabled for this transport, in server preference order.</summary>
    public IReadOnlyList<string> Codecs { get; init; } = ["json"];

    /// <summary>Largest frame in bytes (<c>wire.max_frame_kib</c>).</summary>
    public int MaxFrameBytes { get; init; } = 256 * 1024;

    /// <summary>Wire heartbeat interval (<c>wire.heartbeat_interval_s</c>); QUIC keep-alive is half of it.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Time allowed from connect to the first frame (<c>wire.hello_timeout_s</c>).</summary>
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Connections allowed in the handshake at once; more are closed with 4429.</summary>
    public int MaxPendingHandshakes { get; init; } = 256;

    /// <summary>
    /// <c>wire.quic.stream_per_subscription</c> (doc 90 section 9). Not implemented yet: true makes
    /// <see cref="QuicWireListener.StartAsync"/> throw <see cref="NotSupportedException"/> rather than be ignored.
    /// </summary>
    public bool StreamPerSubscription { get; init; }

    /// <summary>
    /// Optional sink for handshake diagnostics (client SNI, ALPN offered and negotiated, QUIC error codes, exception chain).
    /// Never throws into the listener; null drops the text.
    /// </summary>
    public Action<string>? Diagnostics { get; init; }
}

/// <summary>
/// The <c>quic</c> server transport: accepts <c>System.Net.Quic</c> connections with ALPN <c>bazaar-wire/1</c> (TLS 1.3,
/// no 0-RTT), reads the codec preface from the first bidirectional stream, and hands each connection to the connection
/// layer through <see cref="Accepted"/>.
/// </summary>
public sealed class QuicWireListener : IAcceptingWireTransportListener, IAsyncDisposable
{
    private readonly QuicWireListenerOptions _options;
    private readonly Channel<IWireConnection> _accepted = Channel.CreateBounded<IWireConnection>(new BoundedChannelOptions(64) { SingleReader = false, SingleWriter = false });
    private readonly SemaphoreSlim _pending;
    private readonly CancellationTokenSource _stop = new();
    private QuicListener? _listener;
    private Task? _acceptLoop;
    private volatile IReadOnlyList<TransportOffer> _offers = [];

    /// <summary>Creates a listener; nothing is bound until <see cref="StartAsync"/>.</summary>
    public QuicWireListener(QuicWireListenerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Codecs.Count == 0) throw new ArgumentException("At least one codec is needed.", nameof(options));
        _options = options;
        _pending = new SemaphoreSlim(Math.Max(1, options.MaxPendingHandshakes));
    }

    /// <inheritdoc />
    public string Transport => WireNames.Quic;

    /// <inheritdoc />
    public bool IsSupported => QuicListener.IsSupported;

    /// <inheritdoc />
    public IReadOnlyList<TransportOffer> Offers => _offers;

    /// <inheritdoc />
    public System.Threading.Channels.ChannelReader<IWireConnection> Accepted => _accepted.Reader;

    /// <summary>The bound endpoint once started (useful when port 0 was asked for).</summary>
    public IPEndPoint? LocalEndPoint => _listener?.LocalEndPoint;

    /// <inheritdoc />
    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (_options.StreamPerSubscription) throw new NotSupportedException("wire.quic.stream_per_subscription is not implemented yet (doc 90 section 9).");
        if (!IsSupported) throw new PlatformNotSupportedException("System.Net.Quic is not available here (MsQuic missing).");
        if (_listener is not null) throw new InvalidOperationException("Already started.");
        var keepAlive = _options.HeartbeatInterval / 2;
        _listener = await QuicListener.ListenAsync(
            new QuicListenerOptions
            {
                ListenEndPoint = _options.ListenEndPoint,
                ApplicationProtocols = QuicWireSupport.CreateApplicationProtocols(),
                ConnectionOptionsCallback = (_, hello, _) => ValueTask.FromResult(Describe(hello, new QuicServerConnectionOptions
                {
                    DefaultStreamErrorCode = StreamWireConnection.BadFrame,
                    DefaultCloseErrorCode = 0,
                    MaxInboundBidirectionalStreams = 1,
                    MaxInboundUnidirectionalStreams = 0,
                    KeepAliveInterval = keepAlive,
                    IdleTimeout = _options.HeartbeatInterval * 3,
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions
                    {
                        ApplicationProtocols = QuicWireSupport.CreateApplicationProtocols(),
                        ServerCertificate = _options.Certificate,
                        ClientCertificateRequired = false,
                    },
                })),
            },
            cancellationToken).ConfigureAwait(false);
        var endpoint = _options.AdvertisedEndpoint ?? FormattableString.Invariant($"{_listener.LocalEndPoint.Address}:{_listener.LocalEndPoint.Port}");
        _offers = [new TransportOffer(WireNames.Quic, endpoint, QuicWireSupport.Alpn, [.. _options.Codecs])];
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_listener, _stop.Token), CancellationToken.None);
    }

    private QuicServerConnectionOptions Describe(SslClientHelloInfo hello, QuicServerConnectionOptions options)
    {
        Report($"{QuicWireSupport.DiagnosticsMarker} operation=accept-hello sni={hello.ServerName ?? "(none)"} tls={hello.SslProtocols} alpn-listening=[{string.Join(", ", QuicWireSupport.CreateApplicationProtocols())}] endpoint={_options.ListenEndPoint}");
        return options;
    }

    private void Report(string text)
    {
        try
        {
            _options.Diagnostics?.Invoke(text);
        }
        catch (Exception)
        {
            // A diagnostics sink must never break the listener.
        }
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_listener is { } listener)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
            _listener = null;
        }

        if (_acceptLoop is { } loop)
        {
            try
            {
                await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                // Stopping.
            }
        }

        _offers = [];
        _accepted.Writer.TryComplete();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _stop.Dispose();
        _pending.Dispose();
    }

    private async Task AcceptLoopAsync(QuicListener listener, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var connection = await listener.AcceptConnectionAsync(stop).ConfigureAwait(false);
                _ = Task.Run(() => HandleAsync(connection, stop), CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or QuicException)
        {
            // Listener stopped.
            if (ex is QuicException) Report(QuicWireSupport.DescribeFailure("accept-loop-ended", _options.ListenEndPoint.ToString(), QuicWireSupport.CreateApplicationProtocols(), null, ex));
        }
        finally
        {
            _accepted.Writer.TryComplete();
        }
    }

    private async Task HandleAsync(QuicConnection connection, CancellationToken stop)
    {
        var handedOff = false;
        QuicStream? stream = null;
        try
        {
            if (!_pending.Wait(0, stop))
            {
                await connection.CloseAsync(4429, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            string? codec;
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
                limit.CancelAfter(_options.HelloTimeout);
                stream = await connection.AcceptInboundStreamAsync(limit.Token).ConfigureAwait(false);
                var left = _options.HelloTimeout - System.Diagnostics.Stopwatch.GetElapsedTime(started);
                codec = await WirePrefaceHandshake.ServerAsync(stream, _options.Codecs, left < TimeSpan.Zero ? TimeSpan.Zero : left, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
            {
                await connection.CloseAsync(StreamWireConnection.HelloTimeout, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException or QuicException)
            {
                Report(QuicWireSupport.DescribeFailure("accept-preface", ListenerEndpointText(connection), QuicWireSupport.CreateApplicationProtocols(), connection, ex));
                await connection.CloseAsync(StreamWireConnection.BadFrame, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            finally
            {
                _pending.Release();
            }

            if (codec is null)
            {
                await connection.CloseAsync(StreamWireConnection.BadFrame, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var left2 = _options.HelloTimeout - System.Diagnostics.Stopwatch.GetElapsedTime(started);
            var wire = new StreamWireConnection(
                stream,
                new WireProfile(WireNames.Quic, codec),
                new StreamWireConnectionOptions
                {
                    MaxFrameBytes = _options.MaxFrameBytes,
                    PreHelloTimeout = left2 < TimeSpan.Zero ? TimeSpan.Zero : left2,
                    CloseAsync = code => connection.CloseAsync(code),
                    CloseCodeFromException = QuicWireSupport.CloseCodeFromException,
                    ReleaseAsync = connection.DisposeAsync,
                });
            await _accepted.Writer.WriteAsync(wire, stop).ConfigureAwait(false);
            handedOff = true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException or ObjectDisposedException or QuicException)
        {
            // Stopping, or the peer left during the handshake.
            if (ex is not OperationCanceledException) Report(QuicWireSupport.DescribeFailure("accept-handshake", ListenerEndpointText(connection), QuicWireSupport.CreateApplicationProtocols(), connection, ex));
        }
        finally
        {
            if (!handedOff)
            {
                if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private string ListenerEndpointText(QuicConnection connection)
    {
        try
        {
            return $"{_options.ListenEndPoint} (peer {connection.RemoteEndPoint}, sni {connection.TargetHostName})";
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            return _options.ListenEndPoint.ToString();
        }
    }
}
