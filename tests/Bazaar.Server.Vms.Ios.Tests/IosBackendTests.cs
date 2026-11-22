using Bazaar.Server.Vms.Devices;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;
using Bazaar.Server.Vms.MacNode;

namespace Bazaar.Server.Vms.Ios.Tests;

public sealed class IosBackendTests
{
    private const string _runtime = "com.apple.CoreSimulator.SimRuntime.iOS-18-0";
    private static readonly Guid _image = Guid.CreateVersion7();
    private static readonly VmSpec _spec = new(VmOs.Ios, 0, 0, 0);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IosOptions Options(int max = 2, string runtime = _runtime) => new()
    {
        RuntimeId = runtime,
        RuntimeBuild = "22A3351",
        RuntimeVersion = _image,
        MaxConcurrent = max,
    };

    private static async Task<(IosBackend Backend, InMemoryMacNodeLink Link)> Make(int max = 2, MacNodeInfo? info = null, string runtime = _runtime)
    {
        var link = new InMemoryMacNodeLink(info ?? InMemoryMacNodeLink.GoodNode);
        await link.ConnectAsync(Ct);
        return (new IosBackend(link, Options(max, runtime)), link);
    }

    [Fact]
    public async Task The_capability_report_says_host_process_remote_node_and_no_save()
    {
        var (b, _) = await Make();

        Assert.IsAssignableFrom<IDeviceProviderCatalogEntry>(b);
        Assert.Equal("ios", b.Kind);
        Assert.Equal("ios", b.Capabilities.GuestOs);
        Assert.Equal(DeviceIsolation.HostProcess, b.Capabilities.Isolation);
        Assert.True(b.Capabilities.RemoteNode);
        Assert.False(b.Capabilities.Save);
        Assert.False(b.Capabilities.Checkpoint);
        Assert.False(b.Capabilities.Restore);
        Assert.False(b.Capabilities.GuestAgent);
        Assert.False(b.Capabilities.EgressEnforced);
        Assert.False(b.SupportsSaveState);
    }

    [Fact]
    public async Task Probe_passes_with_a_good_connected_node()
    {
        var (b, _) = await Make();

        var probe = b.ProbeDevice();

        Assert.True(probe.Usable);
        Assert.Null(probe.Reason);
    }

    [Fact]
    public void With_no_mac_node_linked_the_probe_says_so_plainly()
    {
        using var b = new IosBackend(new UnlinkedMacNodeLink(), Options());

        var probe = b.ProbeDevice();

        Assert.False(probe.Usable);
        Assert.Equal("no Mac node linked", probe.Reason);
        Assert.Equal("no Mac node linked", b.Probe().Reason);
    }

