using System.Threading.Channels;
using Bazaar.Wire.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Bazaar.Server.Transport.WebTransport;

/// <summary>Settings of the <c>webtransport</c> listener (doc 90 sections 8 and 9). The transport is off unless <see cref="Enabled"/> is set.</summary>
public sealed class WebTransportWireListenerOptions
{
    /// <summary>Operator switch (owner card 2026-11-19: built now, ships off by default).</summary>
    public bool Enabled { get; init; }

    /// <summary>The request path of the extended CONNECT.</summary>
    public string Path { get; init; } = "/wt";

    /// <summary>Public URL clients connect to (for example <c>https://host:5443/wt</c>); with it unset no offer is published.</summary>
    public string? AdvertisedUrl { get; init; }

    /// <summary>Codecs the operator enabled for this transport, in server preference order.</summary>
    public IReadOnlyList<string> Codecs { get; init; } = ["json"];

    /// <summary>Largest frame in bytes (<c>wire.max_frame_kib</c>).</summary>
    public int MaxFrameBytes { get; init; } = 256 * 1024;

    /// <summary>Time allowed from connect to the first frame (<c>wire.hello_timeout_s</c>).</summary>
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Sessions allowed in the handshake at once; more get HTTP 429.</summary>
    public int MaxPendingHandshakes { get; init; } = 256;

    /// <summary>
    /// Credential check on the CONNECT request headers (doc 90: the credential rides the CONNECT headers). Returns false to
    /// refuse with HTTP 401. When null every request is refused: a transport with no credential check never accepts.
    /// </summary>
    public Func<HttpContext, ValueTask<bool>>? Authorize { get; init; }
}

/// <summary>
/// The <c>webtransport</c> server transport. Mount <see cref="HandleAsync"/> on the configured path of a Kestrel HTTP/3
/// endpoint (see <see cref="WebTransportEndpointExtensions"/>); it accepts the WebTransport session, takes the first
/// bidirectional stream, runs the codec preface and hands the connection to <see cref="Accepted"/>. Framing is the same as <c>quic</c>.
/// </summary>
public sealed class WebTransportWireListener : IAcceptingWireTransportListener, IDisposable
{
    /// <summary>The AppContext switch Kestrel requires for WebTransport (preview-gated in .NET 11).</summary>
    public const string KestrelSwitch = "Microsoft.AspNetCore.Server.Kestrel.Experimental.WebTransportAndH3Datagrams";

    private readonly WebTransportWireListenerOptions _options;
    private readonly Channel<IWireConnection> _accepted = Channel.CreateBounded<IWireConnection>(new BoundedChannelOptions(64));
    private readonly SemaphoreSlim _pending;
    private readonly CancellationTokenSource _stop = new();
    private volatile bool _started;

