using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Bazaar.Client.Transport.Grpc;
using Bazaar.Client.Transport.QuicNative;
using Bazaar.Server.Transport.Grpc;
using Bazaar.Server.Transport.QuicNative;
using Bazaar.Wire.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bazaar.Wire.Bench;

/// <summary>Both ends of one live connection, in this process, plus the ways to drop it.</summary>
internal sealed class ConnectionPair : IAsyncDisposable
{
    /// <summary>The client end, as the client module returns it.</summary>
    public required IWireConnection Client { get; init; }

    /// <summary>The server end, as the server module hands it to the connection layer.</summary>
    public required IWireConnection Server { get; init; }

    /// <summary>Drops the connection from the server side; see <see cref="TransportScenario.DropMethod"/>.</summary>
    public required Func<ValueTask> DropAsync { get; init; }

    /// <summary>Releases what the server handler waits on; null when nothing waits.</summary>
    public Action? Release { get; init; }

    public async ValueTask DisposeAsync()
    {
        Release?.Invoke();
        foreach (var c in new[] { Client, Server })
        {
            try
            {
                await c.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or WebSocketException or QuicException or OperationCanceledException)
            {
                // The peer is already gone; releasing is best effort.
            }
        }
    }
}

/// <summary>One transport under test: a real listener on loopback and the real client module that connects to it.</summary>
internal abstract class TransportScenario : IAsyncDisposable
{
    /// <summary>Row name, such as <c>ws-h2</c>.</summary>
    public abstract string Name { get; }

    /// <summary>How <see cref="ConnectionPair.DropAsync"/> drops the connection, for the notes.</summary>
    public abstract string DropMethod { get; }

    /// <summary>The HTTP protocol the transport must negotiate, or null for non-HTTP transports; a mismatch fails the run so a row is never mislabelled.</summary>
    public virtual string? ExpectedProtocol => null;

    /// <summary>The protocol the server saw on the last connect, or null.</summary>
    public virtual string? ObservedProtocol => null;

    /// <summary>Why this transport cannot run here, or null when it can.</summary>
    public virtual string? UnsupportedReason => null;

    public abstract Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Connects; returns when the client module returns, then waits for the server to hand over its end.</summary>
    public abstract Task<ConnectionPair> ConnectAsync(CancellationToken cancellationToken);

    public abstract ValueTask DisposeAsync();

    public static IReadOnlyList<string> Names { get; } = ["ws-h1", "ws-h2", "ws-h3", "quic", "grpc-h2", "grpc-h3"];

    public static TransportScenario Create(string name) => name switch
    {
        "ws-h1" => new WebSocketScenario(name, HttpProtocols.Http1, "HTTP/1.1"),
        "ws-h2" => new WebSocketScenario(name, HttpProtocols.Http2, "HTTP/2"),
        "ws-h3" => new WebSocketScenario(name, HttpProtocols.Http3, "HTTP/3"),
        "quic" => new QuicScenario(),
        "grpc-h2" => new GrpcScenario(name, HttpProtocols.Http2, "HTTP/2"),
        "grpc-h3" => new GrpcScenario(name, HttpProtocols.Http3, "HTTP/3"),
        _ => throw new ArgumentException("unknown transport " + name),
    };
}

/// <summary>A throwaway self-signed loopback certificate; the key never leaves the process.</summary>
internal static class BenchCertificate
{
    public static X509Certificate2 Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        // Windows Schannel cannot serve with an ephemeral key, so round-trip it into the user key store.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, string.Empty), string.Empty, X509KeyStorageFlags.UserKeySet);
    }

    public static RemoteCertificateValidationCallback Pin(X509Certificate2 cert)
    {
        var hash = cert.GetCertHash(HashAlgorithmName.SHA256);
        return (_, presented, _, _) => presented is not null && CryptographicOperations.FixedTimeEquals(
            (presented as X509Certificate2 ?? new X509Certificate2(presented)).GetCertHash(HashAlgorithmName.SHA256), hash);
    }
}

