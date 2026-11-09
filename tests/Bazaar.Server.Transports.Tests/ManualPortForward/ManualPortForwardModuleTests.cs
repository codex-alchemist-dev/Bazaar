using Bazaar.Server.Transports;
using Bazaar.Server.Transports.Tests.Support;

namespace Bazaar.Server.Transports.Tests.ManualPortForward;

public sealed class ManualPortForwardModuleTests
{
    private const string _host = "203.0.113.10";
    private const string _probeUrl = "https://probe.example.test/check";

    private static Dictionary<string, object> Settings(string? host = _host, int? port = null, string? probe = null)
    {
        var values = new Dictionary<string, object>();
        if (host is not null)
        {
            values[ManualPortForwardModule.PublicHostKey] = host;
        }

        if (port is not null)
        {
            values[ManualPortForwardModule.PublicPortKey] = port.Value;
        }

        if (probe is not null)
        {
            values[ManualPortForwardModule.ProbeUrlKey] = probe;
        }

        return values;
    }

    [Fact]
    public void Module_metadata_matches_the_catalogue()
    {
        var module = new ManualPortForwardModule(new FakeOutsideProbe());

        Assert.Equal("manual-port-forward", module.Id);
        Assert.Equal("Manual port forward", module.DisplayName);
        Assert.Equal(TransportScope.Internet, module.Scope);
        Assert.Equal(20, module.Priority);
        Assert.False(module.DefaultEnabled);
        Assert.Equal(TransportModuleState.Disabled, module.Status.State);
    }

    [Fact]
    public async Task Unset_host_is_failed_not_configured()
    {
        var module = new ManualPortForwardModule(new FakeOutsideProbe());

        await module.StartAsync(Contexts.Create(settings: Settings(host: null)), CancellationToken.None);

        Assert.Equal(TransportModuleState.Failed, module.Status.State);
        Assert.Equal("not-configured", module.Status.Reason);
        Assert.Empty(module.Status.Offers);
    }

    [Theory]
    [InlineData("https://203.0.113.10")]
    [InlineData("203.0.113.10:7443")]
    [InlineData("example.test:7443")]
    [InlineData("example.test/path")]
    [InlineData("example.test?x=1")]
    [InlineData("exa mple.test")]
    [InlineData("user@example.test")]
    [InlineData("[2001:db8::1]")]
    public async Task Host_with_scheme_port_path_or_space_is_failed(string host)
    {
        var module = new ManualPortForwardModule(new FakeOutsideProbe());

        await module.StartAsync(Contexts.Create(settings: Settings(host: host)), CancellationToken.None);

        Assert.Equal(TransportModuleState.Failed, module.Status.State);
        Assert.Equal("invalid-public-host", module.Status.Reason);
        Assert.Empty(module.Status.Offers);
    }

    [Theory]
    [InlineData("203.0.113.10")]
    [InlineData("home.example.test")]
    [InlineData("2001:db8::1")]
    public async Task Valid_host_is_running(string host)
    {
        var module = new ManualPortForwardModule(new FakeOutsideProbe());

        await module.StartAsync(Contexts.Create(settings: Settings(host: host)), CancellationToken.None);

        Assert.Equal(TransportModuleState.Running, module.Status.State);
        Assert.Single(module.Status.Offers);
    }

    [Fact]
    public async Task Valid_host_without_probe_publishes_an_unverified_wss_offer_on_the_public_port()
    {
        var probe = new FakeOutsideProbe(result: true);
        var module = new ManualPortForwardModule(probe);

        await module.StartAsync(Contexts.Create(settings: Settings(port: 8443)), CancellationToken.None);

        Assert.Equal(TransportModuleState.Running, module.Status.State);
        Assert.Null(module.Status.Reason);
        Assert.Empty(probe.Calls);

        var offer = Assert.Single(module.Status.Offers);
        Assert.Equal("manual-port-forward", offer.ModuleId);
        Assert.Equal(TransportScope.Internet, offer.Scope);
        Assert.Equal("wss://203.0.113.10:8443/ws", offer.Uri.ToString());
        Assert.Equal(Contexts.KeyHash, offer.ServerKeyHash);
        Assert.Null(offer.ExpiresAt);
        Assert.False(offer.RequiresRelay);
        Assert.False(offer.TerminatesTls);
        Assert.False(offer.Verified);
    }