    /// <summary>Creates a listener.</summary>
    public WebTransportWireListener(WebTransportWireListenerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Codecs.Count == 0) throw new ArgumentException("At least one codec is needed.", nameof(options));
        _options = options;
        _pending = new SemaphoreSlim(Math.Max(1, options.MaxPendingHandshakes));
    }

    /// <inheritdoc />
    public string Transport => WireNames.WebTransport;

    /// <summary>The path to map.</summary>
    public string Path => _options.Path;

    /// <summary>True when the operator enabled the transport and Kestrel's WebTransport switch is on.</summary>
    public bool IsSupported => _options.Enabled && AppContext.TryGetSwitch(KestrelSwitch, out var on) && on;

    /// <inheritdoc />
    public IReadOnlyList<TransportOffer> Offers =>
        _started && _options.AdvertisedUrl is { } url ? [new TransportOffer(WireNames.WebTransport, url, null, [.. _options.Codecs])] : [];

    /// <inheritdoc />
    public ChannelReader<IWireConnection> Accepted => _accepted.Reader;

    /// <inheritdoc />
    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("WebTransport is off (operator setting) or Kestrel's WebTransport switch is not set.");
        _started = true;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _started = false;
        await _stop.CancelAsync().ConfigureAwait(false);
        _accepted.Writer.TryComplete();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Dispose();
        _pending.Dispose();
    }

    /// <summary>The endpoint handler. Stays alive for the life of the session, as Kestrel requires.</summary>
    public async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var feature = context.Features.Get<IHttpWebTransportFeature>();
        if (!_started || feature is not { IsWebTransportRequest: true })
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (_options.Authorize is not { } authorize || !await authorize(context).ConfigureAwait(false))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!_pending.Wait(0))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        var released = false;
        void Release()
        {
            if (!released) { released = true; _pending.Release(); }
        }

        IWebTransportSession? session = null;
        DuplexPipeStream? stream = null;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handedOff = false;
        try
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, context.RequestAborted);
            limit.CancelAfter(_options.HelloTimeout);
            session = await feature.AcceptAsync(limit.Token).ConfigureAwait(false);
            string? codec;
            try
            {
                var raw = await session.AcceptStreamAsync(limit.Token).ConfigureAwait(false);
                if (raw is null || raw.Features.Get<IStreamDirectionFeature>() is not { CanRead: true, CanWrite: true })
                {
                    session.Abort(StreamWireConnection.BadFrame);
                    return;
                }

                stream = new DuplexPipeStream(raw.Transport);
                var left = _options.HelloTimeout - System.Diagnostics.Stopwatch.GetElapsedTime(started);
                codec = await WirePrefaceHandshake.ServerAsync(stream, _options.Codecs, left < TimeSpan.Zero ? TimeSpan.Zero : left, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_stop.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
            {
                session.Abort(StreamWireConnection.HelloTimeout);
                return;
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException or ConnectionResetException)
            {
                session.Abort(StreamWireConnection.BadFrame);
                return;
            }
            finally
            {
                Release();
            }

            if (codec is null)
            {
                session.Abort(StreamWireConnection.BadFrame);
                return;
            }

            var left2 = _options.HelloTimeout - System.Diagnostics.Stopwatch.GetElapsedTime(started);
            var owned = session;
            var wire = new StreamWireConnection(
                stream,
                new WireProfile(WireNames.WebTransport, codec),
                new StreamWireConnectionOptions
                {
                    MaxFrameBytes = _options.MaxFrameBytes,
                    PreHelloTimeout = left2 < TimeSpan.Zero ? TimeSpan.Zero : left2,
                    CloseAsync = code => { owned.Abort(code); return ValueTask.CompletedTask; },
                    CloseCodeFromException = static ex => ex is ConnectionAbortedException or ConnectionResetException ? 0 : null,
                    ReleaseAsync = () => { done.TrySetResult(); return ValueTask.CompletedTask; },
                });
            await _accepted.Writer.WriteAsync(wire, _stop.Token).ConfigureAwait(false);
            handedOff = true;
            using var gone = context.RequestAborted.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), done);
            await done.Task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException or ObjectDisposedException or ConnectionAbortedException)
        {
            // Stopping, or the peer left.
        }
        finally
        {
            Release();
            if (!handedOff && stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Wiring helpers for the <c>webtransport</c> endpoint.</summary>
public static class WebTransportEndpointExtensions
{
    /// <summary>Sets Kestrel's WebTransport AppContext switch. Call before the host is built; nothing happens for a disabled listener.</summary>
    public static void EnableKestrelWebTransport(WebTransportWireListenerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Enabled) AppContext.SetSwitch(WebTransportWireListener.KestrelSwitch, true);
    }

    /// <summary>Maps the listener's path to its handler (CONNECT, as WebTransport sessions arrive).</summary>
    public static IApplicationBuilder UseWebTransportWire(this IApplicationBuilder app, WebTransportWireListener listener)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(listener);
        return app.Use(async (HttpContext context, Func<Task> next) =>
        {
            if (context.Request.Path.Equals(listener.Path, StringComparison.Ordinal)) await listener.HandleAsync(context).ConfigureAwait(false);
            else await next().ConfigureAwait(false);
        });
    }
}
