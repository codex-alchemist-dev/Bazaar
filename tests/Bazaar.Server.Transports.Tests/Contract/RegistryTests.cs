using Bazaar.Server.Transports;
using Bazaar.Server.Transports.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.Transports.Tests.Contract;

public sealed class RegistryTests
{
    private static TransportOffer Wss(string id, string host, TransportScope scope = TransportScope.Lan) =>
        new(id, scope, new Uri($"wss://{host}:7443/ws"), Contexts.KeyHash, null, false, true);

    [Fact]
    public async Task Disabled_modules_are_not_started()
    {
        var module = new FakeModule("off", 10, defaultEnabled: false);
        await using var registry = new TransportRegistry([module], NullLogger.Instance);

        var status = await registry.StartAsync(Contexts.Create(), CancellationToken.None);

        Assert.False(module.Started);
        Assert.Equal(TransportModuleState.Disabled, status.Single().State);
    }

    [Fact]
    public async Task Default_enabled_module_starts_and_offers_in_priority_order()
    {
        var late = new FakeModule("late", 50, defaultEnabled: true, offer: Wss("late", "203.0.113.5", TransportScope.Internet));
        var early = new FakeModule("early", 10, defaultEnabled: true, offer: Wss("early", "192.168.1.20"));
        await using var registry = new TransportRegistry([late, early], NullLogger.Instance);

        await registry.StartAsync(Contexts.Create(), CancellationToken.None);

        var offers = registry.CurrentOffers();
        Assert.Equal(["early", "late"], offers.Select(o => o.ModuleId));
        Assert.Equal(["early", "late"], registry.Modules.Select(m => m.Id));
    }

    [Fact]
    public async Task Settings_can_enable_a_module_that_is_off_by_default()
    {
        var module = new FakeModule("cloudflared", 30, defaultEnabled: false, offer: Wss("cloudflared", "x.example.test", TransportScope.Internet));
        await using var registry = new TransportRegistry([module], NullLogger.Instance);
        var settings = new Dictionary<string, object> { ["transport.cloudflared.enabled"] = true };

        await registry.StartAsync(Contexts.Create(settings: settings), CancellationToken.None);

        Assert.True(module.Started);
        Assert.Single(registry.CurrentOffers());
    }

    [Fact]
    public async Task A_failing_module_does_not_stop_the_others()
    {
        var broken = new FakeModule("broken", 10, defaultEnabled: true, failStart: true);
        var fine = new FakeModule("fine", 20, defaultEnabled: true, offer: Wss("fine", "192.168.1.21"));
        await using var registry = new TransportRegistry([broken, fine], NullLogger.Instance);

        var status = await registry.StartAsync(Contexts.Create(), CancellationToken.None);

        Assert.Equal(TransportModuleState.Failed, status.Single(s => s.ModuleId == "broken").State);
        Assert.Equal(TransportModuleState.Running, status.Single(s => s.ModuleId == "fine").State);
        Assert.Single(registry.CurrentOffers());
    }

    [Fact]
    public async Task Plain_ws_offer_off_loopback_is_dropped()
    {
        var bad = new TransportOffer("bad", TransportScope.Lan, new Uri("ws://192.168.1.20:7443/ws"), Contexts.KeyHash, null, false, false);
        var module = new FakeModule("bad", 10, defaultEnabled: true, offer: bad);
        await using var registry = new TransportRegistry([module], NullLogger.Instance);

        await registry.StartAsync(Contexts.Create(), CancellationToken.None);

        Assert.Empty(registry.CurrentOffers());
    }

    [Fact]
    public async Task Stop_stops_every_started_module_and_clears_offers()
    {
        var a = new FakeModule("a", 10, defaultEnabled: true, offer: Wss("a", "192.168.1.30"));
        var b = new FakeModule("b", 20, defaultEnabled: true, offer: Wss("b", "192.168.1.31"));
        var registry = new TransportRegistry([a, b], NullLogger.Instance);
        await registry.StartAsync(Contexts.Create(), CancellationToken.None);

        await registry.StopAsync(CancellationToken.None);

        Assert.True(a.Stopped);
        Assert.True(b.Stopped);
        Assert.Empty(registry.CurrentOffers());
    }

    [Fact]
    public void Duplicate_module_ids_are_rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new TransportRegistry([new FakeModule("same", 1), new FakeModule("same", 2)], NullLogger.Instance));
        Assert.Contains("same", ex.Message, StringComparison.Ordinal);
    }
}
