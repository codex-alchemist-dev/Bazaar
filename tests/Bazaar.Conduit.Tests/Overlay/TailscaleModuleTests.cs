using System.Net;
using Bazaar.Conduit;
using Bazaar.Conduit.Tests.Support;

namespace Bazaar.Conduit.Tests.Overlay;

public sealed class TailscaleModuleTests
{
    private static readonly IPAddress _overlay = IPAddress.Parse("100.64.0.5");

    [Fact]
    public void Module_metadata_matches_the_catalogue()
    {
        var module = new TailscaleModule(new FakeOverlayAddressSource());

        Assert.Equal("overlay-tailscale", module.Id);
        Assert.Equal("Tailscale overlay", module.DisplayName);
        Assert.Equal(ConduitScope.Overlay, module.Scope);
        Assert.Equal(40, module.Priority);
        Assert.False(module.DefaultEnabled);
        Assert.Equal(ConduitModuleState.Disabled, module.Status.State);
    }

    [Fact]
    public async Task Address_publishes_one_wss_offer_on_the_local_port()
    {
        var module = new TailscaleModule(new FakeOverlayAddressSource(_overlay));

        await module.StartAsync(Contexts.Create(port: 7443), CancellationToken.None);

        Assert.Equal(ConduitModuleState.Running, module.Status.State);
        Assert.Null(module.Status.Reason);
        var offer = Assert.Single(module.Status.Offers);
        Assert.Equal(new Uri("wss://100.64.0.5:7443/ws"), offer.Uri);
        Assert.Equal("overlay-tailscale", offer.ModuleId);
        Assert.Equal(ConduitScope.Overlay, offer.Scope);
        Assert.False(offer.RequiresRelay);
        Assert.False(offer.Verified);
        Assert.False(offer.TerminatesTls);
        Assert.Null(offer.ExpiresAt);
        Assert.Equal(Contexts.KeyHash, offer.ServerKeyHash);
        Assert.Null(ConduitOfferRules.Validate(offer));
    }

    [Fact]
    public async Task Configured_port_overrides_the_local_port()
    {
        var settings = new Dictionary<string, object> { [TailscaleModule.PortKey] = 8443 };
        var module = new TailscaleModule(new FakeOverlayAddressSource(_overlay));

        await module.StartAsync(Contexts.Create(port: 7443, settings: settings), CancellationToken.None);

        var offer = Assert.Single(module.Status.Offers);
        Assert.Equal(new Uri("wss://100.64.0.5:8443/ws"), offer.Uri);
    }

    [Fact]
    public async Task No_address_fails_with_overlay_not_running()
    {
        var module = new TailscaleModule(new FakeOverlayAddressSource(address: null));

        await module.StartAsync(Contexts.Create(), CancellationToken.None);

        Assert.Equal(ConduitModuleState.Failed, module.Status.State);
        Assert.Equal("overlay-not-running", module.Status.Reason);
        Assert.Empty(module.Status.Offers);
    }

    [Fact]
    public async Task Source_exception_fails_with_its_type_name()
    {
        var module = new TailscaleModule(new FakeOverlayAddressSource(error: new InvalidOperationException("secret path")));

        await module.StartAsync(Contexts.Create(), CancellationToken.None);

        Assert.Equal(ConduitModuleState.Failed, module.Status.State);
        Assert.Equal(nameof(InvalidOperationException), module.Status.Reason);
        Assert.Empty(module.Status.Offers);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public async Task Out_of_range_port_fails_before_asking_for_an_address(int port)
    {
        var source = new FakeOverlayAddressSource(_overlay);
        var settings = new Dictionary<string, object> { [TailscaleModule.PortKey] = port };
        var module = new TailscaleModule(source);

        await module.StartAsync(Contexts.Create(settings: settings), CancellationToken.None);

        Assert.Equal("invalid-port", module.Status.Reason);
        Assert.Equal(ConduitModuleState.Failed, module.Status.State);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task Stop_withdraws_the_offer()
    {
        var module = new TailscaleModule(new FakeOverlayAddressSource(_overlay));
        await module.StartAsync(Contexts.Create(), CancellationToken.None);
        Assert.Single(module.Status.Offers);

        await module.StopAsync(CancellationToken.None);

        Assert.Equal(ConduitModuleState.Disabled, module.Status.State);
        Assert.Empty(module.Status.Offers);
    }

    [Fact]
    public async Task Status_changed_is_raised_when_the_offer_is_published()
    {
        var module = new TailscaleModule(new FakeOverlayAddressSource(_overlay));
        ModuleStatus? last = null;
        module.StatusChanged += (_, status) => last = status;

        await module.StartAsync(Contexts.Create(), CancellationToken.None);

        Assert.NotNull(last);
        Assert.Equal(ConduitModuleState.Running, last.State);
    }
}