/// <summary>An <see cref="IWireConnection"/> over one <see cref="WebSocket"/>, text messages like the shipped ws client.</summary>
internal sealed class WsConnection(WebSocket socket, IDisposable? owner) : IWireConnection
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private byte[] _buffer = new byte[16 * 1024];

    public WireProfile Profile => new(WireNames.WebSocket, WireNames.Json);

    public WebSocket Socket => socket;

    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(frame, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async ValueTask<WireReceive> ReceiveAsync(CancellationToken cancellationToken)
    {
        var used = 0;
        try
        {
            while (true)
            {
                if (used == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);
                var result = await socket.ReceiveAsync(_buffer.AsMemory(used), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return WireReceive.Closed(socket.CloseStatus is { } s ? (int)s : 0);
                used += result.Count;
                if (result.EndOfMessage) return WireReceive.Data(_buffer.AsSpan(0, used).ToArray());
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            return WireReceive.Closed(0);
        }
    }

    public ValueTask DisposeAsync()
    {
        socket.Abort();
        socket.Dispose();
        owner?.Dispose();
        _sendGate.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Shared Kestrel plumbing: one loopback HTTPS endpoint on one HTTP version, a channel of accepted server ends.</summary>
internal abstract class KestrelScenario(string name, HttpProtocols protocols, string expectedProtocol) : TransportScenario
{
    protected sealed record Accepted(IWireConnection Server, TaskCompletionSource Done);

    private WebApplication? _app;
    private volatile string? _observed;
    private volatile HttpContext? _lastContext;

    protected X509Certificate2 Certificate { get; } = BenchCertificate.Create();

    protected Channel<Accepted> AcceptedChannel { get; } = Channel.CreateUnbounded<Accepted>();

    protected Uri BaseAddress { get; private set; } = new("https://127.0.0.1");

    public override string Name => name;

    public override string? ExpectedProtocol => expectedProtocol;

    public override string? ObservedProtocol => _observed;

    public override string DropMethod => "HttpContext.Abort() on the server request (aborts the underlying connection)";

    public override string? UnsupportedReason =>
        protocols == HttpProtocols.Http3 && !(QuicListener.IsSupported && QuicConnection.IsSupported) ? "System.Net.Quic is not available here (MsQuic missing)" : null;

    protected abstract void ConfigureServices(IServiceCollection services);

    protected abstract void MapEndpoints(WebApplication app);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);
        if (protocols == HttpProtocols.Http3) builder.WebHost.UseQuic();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
        {
            listen.Protocols = protocols;
            listen.UseHttps(Certificate);
        }));
        ConfigureServices(builder.Services);
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            _observed = context.Request.Protocol;
            _lastContext = context;
            await next().ConfigureAwait(false);
        });
        MapEndpoints(app);
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _app = app;
        BaseAddress = new Uri(app.Urls.First());
    }

    protected ValueTask AbortLastRequestAsync()
    {
        _lastContext?.Abort();
        return ValueTask.CompletedTask;
    }

    protected async Task<ConnectionPair> PairAsync(IWireConnection client, CancellationToken cancellationToken)
    {
        var accepted = await AcceptedChannel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new ConnectionPair { Client = client, Server = accepted.Server, DropAsync = AbortLastRequestAsync, Release = () => accepted.Done.TrySetResult() };
    }

    public override async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }

        Certificate.Dispose();
    }
}

/// <summary>JSON over WebSocket on Kestrel and <see cref="ClientWebSocket"/>, over HTTP/1.1, HTTP/2 (RFC 8441) or HTTP/3 (RFC 9220), TLS on every row.</summary>
internal sealed class WebSocketScenario(string name, HttpProtocols protocols, string expectedProtocol) : KestrelScenario(name, protocols, expectedProtocol)
{
    private readonly Version _version = protocols switch { HttpProtocols.Http1 => HttpVersion.Version11, HttpProtocols.Http2 => HttpVersion.Version20, _ => HttpVersion.Version30 };

    protected override void ConfigureServices(IServiceCollection services)
    {
    }

