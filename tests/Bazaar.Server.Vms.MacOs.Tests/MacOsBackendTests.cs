using Bazaar.Server.Vms.Devices;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;
using Bazaar.Server.Vms.MacNode;

namespace Bazaar.Server.Vms.MacOs.Tests;

public sealed class MacOsBackendTests
{
    private const string _sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly Guid _image = Guid.CreateVersion7();
    private static readonly VmSpec _spec = new(VmOs.MacOs, 4, 8192, 64);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MacOsOptions Options(int max = 2, bool pinned = true) => new()
    {
        RestoreImageId = pinned ? "macos-15-1" : string.Empty,
        RestoreImageBuild = pinned ? "24B83" : string.Empty,
        RestoreImageSha256 = pinned ? _sha : string.Empty,
        RestoreImageVersion = _image,
        MaxConcurrent = max,
    };

    private static async Task<(MacOsBackend Backend, InMemoryMacNodeLink Link)> Make(int max = 2, MacNodeInfo? info = null, bool pinned = true)
    {
        var link = new InMemoryMacNodeLink(info ?? InMemoryMacNodeLink.GoodNode);
        await link.ConnectAsync(Ct);
        return (new MacOsBackend(link, Options(max, pinned)), link);
    }

    [Fact]
    public async Task The_capability_report_is_hardware_virtualised_remote_and_capped_at_two()
    {
        var (b, _) = await Make();

        Assert.IsAssignableFrom<IDeviceProviderCatalogEntry>(b);
        Assert.Equal("macos", b.Kind);
        Assert.Equal("macos", b.Capabilities.GuestOs);
        Assert.Equal(DeviceIsolation.HardwareVirtualised, b.Capabilities.Isolation);
        Assert.Equal(2, b.Capabilities.MaxConcurrentInstances);
        Assert.True(b.Capabilities.RemoteNode);
        Assert.True(b.Capabilities.GuestAgent);
        Assert.False(b.Capabilities.Save);
        Assert.False(b.Capabilities.ScreenSource);
        Assert.False(b.Capabilities.EgressEnforced);
        Assert.False(b.SupportsSaveState);
    }

    [Fact]
    public async Task Probe_passes_with_an_apple_silicon_node_that_has_the_virtualization_framework()
    {
        var (b, _) = await Make();

        Assert.True(b.ProbeDevice().Usable);
    }

    [Fact]
    public void With_no_mac_node_linked_the_probe_says_so_plainly()
    {
        using var b = new MacOsBackend(new UnlinkedMacNodeLink(), Options());

        var probe = b.ProbeDevice();

        Assert.False(probe.Usable);
        Assert.Equal("no Mac node linked", probe.Reason);
    }

