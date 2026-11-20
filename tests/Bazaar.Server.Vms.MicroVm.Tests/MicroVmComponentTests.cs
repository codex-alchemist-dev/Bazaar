using System.Text.Json;
using Bazaar.Core;
using Bazaar.Server.Settings;
using Bazaar.Server.Vms.Devices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Vms.MicroVm.Tests;

internal sealed class MapSettingStore(Dictionary<string, object> values) : ISettingStore
{
    public Task<T> GetAsync<T>(SettingKey<T> key, string scopeId = "", CancellationToken cancellationToken = default) =>
        Task.FromResult(values.TryGetValue(key.Name, out var v) ? (T)v : key.Default);

    public Task<SettingWriteResult> SetAsync<T>(SettingKey<T> key, T value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<SettingWriteResult> SetJsonAsync(string key, JsonElement value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

public sealed class MicroVmComponentTests
{
    private static string Root => OperatingSystem.IsWindows() ? @"V:\mv" : "/opt/mv";

    private static ServiceProvider Build(Dictionary<string, object> settings)
    {
        var services = new ServiceCollection();
        new MicroVmComponent().AddServices(services, new ConfigurationBuilder().Build());
        services.AddSingleton<ISettingStore>(new MapSettingStore(settings));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Manifest_names_the_vms_microvm_component()
    {
        var manifest = new MicroVmComponent().Manifest;

        Assert.Equal("vms-microvm", manifest.Id);
        Assert.Equal(ComponentKind.Feature, manifest.Kind);
        Assert.Contains("settings", manifest.DependsOn);
    }

    [Fact]
    public void It_registers_one_catalogue_entry_of_kind_microvm()
    {
        using var provider = Build([]);

        var entry = Assert.Single(provider.GetServices<IDeviceProviderCatalogEntry>());

        Assert.Equal("microvm", entry.Kind);
        Assert.IsType<MicroVmCatalogEntry>(entry);
    }

    [Fact]
    public void An_unset_state_directory_reports_not_usable_with_a_reason_and_does_not_throw()
    {
        using var provider = Build([]);
        var entry = provider.GetRequiredService<IDeviceProviderCatalogEntry>();

        var probe = entry.ProbeDevice();

        Assert.False(probe.Usable);
        Assert.Contains("vms.microvm.state_dir", probe.Reason, StringComparison.Ordinal);
        Assert.Equal(0, entry.Running);
        Assert.Equal("linux", entry.Capabilities.GuestOs);
        Assert.False(entry.Capabilities.Save);
    }

    [Fact]
    public void Unset_binary_kernel_and_rootfs_are_named_in_the_reason()
    {
        using var provider = Build(new() { [DeviceProvidersSettings.MicroVmStateDir.Name] = Root + "/state" });

        var probe = provider.GetRequiredService<IDeviceProviderCatalogEntry>().ProbeDevice();

        Assert.False(probe.Usable);
        Assert.Contains("MonitorBinaryPath", probe.Reason, StringComparison.Ordinal);
        Assert.Contains("KernelPath", probe.Reason, StringComparison.Ordinal);
        Assert.Contains("RootfsPath", probe.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_complete_configuration_builds_the_backend_with_the_selected_monitor_and_cap()
    {
        var built = new List<MicroVmOptions>();
        var settings = new MapSettingStore(new()
        {
            [DeviceProvidersSettings.MicroVmStateDir.Name] = Root + "/state",
            [DeviceProvidersSettings.MicroVmMonitor.Name] = MicroVmMonitorSetting.CloudHypervisor,
            [DeviceProvidersSettings.MicroVmMaxConcurrent.Name] = 5,
        });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:Vms:MicroVm:MonitorBinaryPath"] = Root + "/ch",
            ["Bazaar:Vms:MicroVm:KernelPath"] = Root + "/vmlinux",
            ["Bazaar:Vms:MicroVm:RootfsPath"] = Root + "/rootfs.ext4",
        }).Build();
        var entry = new MicroVmCatalogEntry(settings, config, o =>
        {
            built.Add(o);
            var fake = new FakeMonitor();
            return new MicroVmBackend(fake, fake, o);
        });

        Assert.NotNull(entry.Backend);
        Assert.Equal(MicroVmMonitorKind.CloudHypervisor, Assert.Single(built).Monitor);
        Assert.Equal(5, entry.Capabilities.MaxConcurrentInstances);
        Assert.True(entry.ProbeDevice().Usable);
    }

    [Fact]
    public void Linux_facts_need_kvm_and_the_monitor_binary()
    {
        var options = new MicroVmOptions { ImageVersion = Guid.NewGuid(), StateDirectory = Root + "/s", MonitorBinaryPath = Root + "/fc" };

        var ready = new LinuxMicroVmHostFacts(options, _ => true, _ => true, "linux").Current();
        var noKvm = new LinuxMicroVmHostFacts(options, _ => true, _ => false, "linux").Current();
        var noBinary = new LinuxMicroVmHostFacts(options, _ => false, _ => true, "linux").Current();
        var windows = new LinuxMicroVmHostFacts(options, _ => true, _ => true, "windows").Current();

        Assert.Contains("kvm", ready.Features);
        Assert.Contains("microvm", ready.Features);
        Assert.Empty(noKvm.Features);
        Assert.Equal(["kvm"], noBinary.Features);
        Assert.Empty(windows.Features);
        Assert.Equal("windows", windows.OperatingSystem);
    }
}