    [Fact]
    public async Task A_node_that_is_not_connected_or_was_refused_is_not_usable()
    {
        var link = new InMemoryMacNodeLink(InMemoryMacNodeLink.GoodNode);
        using var b = new IosBackend(link, Options());
        Assert.Equal("the Mac node is not connected", b.ProbeDevice().Reason);

        link.RefuseKey();
        await Assert.ThrowsAsync<MacNodeUnavailableException>(() => link.ConnectAsync(Ct));
        Assert.StartsWith("the Mac node was refused", b.ProbeDevice().Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_node_without_xcode_is_not_usable()
    {
        var (b, _) = await Make(info: InMemoryMacNodeLink.GoodNode with { XcodeInstalled = false });

        var probe = b.ProbeDevice();

        Assert.False(probe.Usable);
        Assert.Contains("xcode-simulator", probe.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_node_running_as_the_owner_or_root_is_refused()
    {
        var (b, _) = await Make(info: InMemoryMacNodeLink.GoodNode with { RunsAsDedicatedUser = false });

        var probe = b.ProbeDevice();

        Assert.False(probe.Usable);
        Assert.Contains("dedicated unprivileged", probe.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Intel_macs_can_run_the_simulator()
    {
        var (b, _) = await Make(info: InMemoryMacNodeLink.GoodNode with { Architecture = "x64", VirtualizationAvailable = false });

        Assert.True(b.ProbeDevice().Usable);
    }

    [Fact]
    public async Task Without_a_pinned_runtime_the_provider_is_not_usable_and_refuses_to_create()
    {
        var (b, _) = await Make(runtime: string.Empty);

        Assert.Contains("vms.ios.runtime_pin", b.ProbeDevice().Reason, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.CreateChildAsync(new(_image), _spec, Ct));
    }

    [Fact]
    public async Task Create_makes_a_stopped_simulator_on_the_node_with_the_pinned_runtime_and_device_type()
    {
        var (b, link) = await Make();

        var vm = await b.CreateChildAsync(new(_image), _spec, Ct);

        Assert.Equal(VmState.Stopped, await b.GetStateAsync(vm, Ct));
        Assert.Equal(0, b.Running);
        Assert.Contains(link.Calls, c => c is { Operation: "Create", Kind: MacGuestKind.IosSimulator } && c.NodeRef == vm.BackendRef);
        Assert.DoesNotContain(link.Calls, c => c.Operation == "Launch");
    }

    [Fact]
    public async Task Create_refuses_another_os_and_another_parent()
    {
        var (b, link) = await Make();

        await Assert.ThrowsAsync<NotSupportedException>(() => b.CreateChildAsync(new(_image), _spec with { Os = VmOs.Android }, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.CreateChildAsync(new(Guid.NewGuid()), _spec, Ct));
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

        await b.StopAsync(vm, StopMode.Force, Ct);
        Assert.Equal(VmState.Stopped, await b.GetStateAsync(vm, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.StopAsync(vm, StopMode.Graceful, Ct));

        await b.StartAsync(vm, Ct);
        await b.RetireAsync(vm, Ct);
        Assert.Equal(VmState.Retired, await b.GetStateAsync(vm, Ct));
        Assert.Equal(["Connect", "Create", "Launch", "StopForce", "Launch", "StopForce", "Delete"], link.Calls.Select(c => c.Operation).ToArray());
    }

    [Fact]
    public async Task An_unknown_handle_is_not_found()
    {
        var (b, _) = await Make();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => b.StartAsync(new(Guid.NewGuid(), "sim-0001"), Ct));
    }

    [Fact]
    public async Task The_concurrency_cap_refuses_a_start_past_it_and_frees_on_stop()
    {
        var (b, _) = await Make(max: 1);
        var a = await b.CreateChildAsync(new(_image), _spec, Ct);
        var c = await b.CreateChildAsync(new(_image), _spec, Ct);
        await b.StartAsync(a, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => b.StartAsync(c, Ct));

        await b.StopAsync(a, StopMode.Graceful, Ct);
        await b.StartAsync(c, Ct);
        Assert.Equal(1, b.Running);
    }

    [Fact]
    public async Task A_live_cap_change_applies_to_new_starts_only()
    {
        var (b, _) = await Make(max: 2);
        var a = await b.CreateChildAsync(new(_image), _spec, Ct);
        var c = await b.CreateChildAsync(new(_image), _spec, Ct);
        await b.StartAsync(a, Ct);
        await b.StartAsync(c, Ct);

        b.SetMaxConcurrent(1);

        Assert.Equal(2, b.Running);
        Assert.Equal(1, b.Capabilities.MaxConcurrentInstances);
        Assert.Throws<ArgumentOutOfRangeException>(() => b.SetMaxConcurrent(0));
    }

    [Fact]
    public async Task Save_checkpoint_and_restore_are_not_supported_and_touch_nothing()
    {
        var (b, link) = await Make();
        var vm = await b.CreateChildAsync(new(_image), _spec, Ct);
        await b.StartAsync(vm, Ct);
        var before = link.Calls.Count;

        await Assert.ThrowsAsync<NotSupportedException>(() => b.SaveStateAsync(vm, Ct));
        await Assert.ThrowsAsync<NotSupportedException>(() => b.CheckpointAsync(vm, Ct));
        await Assert.ThrowsAsync<NotSupportedException>(() => b.RestoreAsync(vm, new CheckpointId(Guid.NewGuid()), Ct));

        Assert.Equal(before, link.Calls.Count);
    }

    [Fact]
    public async Task Device_control_sends_typed_commands_for_a_running_guest()
    {
        var (b, link) = await Make();
        var vm = await b.CreateChildAsync(new(_image), _spec, Ct);
        await b.StartAsync(vm, Ct);

        await b.InstallAppAsync(vm.VmId, "build/App.app", Ct);
        await b.LaunchAppAsync(vm.VmId, "com.example.app", Ct);
        await b.SendInputAsync(vm.VmId, new DeviceInput.Tap(1, 2), Ct);
        var screen = await b.CaptureAsync(vm.VmId, Ct);

        Assert.NotEmpty(screen.Png);
        var commands = link.Calls.Where(c => c.Operation == "RunCommand").Select(c => c.Command).ToArray();
        Assert.Equal(new MacGuestCommand.InstallApp("build/App.app"), commands[0]);
        Assert.Equal(new MacGuestCommand.LaunchApp("com.example.app"), commands[1]);
        Assert.Equal(new MacGuestCommand.Input(new DeviceInput.Tap(1, 2)), commands[2]);
    }

    [Fact]
    public async Task Device_control_refuses_a_stopped_guest_and_bad_values_before_the_link()
    {
        var (b, link) = await Make();
        var vm = await b.CreateChildAsync(new(_image), _spec, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => b.LaunchAppAsync(vm.VmId, "com.example.app", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.CaptureAsync(vm.VmId, Ct));

        await b.StartAsync(vm, Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => b.InstallAppAsync(vm.VmId, "../outside.app", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => b.LaunchAppAsync(vm.VmId, "not an id; rm", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => b.SendInputAsync(vm.VmId, new DeviceInput.Text("a\u0007b"), Ct));
        Assert.DoesNotContain(link.Calls, c => c.Operation == "RunCommand");
    }

    [Fact]
    public async Task When_the_node_goes_away_operations_fail_with_the_plain_reason_and_state_is_kept()
    {
        var (b, link) = await Make();
        var vm = await b.CreateChildAsync(new(_image), _spec, Ct);
        await b.StartAsync(vm, Ct);

        link.DropConnection();

        await Assert.ThrowsAsync<MacNodeUnavailableException>(() => b.GetStateAsync(vm, Ct));
        Assert.False(b.ProbeDevice().Usable);
        Assert.Equal(1, b.Running);
    }

    [Fact]
    public async Task A_simulator_that_stopped_on_the_node_reads_back_as_stopped()
    {
        var (b, link) = await Make();
        var vm = await b.CreateChildAsync(new(_image), _spec, Ct);
        await b.StartAsync(vm, Ct);
        await link.StopGuestAsync(MacGuestKind.IosSimulator, vm.BackendRef, force: true, Ct);

        Assert.Equal(VmState.Stopped, await b.GetStateAsync(vm, Ct));
        Assert.Equal(0, b.Running);
    }

    [Theory]
    [InlineData("com.apple.CoreSimulator.SimRuntime.iOS-18-0", true)]
    [InlineData("iOS-18", false)]
    [InlineData("com.apple.CoreSimulator.SimRuntime.", false)]
    [InlineData("com.apple.CoreSimulator.SimRuntime.x; rm -rf /", false)]
    public void Runtime_ids_are_validated(string value, bool ok)
    {
        if (ok)
        {
            Assert.Equal(value, IosIdentifiers.Runtime(value));
        }
        else
        {
            Assert.Throws<ArgumentException>(() => IosIdentifiers.Runtime(value));
        }
    }

    [Fact]
    public void Device_types_are_validated_and_options_reject_bad_values()
    {
        Assert.Equal(IosDeviceProfile.DefaultDeviceType, IosIdentifiers.DeviceType(IosDeviceProfile.DefaultDeviceType));
        Assert.Throws<ArgumentException>(() => IosIdentifiers.DeviceType("iPhone"));
        Assert.Throws<ArgumentException>(() => Options(runtime: "nope").Validate());
        Assert.Throws<ArgumentException>(() => (Options() with { DeviceType = "bad" }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => Options(max: 17).Validate());
        Assert.Throws<ArgumentException>(() => new IosDeviceProfile("x", _runtime).Validated());
    }
}
