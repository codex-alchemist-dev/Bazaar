using System.Net;
using Bazaar.Wire.Abstractions;
using Bazaar.Wire.Schema;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.Wire.Discovery.Tests;

/// <summary>An in-process server with the discovery routes mapped.</summary>
internal sealed class DiscoveryHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private DiscoveryHost(WebApplication app, HttpClient client)
    {
        _app = app;
        Client = client;
    }

    public HttpClient Client { get; }

    public static async Task<DiscoveryHost> StartAsync(
        Action<WireDiscoveryOptions>? configure = null,
        Action<IServiceCollection>? services = null,
        TimeProvider? time = null,
        string remoteAddress = "203.0.113.7")
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IWireSchemaSource, EmbeddedWireSchemaSource>();
        if (time is not null)
        {
            builder.Services.AddSingleton(time);
        }

        builder.Services.AddBazaarWireDiscovery(configure);
        services?.Invoke(builder.Services);
        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers.TryGetValue("X-Test-Client", out var v) ? v.ToString() : remoteAddress);
            return next(context);
        });
        app.UseRouting();
        app.MapBazaarWireDiscovery();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new DiscoveryHost(app, app.GetTestClient());
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }
}

internal sealed class FakeContribution(string[] transports, string[] codecs) : IWireDiscoveryContribution
{
    public IReadOnlyList<string> Transports { get; set; } = transports;

    public IReadOnlyList<string> Codecs { get; set; } = codecs;
}

internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
