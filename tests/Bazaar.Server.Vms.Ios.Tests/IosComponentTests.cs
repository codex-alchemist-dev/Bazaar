using System.Text.Json;
using Bazaar.Core;
using Bazaar.Server.Settings;
using Bazaar.Server.Vms.Devices;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;
using Bazaar.Server.Vms.MacNode;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Vms.Ios.Tests;

internal sealed class MapSettingStore(Dictionary<string, object> values) : ISettingStore
{
    public Task<T> GetAsync<T>(SettingKey<T> key, string scopeId = "", CancellationToken cancellationToken = default) =>
        Task.FromResult(values.TryGetValue(key.Name, out var v) ? (T)v : key.Default);

    public Task<SettingWriteResult> SetAsync<T>(SettingKey<T> key, T value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<SettingWriteResult> SetJsonAsync(string key, JsonElement value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

public sealed class IosComponentTests
{
    private const string _runtime = "com.apple.CoreSimulator.SimRuntime.iOS-18-0";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, object> Pinned(string runtime = _runtime) => new() { [DeviceProvidersSettings.IosRuntimePin.Name] = runtime };

    private static ServiceProvider Build(Dictionary<string, object> settings, IMacNodeLink? link = null)
    {
        var services = new ServiceCollection();
        if (link is not null)
        {
            services.AddSingleton(link);
        }

        new IosComponent().AddServices(services, new ConfigurationBuilder().Build());
        services.AddSingleton<ISettingStore>(new MapSettingStore(settings));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Manifest_names_the_vms_ios_component()
    {
        var manifest = new IosComponent().Manifest;

        Assert.Equal("vms-ios", manifest.Id);
        Assert.Equal(ComponentKind.Feature, manifest.Kind);
        Assert.Contains("settings", manifest.DependsOn);
    }

    [Fact]
    public void It_registers_one_catalogue_entry_and_one_backend_source_of_kind_ios()
    {
        using var provider = Build([]);

        var entry = Assert.Single(provider.GetServices<IDeviceProviderCatalogEntry>());
        var source = Assert.Single(provider.GetServices<IVmBackendSource>());

        Assert.Equal("ios", entry.Kind);
        Assert.Same(entry, source);
        Assert.Equal([VmOs.Ios], source.GuestOs);
        Assert.Equal(VmProviderPlacement.OnDemand, source.Placement);
    }

    [Fact]
    public void Adding_the_component_twice_keeps_one_entry()
    {
        var services = new ServiceCollection();
        new IosComponent().AddServices(services, new ConfigurationBuilder().Build());
        new IosComponent().AddServices(services, new ConfigurationBuilder().Build());

        Assert.Single(services, d => d.ServiceType == typeof(IDeviceProviderCatalogEntry));
        Assert.Single(services, d => d.ServiceType == typeof(IMacNodeLink));
    }

    [Fact]
    public void Without_a_registered_link_the_default_is_the_unlinked_one()
    {
        using var provider = Build([]);

        Assert.IsType<UnlinkedMacNodeLink>(provider.GetRequiredService<IMacNodeLink>());
    }

    [Fact]
    public void A_link_registered_first_wins_over_the_default()
    {
        var link = new InMemoryMacNodeLink();
        using var provider = Build(Pinned(), link);

        Assert.Same(link, provider.GetRequiredService<IMacNodeLink>());
    }

    [Fact]
    public async Task With_no_mac_node_linked_the_entry_reports_not_usable_with_the_plain_reason()
    {
        using var provider = Build([]);
        var entry = provider.GetRequiredService<IDeviceProviderCatalogEntry>();
        await entry.RefreshAsync(Ct);

        var probe = entry.ProbeDevice();

        Assert.False(probe.Usable);
        Assert.Equal("no Mac node linked", probe.Reason);
        Assert.Equal(0, entry.Running);
        Assert.Empty(entry.RunningGuests());
        Assert.Equal("ios", entry.Capabilities.GuestOs);
        Assert.True(entry.Capabilities.RemoteNode);
    }

    [Fact]
    public async Task The_backend_source_lookup_is_unusable_with_the_same_reason_and_does_not_throw()
    {
        using var provider = Build([]);
        var source = provider.GetRequiredService<IVmBackendSource>();

        var lookup = await source.GetCurrentAsync(Ct);

        Assert.False(lookup.Usable);
        Assert.Equal("no Mac node linked", lookup.Reason);
    }

    [Fact]
    public async Task An_unset_runtime_pin_is_reported_after_the_node_checks_not_thrown()
    {
        var link = new InMemoryMacNodeLink(InMemoryMacNodeLink.GoodNode);
        await link.ConnectAsync(Ct);
        using var provider = Build([], link);
        var entry = provider.GetRequiredService<IDeviceProviderCatalogEntry>();
        await entry.RefreshAsync(Ct);

        var probe = entry.ProbeDevice();

        Assert.False(probe.Usable);
        Assert.Contains("vms.ios.runtime_pin", probe.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bad_runtime_id_is_reported_not_thrown()
    {
        using var provider = Build(Pinned("nope"));
        var entry = provider.GetRequiredService<IDeviceProviderCatalogEntry>();
        await entry.RefreshAsync(Ct);

        var probe = entry.ProbeDevice();

        Assert.False(probe.Usable);
        Assert.Contains("simulator runtime", probe.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connected_node_makes_the_entry_usable_without_a_restart()
    {
        var link = new InMemoryMacNodeLink(InMemoryMacNodeLink.GoodNode);
        using var provider = Build(Pinned(), link);
        var entry = provider.GetRequiredService<IosCatalogEntry>();
        await entry.RefreshAsync(Ct);
        Assert.False(entry.ProbeDevice().Usable);

        await link.ConnectAsync(Ct);

        Assert.True(entry.ProbeDevice().Usable);
        var lookup = await entry.GetCurrentAsync(Ct);
        Assert.True(lookup.Usable);
        Assert.IsType<IosBackend>(lookup.Backend);
    }

    [Fact]
    public async Task The_configured_cap_is_applied_and_a_live_change_reaches_the_same_backend()
    {
        var values = Pinned();
        values[DeviceProvidersSettings.IosMaxConcurrent.Name] = 3;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
        using var entry = new IosCatalogEntry(new MapSettingStore(values), new UnlinkedMacNodeLink(), clock: clock);
        await entry.RefreshAsync(Ct);
        var backend = entry.Backend!;
        Assert.Equal(3, entry.Capabilities.MaxConcurrentInstances);

        values[DeviceProvidersSettings.IosMaxConcurrent.Name] = 1;
        clock.Advance(DeviceProviderEntry<IosBackend>.DefaultRefreshInterval);
        await entry.RefreshAsync(Ct);

        Assert.Same(backend, entry.Backend);
        Assert.Equal(1, entry.Capabilities.MaxConcurrentInstances);
    }

    [Fact]
    public void The_runtime_version_is_derived_the_same_way_each_time()
    {
        Assert.Equal(IosCatalogEntry.RuntimeVersion("a"), IosCatalogEntry.RuntimeVersion("a"));
        Assert.NotEqual(IosCatalogEntry.RuntimeVersion("a"), IosCatalogEntry.RuntimeVersion("b"));
    }

    [Fact]
    public void The_runtime_pin_setting_is_an_install_operator_key_that_checks_its_shape()
    {
        var key = DeviceProvidersSettings.IosRuntimePin;

        Assert.Equal("vms.ios.runtime_pin", key.Name);
        Assert.Equal(string.Empty, key.Default);
        Assert.Contains(key, DeviceProvidersSettings.All);
        Assert.False(DeviceProvidersSettings.AppliesLive(key));
        Assert.Null(key.CheckValue(JsonSerializer.SerializeToElement(string.Empty)));
        Assert.Null(key.CheckValue(JsonSerializer.SerializeToElement(_runtime)));
        Assert.NotNull(key.CheckValue(JsonSerializer.SerializeToElement("nope")));
        Assert.NotNull(key.CheckValue(JsonSerializer.SerializeToElement(_runtime + " x")));
    }

    [Fact]
    public void The_ios_settings_are_registered_with_their_documented_defaults_and_limits()
    {
        Assert.Equal(2, DeviceProvidersSettings.IosMaxConcurrent.Default);
        Assert.Contains(DeviceProvidersSettings.IosMaxConcurrent, DeviceProvidersSettings.All);
        Assert.Contains(DeviceProvidersSettings.MacNodeEndpoint, DeviceProvidersSettings.All);
        Assert.Contains(DeviceProvidersSettings.MacNodeKeyPin, DeviceProvidersSettings.All);
        Assert.Equal(string.Empty, DeviceProvidersSettings.MacNodeEndpoint.Default);
        Assert.True(DeviceProvidersSettings.AppliesLive(DeviceProvidersSettings.IosMaxConcurrent));
        Assert.False(DeviceProvidersSettings.AppliesLive(DeviceProvidersSettings.MacNodeEndpoint));
    }
}
