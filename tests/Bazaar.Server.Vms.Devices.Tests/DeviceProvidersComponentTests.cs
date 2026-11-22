using System.Text.Json;
using Bazaar.Core;
using Bazaar.Server.Settings;
using Bazaar.Server.Setup;
using Bazaar.Server.Vms.Devices.Setup;
using Bazaar.Server.Wire.Connection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Vms.Devices.Tests;

public sealed class DeviceProvidersSettingsTests
{
    private static string? Check<T>(SettingKey<T> key, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return key.CheckValue(doc.RootElement.Clone());
    }

    [Fact]
    public void Every_setting_of_doc_62_section_7_is_registered_once_with_a_safe_default()
    {
        var names = DeviceProvidersSettings.All.Select(s => s.Name).ToArray();

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        foreach (var expected in new[]
        {
            "vms.devices.enabled", "vms.devices.idle_stop_minutes", "vms.devices.max_concurrent",
            "vms.android.sdk_root", "vms.android.avd_home", "vms.android.system_image", "vms.android.max_concurrent",
            "vms.microvm.monitor", "vms.microvm.max_concurrent", "vms.microvm.state_dir",
        })
        {
            Assert.Contains(expected, names);
        }

        Assert.Empty(DeviceProvidersSettings.Enabled.Default);
        Assert.Equal(0, DeviceProvidersSettings.IdleStopMinutes.Default);
        Assert.Equal(2, DeviceProvidersSettings.MaxConcurrent.Default);
        Assert.Equal(2, DeviceProvidersSettings.AndroidMaxConcurrent.Default);
        Assert.Equal(8, DeviceProvidersSettings.MicroVmMaxConcurrent.Default);
        Assert.Equal(MicroVmMonitorSetting.Firecracker, DeviceProvidersSettings.MicroVmMonitor.Default);
        Assert.All(DeviceProvidersSettings.All, s =>
        {
            Assert.Equal(SettingScope.Install, s.Scope);
            Assert.Equal(SettingRole.Operator, s.WhoMayChange);
        });
    }

    [Fact]
    public void Every_key_is_known_to_the_setup_validator()
    {
        // The setup step rejects a vms.* key it does not know, so a key added here without it would be refused at setup.
        var plan = new SetupPlan();
        foreach (var setting in DeviceProvidersSettings.All)
        {
            plan.Settings[setting.Name] = JsonSerializer.SerializeToElement(1);
        }

        var unknown = DeviceSetupValidator.Validate(plan).Where(i => i.Message.Contains("not a known device provider setting", StringComparison.Ordinal));

        Assert.Empty(unknown);
    }

    [Theory]
    [InlineData("[]", null)]
    [InlineData("[\"android\",\"microvm\"]", "ok")]
    [InlineData("[\"ios\",\"macos\"]", "ok")]
    [InlineData("[\"android\",\"android\"]", "twice")]
    [InlineData("[\"vmware\"]", "may list only")]
    public void Enabled_accepts_known_kinds_once_each(string json, string? expectation)
    {
        var error = Check(DeviceProvidersSettings.Enabled, json);

        if (expectation is null or "ok")
        {
            Assert.Null(error);
        }
        else
        {
            Assert.Contains(expectation, error, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("\"\"", true)]
    [InlineData("\"/opt/android-sdk\"", true)]
    [InlineData("\"C:\\\\Android\\\\sdk\"", true)]
    [InlineData("\"V:/TOOL/sdk\"", true)]
    [InlineData("\"relative/path\"", false)]
    [InlineData("\"~/sdk\"", false)]
    [InlineData("\"sdk\"", false)]
    public void Path_settings_must_be_absolute_or_empty(string json, bool valid)
    {
        Assert.Equal(valid, Check(DeviceProvidersSettings.AndroidSdkRoot, json) is null);
        Assert.Equal(valid, Check(DeviceProvidersSettings.AndroidAvdHome, json) is null);
        Assert.Equal(valid, Check(DeviceProvidersSettings.MicroVmStateDir, json) is null);
    }

    [Theory]
    [InlineData("\"\"", true)]
    [InlineData("\"system-images;android-35;google_apis;x86_64\"", true)]
    [InlineData("\"image; rm -rf /\"", false)]
    [InlineData("\"a$(b)\"", false)]
    public void System_image_is_an_sdk_package_id(string json, bool valid)
    {
        Assert.Equal(valid, Check(DeviceProvidersSettings.AndroidSystemImage, json) is null);
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("16", true)]
    [InlineData("17", false)]
    public void Android_cap_is_bounded(string json, bool valid)
    {
        Assert.Equal(valid, Check(DeviceProvidersSettings.AndroidMaxConcurrent, json) is null);
    }

    [Fact]
    public void Monitor_accepts_its_snake_case_names_only()
    {
        Assert.Null(Check(DeviceProvidersSettings.MicroVmMonitor, "\"firecracker\""));
        Assert.Null(Check(DeviceProvidersSettings.MicroVmMonitor, "\"cloud_hypervisor\""));
        Assert.NotNull(Check(DeviceProvidersSettings.MicroVmMonitor, "\"qemu\""));
        Assert.NotNull(Check(DeviceProvidersSettings.MicroVmMonitor, "0"));
    }

    [Fact]
    public void The_contribution_registers_all_keys_in_a_registry_next_to_the_built_ins()
    {
        var registry = SettingRegistry.WithContributions([new DeviceProvidersSettingContribution()]);

        foreach (var setting in DeviceProvidersSettings.All)
        {
            Assert.True(registry.TryGet(setting.Name, out var found));
            Assert.Same(setting, found);
        }
    }
}

public sealed class DeviceProvidersComponentTests
{
    private static ServiceProvider Build(params IDeviceProviderCatalogEntry[] entries)
    {
        var services = new ServiceCollection();
        new DeviceProvidersComponent().AddServices(services, new ConfigurationBuilder().Build());
        services.AddSingleton<ISettingStore>(new FakeSettingStore("android"));
        foreach (var entry in entries)
        {
            services.AddSingleton(entry);
        }

        return services.BuildServiceProvider();
    }

    [Fact]
    public void Manifest_names_the_vms_devices_component()
    {
        var manifest = new DeviceProvidersComponent().Manifest;

        Assert.Equal("vms-devices", manifest.Id);
        Assert.Equal(ComponentKind.Feature, manifest.Kind);
        Assert.Contains("settings", manifest.DependsOn);
    }

    [Fact]
    public void It_registers_the_settings_the_wire_handler_and_the_setup_step()
    {
        using var provider = Build(new FakeEntry("android", "android"));

        var contribution = Assert.Single(provider.GetServices<ISettingContribution>());
        Assert.Equal(DeviceProvidersSettings.All.Count, contribution.Settings.Count);
        var handler = Assert.Single(provider.GetServices<IWireRequestHandler>());
        Assert.Equal("vms.devices.list", handler.Type);
        var step = Assert.Single(provider.GetServices<ISetupStep>());
        Assert.IsType<DeviceProvidersSetupStep>(step);
    }

    [Fact]
    public void Adding_the_component_twice_keeps_one_setup_step()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        new DeviceProvidersComponent().AddServices(services, config);
        new DeviceProvidersComponent().AddServices(services, config);

        Assert.Single(services, d => d.ServiceType == typeof(ISetupStep));
    }
}