    protected override void MapEndpoints(WebApplication app)
    {
        app.UseWebSockets();
        app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await AcceptedChannel.Writer.WriteAsync(new Accepted(new WsConnection(socket, null), done)).ConfigureAwait(false);
            try
            {
                await done.Task.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The client or a drop ended the request.
            }
        });
    }

    public override async Task<ConnectionPair> ConnectAsync(CancellationToken cancellationToken)
    {
        // A new handler per connection, so every connect is a new TCP or QUIC connection and a new TLS handshake.
        var handler = new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = BenchCertificate.Pin(Certificate) } };
        var invoker = new HttpMessageInvoker(handler, disposeHandler: true);
        var socket = new ClientWebSocket();
        socket.Options.HttpVersion = _version;
        socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        try
        {
            await socket.ConnectAsync(new Uri($"wss://127.0.0.1:{BaseAddress.Port}/ws"), invoker, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            invoker.Dispose();
            throw;
        }

        return await PairAsync(new WsConnection(socket, invoker), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The shipped gRPC transport modules (<c>bazaar.wire.v1.Session/Connect</c>), server on Kestrel, client <see cref="GrpcWireConnector"/>, over HTTP/2 or HTTP/3.</summary>
internal sealed class GrpcScenario(string name, HttpProtocols protocols, string expectedProtocol) : KestrelScenario(name, protocols, expectedProtocol)
{
    private sealed class AllowAll : IWireTransportAuthenticator
    {
        public ValueTask<WireAuthResult> AuthenticateAsync(string? credential, string transport, string? deviceId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(WireAuthResult.Success("principal:bench", deviceId));
    }

    private sealed class Handoff(Channel<Accepted> channel) : IWireConnectionHandoff
    {
        public async ValueTask HandleAsync(IWireConnection connection, WireAuthResult authentication, CancellationToken cancellationToken)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await channel.Writer.WriteAsync(new Accepted(connection, done), cancellationToken).ConfigureAwait(false);
            try
            {
                await done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The call ended.
            }
        }
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IWireTransportAuthenticator>(new AllowAll());
        services.AddSingleton<IWireConnectionHandoff>(new Handoff(AcceptedChannel));
        services.AddBazaarGrpcTransport(o => o.EnabledCodecs = ["json"]);
    }

    protected override void MapEndpoints(WebApplication app)
    {
        app.UseBazaarGrpcTransport();
        app.MapBazaarGrpcTransport();
    }

    public override async Task<ConnectionPair> ConnectAsync(CancellationToken cancellationToken)
    {
        var connector = new GrpcWireConnector(new GrpcConnectorOptions
        {
            CredentialProvider = _ => ValueTask.FromResult<string?>("bench-credential"),
            PreferHttp3 = ExpectedProtocol == "HTTP/3",
            HttpHandlerFactory = () => new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                SslOptions = { RemoteCertificateValidationCallback = BenchCertificate.Pin(Certificate) },
            },
        });
        var offer = new TransportOffer(WireNames.Grpc, $"https://127.0.0.1:{BaseAddress.Port}", null, ["json"]);
        var client = await connector.ConnectAsync(offer, ["json"], cancellationToken).ConfigureAwait(false);
        return await PairAsync(client, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The shipped native QUIC modules: <see cref="QuicWireListener"/> and <see cref="QuicWireConnector"/>, ALPN <c>bazaar-wire/1</c>, certificate pinned.</summary>
internal sealed class QuicScenario : TransportScenario
{
    private readonly X509Certificate2 _certificate = BenchCertificate.Create();
    private QuicWireListener? _listener;
    private TransportOffer? _offer;

    public override string Name => "quic";

    public override string DropMethod => "StreamWireConnection.CloseAsync(4503) on the server end (QUIC CONNECTION_CLOSE with an application error code)";

    public override string? UnsupportedReason => QuicListener.IsSupported && QuicConnection.IsSupported ? null : "System.Net.Quic is not available here (MsQuic missing)";

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new QuicWireListener(new QuicWireListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            Certificate = _certificate,
            Codecs = ["json"],
        });
        await _listener.StartAsync(cancellationToken).ConfigureAwait(false);
        _offer = _listener.Offers[0] with { Endpoint = $"127.0.0.1:{_listener.LocalEndPoint!.Port}" };
    }

    public override async Task<ConnectionPair> ConnectAsync(CancellationToken cancellationToken)
    {
        var connector = new QuicWireConnector(new QuicWireConnectorOptions { PinCertificate = QuicCertificatePin.Sha256(_certificate.GetCertHash(HashAlgorithmName.SHA256)) });
        var client = await connector.ConnectAsync(_offer!, ["json"], cancellationToken).ConfigureAwait(false);
        var server = await _listener!.Accepted.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new ConnectionPair { Client = client, Server = server, DropAsync = () => ((StreamWireConnection)server).CloseAsync(4503) };
    }

    public override async ValueTask DisposeAsync()
    {
        if (_listener is not null) await _listener.DisposeAsync().ConfigureAwait(false);
        _certificate.Dispose();
    }
}
