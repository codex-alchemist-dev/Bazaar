using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Wire.Connection;
using Bazaar.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Tests.Wire.Harness;

/// <summary>
/// Protocol harness (doc 34 WP5): the real wire module on in-process Kestrel, bound to loopback on a free port,
/// with a fake clock and a fake authenticator. No database, no models, no display.
/// </summary>
public sealed class WireTestServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private WireTestServer(WebApplication app, Uri socketUri, FakeClock clock, FakeAuthenticator auth)
    {
        _app = app;
        SocketUri = socketUri;
        Clock = clock;
        Auth = auth;
    }

    /// <summary>The <c>ws://</c> address of the socket endpoint.</summary>
    public Uri SocketUri { get; }

    /// <summary>The clock the wire module runs on.</summary>
    public FakeClock Clock { get; }

    /// <summary>The fake authenticator.</summary>
    public FakeAuthenticator Auth { get; }

    /// <summary>The wire connection manager.</summary>
    public WireConnectionManager Manager => _app.Services.GetRequiredService<WireConnectionManager>();

    /// <summary>The idempotent request layer.</summary>
    public IdempotentRequestExecutor Idempotency => _app.Services.GetRequiredService<IdempotentRequestExecutor>();

    /// <summary>Starts a server. <paramref name="configure"/> may tighten wire options; <paramref name="handlers"/> are added to the registry.</summary>
    public static async Task<WireTestServer> StartAsync(
        Action<WireOptions>? configure = null,
        FakeAuthenticator? auth = null,
        IEnumerable<IWireRequestHandler>? handlers = null,
        FakeClock? clock = null)
    {
        clock ??= new FakeClock();
        auth ??= new FakeAuthenticator().Allow("good");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton<IConnectionAuthenticator>(auth);
        builder.Services.AddBazaarWire(new ConfigurationBuilder().Build());
        if (configure is not null) builder.Services.Configure(configure);
        foreach (var h in handlers ?? []) builder.Services.AddSingleton<IWireRequestHandler>(h);
        var app = builder.Build();
        app.UseBazaarWire();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var path = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<WireOptions>>().Value.Path;
        return new WireTestServer(app, new Uri(address.Replace("http://", "ws://", StringComparison.Ordinal) + path), clock, auth);
    }

    /// <summary>Advances the fake clock in one-second steps until <paramref name="done"/> is true, yielding to the server between steps.</summary>
    public async Task AdvanceUntilAsync(Func<bool> done, TimeSpan max)
    {
        var elapsed = TimeSpan.Zero;
        while (!done() && elapsed < max)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            elapsed += TimeSpan.FromSeconds(1);
            await Task.Delay(5);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
