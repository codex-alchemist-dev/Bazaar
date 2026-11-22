using Bazaar.Server.Vms.Devices;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Vms.MicroVm.Tests;

/// <summary>The microVM entry as a backend source with a live cap (doc 62 section 9.2).</summary>
public sealed class MicroVmLiveSettingsTests
{
    private static readonly VmSpec _spec = new(VmOs.Linux, 1, 512, 4);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Root => OperatingSystem.IsWindows() ? @"V:\mv" : "/opt/mv";

    private readonly FakeMonitor _monitor = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
    private readonly Dictionary<string, object> _values = new()
    {
        [DeviceProvidersSettings.MicroVmStateDir.Name] = (OperatingSystem.IsWindows() ? @"V:\mv" : "/opt/mv") + "/state",
        [DeviceProvidersSettings.MicroVmMaxConcurrent.Name] = 1,
    };

    private readonly IConfiguration _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Bazaar:Vms:MicroVm:MonitorBinaryPath"] = Root + "/fc",
        ["Bazaar:Vms:MicroVm:KernelPath"] = Root + "/vmlinux",
        ["Bazaar:Vms:MicroVm:RootfsPath"] = Root + "/rootfs.ext4",
        ["Bazaar:Vms:MicroVm:ImageVersion"] = "0193a1b2-0000-7000-8000-000000000001",
    }).Build();

    private int _builds;

    private MicroVmCatalogEntry Entry() => new(new MapSettingStore(_values), _config, o =>
    {
        _builds++;
        return new MicroVmBackend(_monitor, _monitor, o);
    }, _clock);

    [Fact]
    public async Task A_monitor_change_after_the_build_needs_a_restart_and_is_named_by_key()
    {
        using var entry = Entry();
        await entry.RefreshAsync(Ct);

        _values[DeviceProvidersSettings.MicroVmMonitor.Name] = MicroVmMonitorSetting.CloudHypervisor;
        _clock.Advance(DeviceProviderEntry<MicroVmBackend>.DefaultRefreshInterval);
        await entry.RefreshAsync(Ct);

        Assert.Equal([DeviceProvidersSettings.MicroVmMonitor.Name], entry.RestartRequiredKeys);
        Assert.Equal(1, _builds);
    }

    [Fact]
    public async Task A_live_cap_change_reaches_the_same_backend_without_a_rebuild()
    {
        using var entry = Entry();
        await entry.RefreshAsync(Ct);
        var backend = entry.Backend!;
        var image = new ImageVersionId(Guid.Parse("0193a1b2-0000-7000-8000-000000000001"));
        var first = await backend.CreateChildAsync(image, _spec, Ct);
        var second = await backend.CreateChildAsync(image, _spec, Ct);
        await backend.StartAsync(first, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.StartAsync(second, Ct));

        _values[DeviceProvidersSettings.MicroVmMaxConcurrent.Name] = 3;
        _clock.Advance(DeviceProviderEntry<MicroVmBackend>.DefaultRefreshInterval);
        await entry.RefreshAsync(Ct);

        Assert.Same(backend, entry.Backend);
        Assert.Equal(1, _builds);
        Assert.Equal(3, entry.Capabilities.MaxConcurrentInstances);
        await backend.StartAsync(second, Ct);
    }

    [Fact]
    public async Task Within_the_interval_a_changed_cap_is_not_read_yet()
    {
        using var entry = Entry();
        await entry.RefreshAsync(Ct);

        _values[DeviceProvidersSettings.MicroVmMaxConcurrent.Name] = 3;
        _clock.Advance(TimeSpan.FromSeconds(1));
        await entry.RefreshAsync(Ct);

        Assert.Equal(1, entry.Capabilities.MaxConcurrentInstances);
    }

    [Fact]
    public async Task As_a_backend_source_it_is_on_demand_for_linux_only()
    {
        using var entry = Entry();

        var lookup = await entry.GetCurrentAsync(Ct);

        Assert.Equal(VmProviderPlacement.OnDemand, entry.Placement);
        Assert.Equal([VmOs.Linux], entry.GuestOs);
        Assert.True(lookup.Usable);
        Assert.Same(entry.Backend, lookup.Backend);
    }

    [Fact]
    public void The_component_registers_the_entry_as_a_backend_source()
    {
        var services = new ServiceCollection();
        new MicroVmComponent().AddServices(services, new ConfigurationBuilder().Build());
        services.AddSingleton<Bazaar.Server.Settings.ISettingStore>(new MapSettingStore([]));
        using var provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<MicroVmCatalogEntry>(), Assert.Single(provider.GetServices<IVmBackendSource>()));
    }
}
