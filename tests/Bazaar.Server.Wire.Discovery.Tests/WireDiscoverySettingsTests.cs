using System.Net;
using System.Text.Json;
using Bazaar.Server.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Wire.Discovery.Tests;

public class WireDiscoverySettingsTests
{
    private const string Route = "/.well-known/bazaar/wire";
    private static readonly SettingActor _operator = new("op", SettingRole.Operator);

    private sealed class FakeReader(WireDiscoverySettingValues values) : IWireDiscoverySettingsReader
    {
        public WireDiscoverySettingValues Values { get; set; } = values;

        public bool Fail { get; set; }

        public Task<WireDiscoverySettingValues> ReadAsync(CancellationToken cancellationToken) =>
            Fail ? throw new InvalidOperationException("store down") : Task.FromResult(Values);
    }

    [Fact]
    public void Keys_have_the_documented_names_defaults_and_scope()
    {
        Assert.Equal("wire.discovery.enabled", WireDiscoverySettings.Enabled.Name);
        Assert.True(WireDiscoverySettings.Enabled.Default);
        Assert.Equal(new WireDiscoveryOptions().PermitLimit, WireDiscoverySettings.PermitLimit.Default);
        Assert.Equal(60, WireDiscoverySettings.WindowSeconds.Default);
        Assert.All(WireDiscoverySettings.All, k =>
        {
            Assert.Equal(SettingScope.Install, k.Scope);
            Assert.Equal(SettingRole.Operator, k.WhoMayChange);
            Assert.Equal(ApprovalTier.None, k.ApprovalTier);
        });
        Assert.Equal(WireDiscoverySettings.All.Count, new WireDiscoverySettingContribution().Settings.Select(s => s.Name).Distinct().Count());
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("-5", false)]
    [InlineData("1", true)]
    [InlineData("6000", true)]
    [InlineData("6001", false)]
    public void Rate_limit_cannot_be_switched_off_or_made_unbounded(string json, bool accepted)
    {
        using var doc = JsonDocument.Parse(json);
        var result = WireDiscoverySettings.PermitLimit.Evaluate(_operator, doc.RootElement);
        Assert.Equal(accepted ? SettingWriteOutcome.Accepted : SettingWriteOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public void Only_an_operator_may_change_the_keys_and_wrong_types_are_rejected()
    {
        using var on = JsonDocument.Parse("false");
        Assert.Equal(SettingWriteOutcome.Rejected, WireDiscoverySettings.Enabled.Evaluate(new SettingActor("a", SettingRole.Agent), on.RootElement).Outcome);
        Assert.Equal(SettingWriteOutcome.Accepted, WireDiscoverySettings.Enabled.Evaluate(_operator, on.RootElement).Outcome);
        using var text = JsonDocument.Parse("\"no\"");
        Assert.Equal(SettingWriteOutcome.Rejected, WireDiscoverySettings.Enabled.Evaluate(_operator, text.RootElement).Outcome);
    }

    [Fact]
    public async Task Live_setting_turns_the_route_off_and_on_without_a_restart()
    {
        var reader = new FakeReader(new WireDiscoverySettingValues(true, 300, 60));
        var live = new WireDiscoveryLiveSettings();
        await using var host = await DiscoveryHost.StartAsync(services: s => s.AddSingleton(live));
        var refresher = new WireDiscoverySettingsRefresher(
            reader, live, Options.Create(new WireDiscoveryOptions()), NullLogger<WireDiscoverySettingsRefresher>.Instance, TimeProvider.System);

        await refresher.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(Route, TestContext.Current.CancellationToken)).StatusCode);

        reader.Values = reader.Values with { Enabled = false };
        await refresher.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync(Route, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync(Route + "/schema/wire.cddl", TestContext.Current.CancellationToken)).StatusCode);

        reader.Values = reader.Values with { Enabled = true };
        await refresher.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(Route, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Live_rate_limit_applies_and_a_failed_read_keeps_the_last_values()
    {
        var reader = new FakeReader(new WireDiscoverySettingValues(true, 2, 60));
        var live = new WireDiscoveryLiveSettings();
        await using var host = await DiscoveryHost.StartAsync(services: s => s.AddSingleton(live));
        var refresher = new WireDiscoverySettingsRefresher(
            reader, live, Options.Create(new WireDiscoveryOptions()), NullLogger<WireDiscoverySettingsRefresher>.Instance, TimeProvider.System);
        await refresher.RefreshAsync(TestContext.Current.CancellationToken);

        reader.Fail = true;
        await refresher.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(Route, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(Route, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await host.Client.GetAsync(Route, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Before_the_first_reading_the_options_apply()
    {
        await using var host = await DiscoveryHost.StartAsync(services: s => s.AddSingleton(new WireDiscoveryLiveSettings()));
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(Route, TestContext.Current.CancellationToken)).StatusCode);
    }
}
