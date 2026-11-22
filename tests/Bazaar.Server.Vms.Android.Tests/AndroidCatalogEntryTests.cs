using Bazaar.Server.Vms.Devices;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.Android.Tests;

public sealed class AndroidCatalogEntryTests
{
    private static readonly Guid _image = Guid.CreateVersion7();
    private static readonly VmSpec _spec = new(VmOs.Android, 2, 4096, 16);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (AndroidBackend Backend, FakeAndroidHost Host) Make(int max = 2)
    {
        var host = new FakeAndroidHost();
        var options = new AndroidOptions
        {
            SdkRoot = OperatingSystem.IsWindows() ? @"V:\TOOL\android-sdk" : "/opt/sdk",
            AvdHome = OperatingSystem.IsWindows() ? @"V:\TOOL\android-user" : "/opt/avd",
            SystemImagePackage = "system-images;android-35;google_apis;x86_64",
            SystemImageVersion = _image,
            MaxConcurrent = max,
        };
        return (new AndroidBackend(host, host, host, options), host);
    }

    [Fact]
    public void Entry_reports_kind_capabilities_and_configured_cap()
    {
        var (b, _) = Make(1);
        Assert.IsAssignableFrom<IDeviceProviderCatalogEntry>(b);
        Assert.Equal("android", b.Kind);
        Assert.Equal("android", b.Capabilities.GuestOs);
        Assert.Equal(1, b.Capabilities.MaxConcurrentInstances);
        Assert.False(b.Capabilities.EgressEnforced);
    }

    [Fact]
    public void Probe_passes_with_the_emulator_feature_and_gives_a_reason_without_it()
    {
        var (b, h) = Make();
        var ok = b.ProbeDevice();
        Assert.True(ok.Usable);
        Assert.Null(ok.Reason);

        h.Facts = new("linux", "x64", new HashSet<string>());
        var bad = b.ProbeDevice();
        Assert.False(bad.Usable);
        Assert.False(string.IsNullOrWhiteSpace(bad.Reason));
    }

    [Fact]
    public async Task Only_running_guests_are_listed_and_counted()
    {
        var (b, _) = Make();
        Assert.Equal(0, b.Running);
        var a = await b.CreateChildAsync(new(_image), _spec, Ct);
        var c = await b.CreateChildAsync(new(_image), _spec, Ct);
        Assert.Empty(b.RunningGuests());

        await b.StartAsync(a, Ct);
        var guests = b.RunningGuests();
        Assert.Equal(1, b.Running);
        Assert.Equal(a.VmId, Assert.Single(guests).VmId);
        Assert.Equal("Running", guests[0].State);

        await b.StartAsync(c, Ct);
        Assert.Equal(2, b.Running);
        await b.StopAsync(a, StopMode.Graceful, Ct);
        Assert.Equal(c.VmId, Assert.Single(b.RunningGuests()).VmId);
    }
}
