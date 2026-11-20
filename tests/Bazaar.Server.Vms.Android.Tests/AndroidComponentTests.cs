using System.Text.Json;
using Bazaar.Core;
using Bazaar.Server.Settings;
using Bazaar.Server.Vms.Devices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Vms.Android.Tests;

internal sealed class MapSettingStore(Dictionary<string, object> values) : ISettingStore
{
    public Task<T> GetAsync<T>(SettingKey<T> key, string scopeId = "", CancellationToken cancellationToken = default) =>
        Task.FromResult(values.TryGetValue(key.Name, out var v) ? (T)v : key.Default);

    public Task<SettingWriteResult> SetAsync<T>(SettingKey<T> key, T value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<SettingWriteResult> SetJsonAsync(string key, JsonElement value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

public sealed class AndroidComponentTests
{
    private static string Root => OperatingSystem.IsWindows() ? @"V:\android" : "/opt/android";

    private static ServiceProvider Build(Dictionary<string, object> settings)
    {
        var services = new ServiceCollection();
        new AndroidComponent().AddServices(services, new ConfigurationBuilder().Build());
        services.AddSingleton<ISettingStore>(new MapSettingStore(settings));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Manifest_names_the_vms_android_component()
    {
        var manifest = new AndroidComponent().Manifest;

        Assert.Equal("vms-android", manifest.Id);
        Assert.Equal(ComponentKind.Feature, manifest.Kind);
        Assert.Contains("settings", manifest.DependsOn);
    }

    [Fact]
    public void It_registers_one_catalogue_entry_of_kind_android()
    {
        using var provider = Build([]);

        var entry = Assert.Single(provider.GetServices<IDeviceProviderCatalogEntry>());

        Assert.Equal("android", entry.Kind);
        Assert.IsType<AndroidCatalogEntry>(entry);
        Assert.Same(entry, provider.GetRequiredService<AndroidCatalogEntry>());
    }

    [Fact]
    public void Adding_the_component_twice_keeps_one_entry()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        new AndroidComponent().AddServices(services, config);
        new AndroidComponent().AddServices(services, config);

        Assert.Single(services, d => d.ServiceType == typeof(IDeviceProviderCatalogEntry));
    }

    [Fact]
    public void An_unset_sdk_root_reports_not_usable_with_a_reason_and_does_not_throw()
    {
        using var provider = Build([]);
        var entry = provider.GetRequiredService<IDeviceProviderCatalogEntry>();

        var probe = entry.ProbeDevice();

        Assert.False(probe.Usable);
        Assert.Contains("vms.android.sdk_root", probe.Reason, StringComparison.Ordinal);
        Assert.Equal(0, entry.Running);
        Assert.Empty(entry.RunningGuests());
        Assert.Equal("android", entry.Capabilities.GuestOs);
    }

    [Fact]
    public void A_relative_path_that_slips_past_the_setting_check_is_reported_not_thrown()
    {
        using var provider = Build(new()
        {
            [DeviceProvidersSettings.AndroidSdkRoot.Name] = "relative",
            [DeviceProvidersSettings.AndroidAvdHome.Name] = Root + "/avd",
            [DeviceProvidersSettings.AndroidSystemImage.Name] = "system-images;android-35;google_apis;x86_64",
        });

        var probe = provider.GetRequiredService<IDeviceProviderCatalogEntry>().ProbeDevice();

        Assert.False(probe.Usable);
        Assert.NotNull(probe.Reason);
    }

    [Fact]
    public void A_complete_configuration_builds_the_backend_with_the_configured_cap()
    {
        using var provider = Build(new()
        {
            [DeviceProvidersSettings.AndroidSdkRoot.Name] = Root + "/sdk",
            [DeviceProvidersSettings.AndroidAvdHome.Name] = Root + "/avd",
            [DeviceProvidersSettings.AndroidSystemImage.Name] = "system-images;android-35;google_apis;x86_64",
            [DeviceProvidersSettings.AndroidMaxConcurrent.Name] = 3,
        });
        var entry = provider.GetRequiredService<AndroidCatalogEntry>();

        Assert.NotNull(entry.Backend);
        Assert.Equal(3, entry.Capabilities.MaxConcurrentInstances);

        // The SDK folder does not exist here, so the probe fails closed with a reason instead of throwing.
        Assert.False(entry.ProbeDevice().Usable);
    }

    [Fact]
    public void The_default_workspace_refuses_install_from_workspace()
    {
        using var provider = Build([]);

        var workspace = provider.GetRequiredService<IAndroidWorkspace>();

        Assert.Throws<NotSupportedException>(() => workspace.ResolveFile(Guid.NewGuid(), "app.apk"));
    }

    [Fact]
    public void The_image_version_is_configured_or_derived_the_same_way_each_time()
    {
        var empty = new ConfigurationBuilder().Build();
        var id = Guid.NewGuid();
        var configured = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:Vms:Android:SystemImageVersion"] = id.ToString() }).Build();

        Assert.Equal(id, AndroidCatalogEntry.ImageVersion(configured, "a;b"));
        Assert.Equal(AndroidCatalogEntry.ImageVersion(empty, "a;b"), AndroidCatalogEntry.ImageVersion(empty, "a;b"));
        Assert.NotEqual(AndroidCatalogEntry.ImageVersion(empty, "a;b"), AndroidCatalogEntry.ImageVersion(empty, "a;c"));
    }
}
