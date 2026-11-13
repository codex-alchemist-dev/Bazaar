using Bazaar.Conduit;
using Bazaar.Conduit.Tests.Support;

namespace Bazaar.Conduit.Tests.Overlay;

public sealed class WireGuardModuleTests
{
    private static Dictionary<string, object> Settings(string? address, int? port = null)
    {
        var values = new Dictionary<string, object>();
        if (address is not null)
        {
            values[WireGuardModule.AddressKey] = address;
        }

        if (port is not null)
        {
            values[WireGuardModule.PortKey] = port.Value;
        }

        return values;
    }

    [Fact]
    public void Module_metadata_matches_the_catalogue()
    {
        var module = new WireGuardModule();

        Assert.Equal("overlay-wireguard", module.Id);
        Assert.Equal("WireGuard overlay", module.DisplayName);
        Assert.Equal(ConduitScope.Overlay, module.Scope);
        Assert.Equal(41, module.Priority);
        Assert.False(module.DefaultEnabled);
        Assert.Equal(ConduitModuleState.Disabled, module.Status.State);
    }

    [Fact]
    public async Task Ipv4_address_publishes_one_wss_offer()
    {
        var module = new WireGuardModule();

        await module.StartAsync(Contexts.Create(settings: Settings("10.8.0.2"), port: 51820), CancellationToken.None);

        Assert.Equal(ConduitModuleState.Running, module.Status.State);
        var offer = Assert.Single(module.Status.Offers);
        Assert.Equal(new Uri("wss://10.8.0.2:51820/ws"), offer.Uri);
        Assert.Equal(ConduitScope.Overlay, offer.Scope);
        Assert.Equal("overlay-wireguard", offer.ModuleId);
        Assert.Equal(Contexts.KeyHash, offer.ServerKeyHash);
        Assert.Null(ConduitOfferRules.Validate(offer));
    }

    [Fact]
    public async Task Ipv6_literal_is_accepted_and_bracketed()
    {
        var module = new WireGuardModule();

        await module.StartAsync(Contexts.Create(settings: Settings("fd00:b102::2"), port: 7443), CancellationToken.None);

        Assert.Equal(ConduitModuleState.Running, module.Status.State);
        var offer = Assert.Single(module.Status.Offers);
        Assert.Equal("wss", offer.Uri.Scheme);
        Assert.Equal(new Uri("wss://[fd00:b102::2]:7443/ws"), offer.Uri);
        Assert.Null(ConduitOfferRules.Validate(offer));
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("example.test")]
    [InlineData("10.8.0.2:51820")]
    [InlineData("1.2.3")]
    [InlineData("[fd00::2]")]
    [InlineData("fe80::1%eth0")]
    public async Task Invalid_address_fails_with_invalid_address(string address)
    {
        var module = new WireGuardModule();

        await module.StartAsync(Contexts.Create(settings: Settings(address)), CancellationToken.None);

        Assert.Equal(ConduitModuleState.Failed, module.Status.State);
        Assert.Equal("invalid-address", module.Status.Reason);
        Assert.Empty(module.Status.Offers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_address_fails_with_not_configured(string address)
    {
        var module = new WireGuardModule();

        await module.StartAsync(Contexts.Create(settings: Settings(address)), CancellationToken.None);

        Assert.Equal(ConduitModuleState.Failed, module.Status.State);
        Assert.Equal("not-configured", module.Status.Reason);
    }

    [Fact]
    public async Task Missing_address_setting_fails_with_not_configured()
    {
        var module = new WireGuardModule();

        await module.StartAsync(Contexts.Create(settings: Settings(address: null)), CancellationToken.None);

        Assert.Equal("not-configured", module.Status.Reason);
    }

    [Fact]
    public async Task Stop_withdraws_the_offer()
    {
        var module = new WireGuardModule();
        await module.StartAsync(Contexts.Create(settings: Settings("10.8.0.2")), CancellationToken.None);
        Assert.Single(module.Status.Offers);

        await module.StopAsync(CancellationToken.None);

        Assert.Equal(ConduitModuleState.Disabled, module.Status.State);
        Assert.Empty(module.Status.Offers);
    }
}
