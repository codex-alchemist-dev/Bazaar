using System.Collections.Concurrent;
using System.Text;
using Bazaar.Client.Transport.Grpc;
using Bazaar.Server.Transport.Grpc;
using Bazaar.Wire.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Transport.Grpc.Tests;

/// <summary>Accepts the fake credential <see cref="Good"/> only; records every call.</summary>
internal sealed class FakeAuthenticator : IWireTransportAuthenticator
{
    public const string Good = "fake-token-for-tests";

    public ConcurrentQueue<(string? Credential, string Transport)> Calls { get; } = new();

    public ValueTask<WireAuthResult> AuthenticateAsync(string? credential, string transport, string? deviceId, CancellationToken cancellationToken)
    {
        Calls.Enqueue((credential, transport));
        return ValueTask.FromResult(credential == Good ? WireAuthResult.Success("principal:test", deviceId) : WireAuthResult.Fail("bad credential"));
    }
}

/// <summary>Echoes every frame back. The frame "close" makes it close the connection with 4403.</summary>
internal sealed class EchoHandoff : IWireConnectionHandoff
{
    public ConcurrentQueue<WireProfile> Profiles { get; } = new();

    public ConcurrentQueue<byte[]> Received { get; } = new();

    public ConcurrentQueue<Exception> Failures { get; } = new();

    public async ValueTask HandleAsync(IWireConnection connection, WireAuthResult authentication, CancellationToken cancellationToken)
    {
        Profiles.Enqueue(connection.Profile);
        try
        {
            while (true)
            {
                var received = await connection.ReceiveAsync(cancellationToken);
                if (received.IsClosed) return;
                var bytes = received.Frame.ToArray();
                Received.Enqueue(bytes);
                if (Encoding.ASCII.GetString(bytes) == "close")
                {
                    await ((ICloseableWireConnection)connection).CloseAsync(4403, cancellationToken);
                    return;
                }

                await connection.SendAsync(bytes, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failures.Enqueue(ex);
            throw;
        }
    }
}

/// <summary>An in-process server (TestServer) with the gRPC transport mapped.</summary>
internal sealed class TestHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestHost(WebApplication app, FakeAuthenticator auth, EchoHandoff handoff)
    {
        _app = app;
        Auth = auth;
        Handoff = handoff;
    }

    public FakeAuthenticator Auth { get; }

    public EchoHandoff Handoff { get; }

    public TestServer Server => _app.GetTestServer();

    public static async Task<TestHost> StartAsync(Action<GrpcTransportOptions>? configure = null, Action<WireTransportsEnabledOptions>? enabled = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var auth = new FakeAuthenticator();
        var handoff = new EchoHandoff();
        builder.Services.AddSingleton<IWireTransportAuthenticator>(auth);
        builder.Services.AddSingleton<IWireConnectionHandoff>(handoff);
        builder.Services.AddBazaarGrpcTransport(o =>
        {
            o.EnabledCodecs = ["json", "cbor"];
            configure?.Invoke(o);
        });
        if (enabled is not null) builder.Services.Configure(enabled);
        var app = builder.Build();
        app.UseBazaarGrpcTransport();
        app.MapBazaarGrpcTransport();
        await app.StartAsync();
        return new TestHost(app, auth, handoff);
    }

    public GrpcWireConnector Connector(string? credential = FakeAuthenticator.Good, int maxFrameBytes = 256 * 1024) =>
        new(new GrpcConnectorOptions
        {
            CredentialProvider = _ => ValueTask.FromResult(credential),
            HttpHandlerFactory = () => Server.CreateHandler(),
            MaxFrameBytes = maxFrameBytes,
        });

    public static TransportOffer Offer { get; } = new(WireNames.Grpc, "http://localhost", "h2", ["json", "cbor"]);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
