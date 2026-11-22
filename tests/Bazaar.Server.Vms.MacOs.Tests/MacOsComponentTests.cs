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

namespace Bazaar.Server.Vms.MacOs.Tests;

internal sealed class MapSettingStore(Dictionary<string, object> values) : ISettingStore
{
    public Task<T> GetAsync<T>(SettingKey<T> key, string scopeId = "", CancellationToken cancellationToken = default) =>
        Task.FromResult(values.TryGetValue(key.Name, out var v) ? (T)v : key.Default);

    public Task<SettingWriteResult> SetAsync<T>(SettingKey<T> key, T value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<SettingWriteResult> SetJsonAsync(string key, JsonElement value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

public sealed class MacOsComponentTests
{
    private const string _sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IConfiguration Config(bool pinned = true) => new ConfigurationBuilder()
        .AddInMemoryCollection(pinned
            ? new Dictionary<string, string?>
            {
                ["Bazaar:Vms:MacOs:RestoreImageId"] = "macos-15-1",
                ["Bazaar:Vms:MacOs:RestoreImageBuild"] = "24B83",
                ["Bazaar:Vms:MacOs:RestoreImageSha256"] = _sha,
            }
            : [])
        .Build();

    private static ServiceProvider Build(Dictionary<string, object> settings, IConfiguration? config = null, IMacNodeLink? link = null)
    {
        var services = new ServiceCollection();
        if (link is not null)
        {
            services.AddSingleton(link);
        }

        new MacOsComponent().AddServices(services, config ?? Config());
        services.AddSingleton<ISettingStore>(new MapSettingStore(settings));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Manifest_names_the_vms_macos_component()
    {
        var manifest = new MacOsComponent().Manifest;

        Assert.Equal("vms-macos", manifest.Id);
        Assert.Equal(ComponentKind.Feature, manifest.Kind);
        Assert.Contains("settings", manifest.DependsOn);
    }

    [Fact]
    public void It_registers_one_catalogue_entry_and_one_backend_source_of_kind_macos()
    {
        using var provider = Build([]);

        var entry = Assert.Single(provider.GetServices<IDeviceProviderCatalogEntry>());
        var source = Assert.Single(provider.GetServices<IVmBackendSource>());

        Assert.Equal("macos", entry.Kind);
        Assert.Same(entry, source);
        Assert.Equal([VmOs.MacOs], source.GuestOs);
        Assert.Equal(VmProviderPlacement.OnDemand, source.Placement);
    }

    [Fact]
    public void Adding_the_component_twice_keeps_one_entry_and_one_link()
    {
        var services = new ServiceCollection();
        new MacOsComponent().AddServices(services, Config());
        new MacOsComponent().AddServices(services, Config());

        Assert.Single(services, d => d.ServiceType == typeof(IDeviceProviderCatalogEntry));
        Assert.Single(services, d => d.ServiceType == typeof(IMacNodeLink));
    }

    [Fact]
    public void Without_a_registered_link_the_default_is_the_unlinked_one_and_a_registered_one_wins()
    {
        using var plain = Build([]);
        var link = new InMemoryMacNodeLink();
        using var custom = Build([], link: link);

        Assert.IsType<UnlinkedMacNodeLink>(plain.GetRequiredService<IMacNodeLink>());
        Assert.Same(link, custom.GetRequiredService<IMacNodeLink>());
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
        Assert.Equal("macos", entry.Capabilities.GuestOs);
        Assert.Equal(2, entry.Capabilities.MaxConcurrentInstances);
    }

    [Fact]
    public async Task The_backend_source_lookup_is_unusable_with_the_same_reason_and_does_not_throw()
    {
        using var provider = Build([]);
        var lookup = await provider.GetRequiredService<IVmBackendSource>().GetCurrentAsync(Ct);

        Assert.False(lookup.Usable);
        Assert.Equal("no Mac node linked", lookup.Reason);
    }

    [Fact]
    public async Task An_unset_pin_and_a_malformed_pin_are_reported_not_thrown()
    {
        var link = new InMemoryMacNodeLink(InMemoryMacNodeLink.GoodNode);
        await link.ConnectAsync(Ct);
        using var unset = Build([], Config(pinned: false), link);
        var bad = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:Vms:MacOs:RestoreImageSha256"] = "nope" }).Build();
        using var malformed = Build([], bad, link);

        var unsetEntry = unset.GetRequiredService<IDeviceProviderCatalogEntry>();
        var badEntry = malformed.GetRequiredService<IDeviceProviderCatalogEntry>();
        await unsetEntry.RefreshAsync(Ct);
        await badEntry.RefreshAsync(Ct);

        Assert.Contains("restore image", unsetEntry.ProbeDevice().Reason, StringComparison.Ordinal);
        Assert.False(badEntry.ProbeDevice().Usable);
        Assert.Contains("Invalid", badEntry.ProbeDevice().Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connected_node_makes_the_entry_usable_without_a_restart()
    {
        var link = new InMemoryMacNodeLink(InMemoryMacNodeLink.GoodNode);
        using var provider = Build([], link: link);
        var entry = provider.GetRequiredService<MacOsCatalogEntry>();
        await entry.RefreshAsync(Ct);
        Assert.False(entry.ProbeDevice().Usable);

        await link.ConnectAsync(Ct);

        Assert.True(entry.ProbeDevice().Usable);
        Assert.IsType<MacOsBackend>((await entry.GetCurrentAsync(Ct)).Backend);
    }

    [Fact]
    public async Task A_live_cap_change_reaches_the_same_backend()
    {
        var values = new Dictionary<string, object> { [DeviceProvidersSettings.MacOsMaxConcurrent.Name] = 2 };
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
        using var entry = new MacOsCatalogEntry(new MapSettingStore(values), Config(), new UnlinkedMacNodeLink(), clock: clock);
        await entry.RefreshAsync(Ct);
        var backend = entry.Backend!;

        values[DeviceProvidersSettings.MacOsMaxConcurrent.Name] = 1;
        clock.Advance(DeviceProviderEntry<MacOsBackend>.DefaultRefreshInterval);
        await entry.RefreshAsync(Ct);

        Assert.Same(backend, entry.Backend);
        Assert.Equal(1, entry.Capabilities.MaxConcurrentInstances);
    }

    [Fact]
    public void The_restore_image_version_is_configured_or_derived_the_same_way_each_time()
    {
        var empty = new ConfigurationBuilder().Build();
        var id = Guid.NewGuid();
        var configured = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:Vms:MacOs:RestoreImageVersion"] = id.ToString() }).Build();

        Assert.Equal(id, MacOsCatalogEntry.RestoreImageVersion(configured, "a", "b", "c"));
        Assert.Equal(MacOsCatalogEntry.RestoreImageVersion(empty, "a", "b", "c"), MacOsCatalogEntry.RestoreImageVersion(empty, "a", "b", "c"));
        Assert.NotEqual(MacOsCatalogEntry.RestoreImageVersion(empty, "a", "b", "c"), MacOsCatalogEntry.RestoreImageVersion(empty, "a", "b", "d"));
    }

    [Fact]
    public void The_macos_setting_may_lower_the_apple_cap_and_never_raise_it()
    {
        var key = DeviceProvidersSettings.MacOsMaxConcurrent;

        Assert.Equal(2, key.Default);
        Assert.Contains(key, DeviceProvidersSettings.All);
        Assert.True(DeviceProvidersSettings.AppliesLive(key));
        Assert.NotNull(key.CheckValue(JsonSerializer.SerializeToElement(3)));
        Assert.NotNull(key.CheckValue(JsonSerializer.SerializeToElement(0)));
        Assert.Null(key.CheckValue(JsonSerializer.SerializeToElement(1)));
    }
}
