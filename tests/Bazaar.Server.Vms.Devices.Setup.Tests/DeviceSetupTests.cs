using System.Text.Json;
using Bazaar.Server.Setup;
using Bazaar.Server.Vms.Devices.Setup;

namespace Bazaar.Server.Vms.Devices.Setup.Tests;

public sealed class DeviceSetupTests
{
    private static SetupPlan Plan(Dictionary<string, object> settings, params string[] modules)
    {
        var plan = new SetupPlan();
        foreach (var (key, value) in settings)
        {
            plan.Settings[key] = JsonSerializer.SerializeToElement(value);
        }

        plan.Modules.AddRange(modules);
        return plan;
    }

    private static Dictionary<string, object> AndroidSettings() => new()
    {
        ["vms.devices.enabled"] = new List<string> { "android" },
        ["vms.android.sdk_root"] = "/opt/android-sdk",
        ["vms.android.avd_home"] = "/srv/bazaar/avd",
        ["vms.android.system_image"] = "system-images;android-35;google_apis;x86_64",
    };

    [Fact]
    public void EmptyDefaultIsValid() => Assert.Empty(DeviceSetupValidator.Validate(new SetupPlan()));

    [Fact]
    public void AndroidValidWithAllSettingsAndModule() =>
        Assert.Empty(DeviceSetupValidator.Validate(Plan(AndroidSettings(), "Bazaar.Server.Vms.Android")));

    [Theory]
    [InlineData("vms.android.sdk_root")]
    [InlineData("vms.android.avd_home")]
    [InlineData("vms.android.system_image")]
    public void AndroidNeedsEachSetting(string missing)
    {
        var settings = AndroidSettings();
        settings.Remove(missing);
        var issues = DeviceSetupValidator.Validate(Plan(settings, "Bazaar.Server.Vms.Android"));
        Assert.Contains(issues, i => i.Path == "settings." + missing);
    }

    [Theory]
    [InlineData("relative/sdk")]
    [InlineData("~/Android/Sdk")]
    [InlineData("%USERPROFILE%\\AppData\\Local\\Android")]
    [InlineData("/home/someone/.android/avd")]
    [InlineData("/opt/../etc")]
    public void RelativeOrProfilePathRefused(string path)
    {
        var settings = AndroidSettings();
        settings["vms.android.avd_home"] = path;
        var issues = DeviceSetupValidator.Validate(Plan(settings, "Bazaar.Server.Vms.Android"));
        Assert.Contains(issues, i => i.Path == "settings.vms.android.avd_home");
    }

    [Fact]
    public void WindowsDrivePathAccepted() => Assert.True(DeviceSetupValidator.IsExplicitAbsolutePath(@"D:\Android\Sdk"));

    [Theory]
    [InlineData("ios")]
    [InlineData("macos")]
    public void AppleKindsRefusedWithNotice(string kind)
    {
        var plan = Plan(new() { ["vms.devices.enabled"] = new List<string> { kind } });
        var issues = DeviceSetupValidator.Validate(plan);
        Assert.Contains(issues, i => i.Message.Contains(DeviceSetupText.AppleHardwareNotice, StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownKeyRefused()
    {
        var issues = DeviceSetupValidator.Validate(Plan(new() { ["vms.android.bogus"] = 1 }));
        Assert.Contains(issues, i => i.Path == "settings.vms.android.bogus");
    }

    [Fact]
    public void MicroVmNeedsAbsoluteStateDirAndModule()
    {
        var bad = Plan(new() { ["vms.devices.enabled"] = new List<string> { "microvm" }, ["vms.microvm.state_dir"] = "state" });
        Assert.Equal(2, DeviceSetupValidator.Validate(bad).Count);
        var good = Plan(new() { ["vms.devices.enabled"] = new List<string> { "microvm" }, ["vms.microvm.state_dir"] = "/var/lib/bazaar/microvm" }, "Bazaar.Server.Vms.MicroVm");
        Assert.Empty(DeviceSetupValidator.Validate(good));
    }

    [Fact]
    public void MissingModuleReported()
    {
        var issues = DeviceSetupValidator.Validate(Plan(AndroidSettings()));
        Assert.Contains(issues, i => i.Path == "modules");
    }

    [Fact]
    public async Task StepIsIdempotentAndFailsWithoutChange()
    {
        var step = new DeviceProvidersSetupStep();
        var ctx = new SetupApplyContext("h", SetupEntryPath.Api, "op", null, null, SetupStateSnapshot.Empty, []);
        var ok = Plan(AndroidSettings(), "Bazaar.Server.Vms.Android");
        var ct = TestContext.Current.CancellationToken;
        Assert.True((await step.ApplyAsync(ok, ctx, ct)).Succeeded);
        Assert.True((await step.ApplyAsync(ok, ctx, ct)).Succeeded);
        Assert.Equal("vms.devices", step.Id);

        var bad = Plan(new() { ["vms.devices.enabled"] = new List<string> { "ios" } });
        var result = await step.ApplyAsync(bad, ctx, ct);
        Assert.False(result.Succeeded);
        Assert.False(string.IsNullOrEmpty(result.Message));
    }
}