    [Fact]
    public async Task An_intel_mac_or_a_node_without_the_framework_is_not_usable()
    {
        var (intel, _) = await Make(info: InMemoryMacNodeLink.GoodNode with { Architecture = "x64" });
        var (bare, _) = await Make(info: InMemoryMacNodeLink.GoodNode with { VirtualizationAvailable = false });

        Assert.Contains("needs arm64", intel.ProbeDevice().Reason, StringComparison.Ordinal);
        Assert.Contains("virtualization-framework", bare.ProbeDevice().Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_pinned_restore_image_the_provider_is_not_usable_and_refuses_to_create()
    {
        var (b, _) = await Make(pinned: false);

        Assert.Contains("restore image", b.ProbeDevice().Reason, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.CreateChildAsync(new(_image), _spec, Ct));
    }

    [Fact]
    public async Task Create_makes_a_stopped_guest_from_the_pinned_image_with_the_requested_size()
    {
        var (b, link) = await Make();

        var vm = await b.CreateChildAsync(new(_image), _spec, Ct);

        Assert.Equal(VmState.Stopped, await b.GetStateAsync(vm, Ct));
        Assert.Contains(link.Calls, c => c is { Operation: "Create", Kind: MacGuestKind.MacOsVm } && c.NodeRef == vm.BackendRef);
        Assert.DoesNotContain(link.Calls, c => c.Operation == "Launch");
    }

    [Fact]
    public async Task Create_refuses_another_os_another_parent_and_a_bad_size()
    {
        var (b, link) = await Make();

        await Assert.ThrowsAsync<NotSupportedException>(() => b.CreateChildAsync(new(_image), _spec with { Os = VmOs.Ios }, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.CreateChildAsync(new(Guid.NewGuid()), _spec, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => b.CreateChildAsync(new(_image), _spec with { Vcpu = 0 }, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => b.CreateChildAsync(new(_image), _spec with { MemoryMb = 512 }, Ct));
        Assert.DoesNotContain(link.Calls, c => c.Operation == "Create");
    }

    [Fact]
    public async Task Start_stop_and_retire_drive_the_node_and_the_state()
    {
        var (b, link) = await Make();
        var vm = await b.CreateChildAsync(new(_image), _spec, Ct);

        await b.StartAsync(vm, Ct);
        Assert.Equal(VmState.Running, await b.GetStateAsync(vm, Ct));
        Assert.Equal(vm.VmId, Assert.Single(b.RunningGuests()).VmId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.StartAsync(vm, Ct));

        await b.StopAsync(vm, StopMode.Graceful, Ct);
        Assert.Equal(VmState.Stopped, await b.GetStateAsync(vm, Ct));
        await b.StartAsync(vm, Ct);
        await b.RetireAsync(vm, Ct);

        Assert.Equal(VmState.Retired, await b.GetStateAsync(vm, Ct));
        Assert.Equal(["Connect", "Create", "Launch", "Stop", "Launch", "StopForce", "Delete"], link.Calls.Select(c => c.Operation).ToArray());
    }

    [Fact]
    public async Task The_third_guest_is_refused_at_apples_cap_of_two()
    {
        var (b, _) = await Make(max: 2);
        var guests = new List<VmHandle>();
        for (var i = 0; i < 3; i++)
        {
            guests.Add(await b.CreateChildAsync(new(_image), _spec, Ct));
        }

        await b.StartAsync(guests[0], Ct);
        await b.StartAsync(guests[1], Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.StartAsync(guests[2], Ct));

        await b.StopAsync(guests[0], StopMode.Graceful, Ct);
        await b.StartAsync(guests[2], Ct);
        Assert.Equal(2, b.Running);
    }

    [Fact]
    public async Task The_setting_may_lower_the_cap_and_never_raise_it_above_two()
    {
        var (b, _) = await Make(max: 1);
        var a = await b.CreateChildAsync(new(_image), _spec, Ct);
        var c = await b.CreateChildAsync(new(_image), _spec, Ct);
        await b.StartAsync(a, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.StartAsync(c, Ct));

        b.SetMaxConcurrent(2);
        await b.StartAsync(c, Ct);

        Assert.Throws<ArgumentOutOfRangeException>(() => b.SetMaxConcurrent(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => Options(max: 3).Validate());
        Assert.Equal(2, MacOsInstanceCap.Effective(9));
        Assert.Equal(1, MacOsInstanceCap.Effective(0));
    }

    [Fact]
    public async Task A_node_that_reports_a_lower_cap_is_respected()
    {
        var (b, _) = await Make(info: InMemoryMacNodeLink.GoodNode with { MaxVmInstances = 1 });
        var a = await b.CreateChildAsync(new(_image), _spec, Ct);
        var c = await b.CreateChildAsync(new(_image), _spec, Ct);
        await b.StartAsync(a, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => b.StartAsync(c, Ct));
    }

    [Fact]
    public async Task Save_checkpoint_restore_capture_and_input_are_not_offered_and_touch_nothing()
    {
        var (b, link) = await Make();
        var vm = await b.CreateChildAsync(new(_image), _spec, Ct);
        await b.StartAsync(vm, Ct);
        var before = link.Calls.Count;

        await Assert.ThrowsAsync<NotSupportedException>(() => b.SaveStateAsync(vm, Ct));
        await Assert.ThrowsAsync<NotSupportedException>(() => b.CheckpointAsync(vm, Ct));
        await Assert.ThrowsAsync<NotSupportedException>(() => b.RestoreAsync(vm, new CheckpointId(Guid.NewGuid()), Ct));
        await Assert.ThrowsAsync<NotSupportedException>(() => b.CaptureAsync(vm.VmId, Ct));
        await Assert.ThrowsAsync<NotSupportedException>(() => b.SendInputAsync(vm.VmId, new DeviceInput.Tap(1, 2), Ct));

        Assert.Equal(before, link.Calls.Count);
    }

    [Fact]
    public async Task Device_control_installs_and_launches_apps_through_typed_commands()
    {
        var (b, link) = await Make();
        var vm = await b.CreateChildAsync(new(_image), _spec, Ct);
        await b.StartAsync(vm, Ct);

        await b.InstallAppAsync(vm.VmId, "build/Tool.app", Ct);
        await b.LaunchAppAsync(vm.VmId, "com.example.tool", Ct);

        var commands = link.Calls.Where(c => c.Operation == "RunCommand").Select(c => c.Command).ToArray();
        Assert.Equal(new MacGuestCommand.InstallApp("build/Tool.app"), commands[0]);
        Assert.Equal(new MacGuestCommand.LaunchApp("com.example.tool"), commands[1]);
    }

    [Fact]
    public async Task Device_control_refuses_a_stopped_guest_and_bad_values_before_the_link()
    {
        var (b, link) = await Make();
        var vm = await b.CreateChildAsync(new(_image), _spec, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => b.LaunchAppAsync(vm.VmId, "com.example.tool", Ct));

        await b.StartAsync(vm, Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => b.InstallAppAsync(vm.VmId, "/etc/passwd", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => b.LaunchAppAsync(vm.VmId, "x y", Ct));
        Assert.DoesNotContain(link.Calls, c => c.Operation == "RunCommand");
    }

    [Fact]
    public async Task When_the_node_goes_away_operations_fail_and_the_probe_says_why()
    {
        var (b, link) = await Make();
        var vm = await b.CreateChildAsync(new(_image), _spec, Ct);
        await b.StartAsync(vm, Ct);

        link.DropConnection();

        await Assert.ThrowsAsync<MacNodeUnavailableException>(() => b.GetStateAsync(vm, Ct));
        Assert.Equal("the Mac node is not connected", b.ProbeDevice().Reason);
    }

    [Theory]
    [InlineData("macos-15-1", "24B83", _sha, true)]
    [InlineData("macos 15", "24B83", _sha, false)]
    [InlineData("macos-15-1", "bad", _sha, false)]
    [InlineData("macos-15-1", "24B83", "ABC", false)]
    public void The_restore_image_pin_is_validated(string id, string build, string sha, bool ok)
    {
        var pin = new MacOsRestoreImagePin(id, build, sha);

        if (ok)
        {
            Assert.Same(pin, pin.Validated());
        }
        else
        {
            Assert.Throws<ArgumentException>(() => pin.Validated());
        }
    }

    [Fact]
    public void A_partly_set_pin_is_rejected_by_the_options()
    {
        Assert.Throws<ArgumentException>(() => (Options(pinned: false) with { RestoreImageId = "macos-15-1" }).Validate());
        Assert.False(Options(pinned: false).IsPinned);
        Assert.True(Options().IsPinned);
    }
}