    [Fact]
    public async Task Public_port_defaults_to_the_local_port()
    {
        var module = new ManualPortForwardModule(new FakeOutsideProbe());

        await module.StartAsync(Contexts.Create(port: 9000, settings: Settings()), CancellationToken.None);

        Assert.Equal(9000, Assert.Single(module.Status.Offers).Uri.Port);
    }

    [Fact]
    public async Task Probe_returning_true_publishes_a_verified_offer()
    {
        var probe = new FakeOutsideProbe(result: true);
        var module = new ManualPortForwardModule(probe);

        await module.StartAsync(Contexts.Create(settings: Settings(probe: _probeUrl)), CancellationToken.None);

        Assert.Equal(TransportModuleState.Running, module.Status.State);
        Assert.Null(module.Status.Reason);
        Assert.True(Assert.Single(module.Status.Offers).Verified);

        var call = Assert.Single(probe.Calls);
        Assert.Equal(new Uri(_probeUrl), call.Probe);
        Assert.Equal(Contexts.KeyHash, call.KeyHash);
    }

    [Fact]
    public async Task Probe_returning_false_publishes_an_unverified_offer_and_stays_running()
    {
        var module = new ManualPortForwardModule(new FakeOutsideProbe(result: false));

        await module.StartAsync(Contexts.Create(settings: Settings(probe: _probeUrl)), CancellationToken.None);

        Assert.Equal(TransportModuleState.Running, module.Status.State);
        Assert.Equal("probe-failed", module.Status.Reason);
        Assert.False(Assert.Single(module.Status.Offers).Verified);
    }

    [Fact]
    public async Task Probe_that_throws_leaves_the_module_running()
    {
        var module = new ManualPortForwardModule(new FakeOutsideProbe(exception: new InvalidOperationException("probe broke")));

        await module.StartAsync(Contexts.Create(settings: Settings(probe: _probeUrl)), CancellationToken.None);

        Assert.Equal(TransportModuleState.Running, module.Status.State);
        Assert.Equal("probe-failed", module.Status.Reason);
        Assert.False(Assert.Single(module.Status.Offers).Verified);
    }

    [Theory]
    [InlineData("http://probe.example.test/check")]
    [InlineData("ftp://probe.example.test/check")]
    [InlineData("probe.example.test/check")]
    public async Task Non_https_probe_url_is_failed(string probe)
    {
        var fake = new FakeOutsideProbe(result: true);
        var module = new ManualPortForwardModule(fake);

        await module.StartAsync(Contexts.Create(settings: Settings(probe: probe)), CancellationToken.None);

        Assert.Equal(TransportModuleState.Failed, module.Status.State);
        Assert.Equal("invalid-probe-url", module.Status.Reason);
        Assert.Empty(module.Status.Offers);
        Assert.Empty(fake.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task Public_port_out_of_range_is_failed(int port)
    {
        var module = new ManualPortForwardModule(new FakeOutsideProbe());

        await module.StartAsync(Contexts.Create(settings: Settings(port: port)), CancellationToken.None);

        Assert.Equal(TransportModuleState.Failed, module.Status.State);
        Assert.Equal("invalid-public-port", module.Status.Reason);
        Assert.Empty(module.Status.Offers);
    }

    [Fact]
    public async Task Stop_withdraws_the_offer_and_disables_the_module()
    {
        var module = new ManualPortForwardModule(new FakeOutsideProbe());
        await module.StartAsync(Contexts.Create(settings: Settings()), CancellationToken.None);
        var seen = new List<ModuleStatus>();
        module.StatusChanged += (_, status) => seen.Add(status);

        await module.StopAsync(CancellationToken.None);

        Assert.Equal(TransportModuleState.Disabled, module.Status.State);
        Assert.Empty(module.Status.Offers);
        Assert.Equal(TransportModuleState.Disabled, Assert.Single(seen).State);
    }

    [Fact]
    public async Task Status_changes_are_raised_while_starting()
    {
        var module = new ManualPortForwardModule(new FakeOutsideProbe(result: true));
        var states = new List<TransportModuleState>();
        module.StatusChanged += (_, status) => states.Add(status.State);

        await module.StartAsync(Contexts.Create(settings: Settings(probe: _probeUrl)), CancellationToken.None);

        Assert.Equal([TransportModuleState.Starting, TransportModuleState.Running], states);
    }
}
