using System.Net;
using System.Text.Json;
using Bazaar.Core;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Settings;
using Bazaar.Server.Wire.Discovery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.Host.Tests;

public sealed class WireDiscoveryHostTests
{
    private const string _route = "/.well-known/bazaar/wire";

    private sealed class FakeReader(bool enabled) : IWireDiscoverySettingsReader
    {
        public bool Enabled { get; set; } = enabled;

        public Task<WireDiscoverySettingValues> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(new WireDiscoverySettingValues(Enabled, 300, 60));
    }

    /// <summary>The host's own composition, as Program.cs wires it: component list entry, host pieces, and the conditional map.</summary>
    private static async Task<WebApplication> StartAsync(FakeReader reader, Dictionary<string, string?>? config = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(config ?? []);
        builder.Services.AddSingleton<IWireDiscoverySettingsReader>(reader);
        builder.Services.AddBazaarHostWireDiscovery();
        builder.Services.AddBazaarComponents(builder.Configuration, [HostComponents.All.Single(c => c.Manifest.Id == WireDiscoveryComponent.Id)]);
        builder.Services.Configure<WireDiscoveryOptions>(o => o.SettingsRefreshInterval = TimeSpan.FromMilliseconds(50));
        var app = builder.Build();
        if (BazaarComponentRegistration.IsEnabled(app.Configuration, WireDiscoveryComponent.Id))
        {
            app.MapBazaarWireDiscovery();
        }

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static async Task<HttpStatusCode> GetAsync(WebApplication app, string path) =>
        (await app.GetTestClient().GetAsync(path, TestContext.Current.CancellationToken)).StatusCode;

    [Fact]
    public async Task Route_answers_with_the_floor_transport_and_codec_when_the_setting_is_on()
    {
        await using var app = await StartAsync(new FakeReader(true));
        var response = await app.GetTestClient().GetAsync(_route, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["ws"], doc.RootElement.GetProperty("transports").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["json"], doc.RootElement.GetProperty("codecs").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(HttpStatusCode.OK, await GetAsync(app, _route + "/schema/wire.cddl"));
    }

    [Fact]
    public async Task Route_answers_404_when_the_setting_is_off_and_follows_a_live_change()
    {
        var reader = new FakeReader(false);
        await using var app = await StartAsync(reader);
        Assert.Equal(HttpStatusCode.NotFound, await GetAsync(app, _route));
        Assert.Equal(HttpStatusCode.NotFound, await GetAsync(app, _route + "/schema/wire.cddl"));

        reader.Enabled = true;
        await WaitForAsync(async () => await GetAsync(app, _route) == HttpStatusCode.OK);
        reader.Enabled = false;
        await WaitForAsync(async () => await GetAsync(app, _route) == HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Disabled_component_maps_no_route()
    {
        await using var app = await StartAsync(new FakeReader(true), new() { ["Bazaar:Components:wire-discovery:Enabled"] = "false" });
        Assert.Equal(HttpStatusCode.NotFound, await GetAsync(app, _route));
    }

    [Fact]
    public void Host_registry_knows_the_discovery_keys_and_lists_the_component()
    {
        Assert.Contains(HostComponents.All, c => c.Manifest.Id == WireDiscoveryComponent.Id);
        var registry = SettingRegistry.WithContributions([new WireDiscoverySettingContribution()]);
        Assert.Contains(registry.Definitions, d => d.Name == "wire.discovery.enabled");
        Assert.Contains(registry.Definitions, d => d.Name == "wire.discovery.rate.permit_limit");
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met in time.");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }
}
