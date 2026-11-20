using Bazaar.Server.Vms.Devices;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.MicroVm.Tests;

public sealed class MicroVmCatalogEntryTests
{
    private static readonly Guid _image = Guid.CreateVersion7();
    private static readonly VmSpec _spec = new(VmOs.Linux, 1, 512, 4);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (MicroVmBackend B, FakeMonitor M) Make(int max = 3)
    {
        var m = new FakeMonitor();
        var state = OperatingSystem.IsWindows() ? @"V:\mv" : "/var/lib/bazaar/mv";
        return (new MicroVmBackend(m, m, new MicroVmOptions { ImageVersion = _image, StateDirectory = state, MaxConcurrent = max }), m);
    }

    [Fact]
    public void Entry_reports_kind_capabilities_and_configured_cap()
    {
        var (b, _) = Make(5);
        Assert.IsAssignableFrom<IDeviceProviderCatalogEntry>(b);
        Assert.Equal("microvm", b.Kind);
        Assert.Equal("linux", b.Capabilities.GuestOs);
        Assert.Equal(5, b.Capabilities.MaxConcurrentInstances);
        Assert.True(b.Capabilities.Headless);
        Assert.False(b.Capabilities.Save);
        Assert.False(b.Capabilities.EgressEnforced);
    }

    [Fact]
    public void Probe_reports_usable_or_a_reason()
    {
        var (b, m) = Make();
        Assert.True(b.ProbeDevice().Usable);

        m.Facts = new("windows", "x64", new HashSet<string>());
        var bad = b.ProbeDevice();
        Assert.False(bad.Usable);
        Assert.False(string.IsNullOrWhiteSpace(bad.Reason));
    }

    [Fact]
    public async Task Only_running_guests_are_listed_and_counted()
    {
        var (b, _) = Make();
        var a = await b.CreateChildAsync(new(_image), _spec, Ct);
        var c = await b.CreateChildAsync(new(_image), _spec, Ct);
        Assert.Equal(0, b.Running);
        Assert.Empty(b.RunningGuests());

        await b.StartAsync(c, Ct);
        Assert.Equal(1, b.Running);
        Assert.Equal(c.VmId, Assert.Single(b.RunningGuests()).VmId);

        await b.StartAsync(a, Ct);
        Assert.Equal(2, b.Running);
        await b.StopAsync(c, StopMode.Graceful, Ct);
        var left = Assert.Single(b.RunningGuests());
        Assert.Equal(a.VmId, left.VmId);
        Assert.Equal("Running", left.State);
    }
}
