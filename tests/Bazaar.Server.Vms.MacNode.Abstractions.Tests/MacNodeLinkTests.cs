using Bazaar.Contracts.Modules;
using Bazaar.Server.Vms.Devices;

namespace Bazaar.Server.Vms.MacNode.Abstractions.Tests;

public sealed class MacNodeLinkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly MacGuestSpec _sim = new("runtime-18", "iphone_16", 0, 0, 0);
    private static readonly MacGuestSpec _vm = new("restore-15", null, 4, 8192, 64);

    private static async Task<InMemoryMacNodeLink> Connected(MacNodeInfo? info = null)
    {
        var link = new InMemoryMacNodeLink(info ?? InMemoryMacNodeLink.GoodNode);
        await link.ConnectAsync(Ct);
        return link;
    }

    [Fact]
    public void The_apple_rule_is_stated_in_the_shared_text()
    {
        Assert.Contains("Apple hardware", MacNodeRules.AppleHardwareRule, StringComparison.Ordinal);
        Assert.Equal(2, MacNodeRules.MaxMacOsVmInstances);
    }

    [Fact]
    public async Task A_new_link_is_unlinked_and_refuses_every_operation_with_the_plain_reason()
    {
        var link = new InMemoryMacNodeLink();

        Assert.Equal(MacNodeState.Unlinked, link.Status.State);
        Assert.Equal("no Mac node linked", link.Status.UnavailableReason);
        var ex = await Assert.ThrowsAsync<MacNodeUnavailableException>(() => link.CreateGuestAsync(MacGuestKind.IosSimulator, _sim, Ct));
        Assert.Equal("no Mac node linked", ex.Message);
        await Assert.ThrowsAsync<MacNodeUnavailableException>(() => link.ConnectAsync(Ct));
    }

    [Fact]
    public async Task The_unlinked_link_is_a_null_object()
    {
        var link = new UnlinkedMacNodeLink();

        Assert.Same(MacNodeStatus.Unlinked, link.Status);
        await Assert.ThrowsAsync<MacNodeUnavailableException>(() => link.ConnectAsync(Ct));
        await Assert.ThrowsAsync<MacNodeUnavailableException>(() => link.LaunchGuestAsync(MacGuestKind.MacOsVm, "x", Ct));
        await Assert.ThrowsAsync<MacNodeUnavailableException>(() => link.ScreenshotAsync(MacGuestKind.MacOsVm, "x", Ct));
        await link.DisconnectAsync(Ct);
    }

    [Fact]
    public async Task Connecting_reports_the_node_facts_and_disconnecting_returns_to_disconnected()
    {
        var link = new InMemoryMacNodeLink(InMemoryMacNodeLink.GoodNode);
        Assert.Equal(MacNodeState.Disconnected, link.Status.State);
        Assert.Equal("the Mac node is not connected", link.Status.UnavailableReason);

        await link.ConnectAsync(Ct);
        Assert.True(link.Status.IsConnected);
        Assert.Null(link.Status.UnavailableReason);
        Assert.Equal("arm64", link.Status.Info!.Architecture);

        await link.DisconnectAsync(Ct);
        Assert.False(link.Status.IsConnected);
    }

    [Fact]
    public async Task A_key_mismatch_fails_closed_as_refused()
    {
        var link = new InMemoryMacNodeLink(InMemoryMacNodeLink.GoodNode);
        link.RefuseKey();

        await Assert.ThrowsAsync<MacNodeUnavailableException>(() => link.ConnectAsync(Ct));

        Assert.Equal(MacNodeState.Refused, link.Status.State);
        Assert.StartsWith("the Mac node was refused", link.Status.UnavailableReason, StringComparison.Ordinal);
        await Assert.ThrowsAsync<MacNodeUnavailableException>(() => link.CreateGuestAsync(MacGuestKind.IosSimulator, _sim, Ct));
    }

    [Fact]
    public async Task A_dropped_connection_blocks_operations_until_it_is_connected_again()
    {
        var link = await Connected();
        var guest = await link.CreateGuestAsync(MacGuestKind.IosSimulator, _sim, Ct);

        link.DropConnection("the Mac went to sleep");
        var ex = await Assert.ThrowsAsync<MacNodeUnavailableException>(() => link.LaunchGuestAsync(MacGuestKind.IosSimulator, guest, Ct));
        Assert.Contains("the Mac went to sleep", ex.Message, StringComparison.Ordinal);

        await link.ConnectAsync(Ct);
        await link.LaunchGuestAsync(MacGuestKind.IosSimulator, guest, Ct);
        Assert.True(await link.IsGuestRunningAsync(MacGuestKind.IosSimulator, guest, Ct));
    }

    [Fact]
    public async Task A_guest_runs_through_create_launch_command_screenshot_stop_delete()
    {
        var link = await Connected();
        var guest = await link.CreateGuestAsync(MacGuestKind.IosSimulator, _sim, Ct);
        Assert.False(await link.IsGuestRunningAsync(MacGuestKind.IosSimulator, guest, Ct));

        await link.LaunchGuestAsync(MacGuestKind.IosSimulator, guest, Ct);
        await link.RunCommandAsync(MacGuestKind.IosSimulator, guest, new MacGuestCommand.InstallApp("build/App.app"), Ct);
        await link.RunCommandAsync(MacGuestKind.IosSimulator, guest, new MacGuestCommand.LaunchApp("com.example.app"), Ct);
        await link.RunCommandAsync(MacGuestKind.IosSimulator, guest, new MacGuestCommand.Input(new DeviceInput.Tap(10, 20)), Ct);
        var screen = await link.ScreenshotAsync(MacGuestKind.IosSimulator, guest, Ct);
        await link.StopGuestAsync(MacGuestKind.IosSimulator, guest, force: false, Ct);
        await link.DeleteGuestAsync(MacGuestKind.IosSimulator, guest, Ct);

        Assert.Equal(0x89, screen.Png[0]);
        Assert.Equal(
            ["Connect", "Create", "Launch", "RunCommand", "RunCommand", "RunCommand", "Screenshot", "Stop", "Delete"],
            link.Calls.Select(c => c.Operation).ToArray());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => link.LaunchGuestAsync(MacGuestKind.IosSimulator, guest, Ct));
    }

    [Fact]
    public async Task Commands_need_a_running_guest_of_the_named_kind()
    {
        var link = await Connected();
        var guest = await link.CreateGuestAsync(MacGuestKind.IosSimulator, _sim, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => link.RunCommandAsync(MacGuestKind.IosSimulator, guest, new MacGuestCommand.LaunchApp("com.example.app"), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => link.ScreenshotAsync(MacGuestKind.IosSimulator, guest, Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => link.LaunchGuestAsync(MacGuestKind.MacOsVm, guest, Ct));
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("/abs/App.app")]
    [InlineData("a\\b")]
    public async Task The_node_refuses_an_unsafe_workspace_path(string path)
    {
        var link = await Connected();
        var guest = await link.CreateGuestAsync(MacGuestKind.IosSimulator, _sim, Ct);
        await link.LaunchGuestAsync(MacGuestKind.IosSimulator, guest, Ct);

        await Assert.ThrowsAsync<ArgumentException>(() => link.RunCommandAsync(MacGuestKind.IosSimulator, guest, new MacGuestCommand.InstallApp(path), Ct));
        Assert.DoesNotContain(link.Calls, c => c.Operation == "RunCommand");
    }

    [Fact]
    public async Task The_node_refuses_a_bad_app_id_and_control_characters_in_text()
    {
        var link = await Connected();
        var guest = await link.CreateGuestAsync(MacGuestKind.IosSimulator, _sim, Ct);
        await link.LaunchGuestAsync(MacGuestKind.IosSimulator, guest, Ct);

        await Assert.ThrowsAsync<ArgumentException>(() => link.RunCommandAsync(MacGuestKind.IosSimulator, guest, new MacGuestCommand.LaunchApp("x; rm -rf /"), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => link.RunCommandAsync(MacGuestKind.IosSimulator, guest, new MacGuestCommand.Input(new DeviceInput.Text("a\nb")), Ct));
    }

    [Fact]
    public async Task A_simulator_guest_needs_a_device_type_and_every_guest_a_parent()
    {
        var link = await Connected();

        await Assert.ThrowsAsync<ArgumentException>(() => link.CreateGuestAsync(MacGuestKind.IosSimulator, _sim with { DeviceType = null }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => link.CreateGuestAsync(MacGuestKind.MacOsVm, _vm with { ParentRef = " " }, Ct));
    }

    [Fact]
    public async Task The_node_caps_macos_vms_at_apples_two_even_if_it_reports_more()
    {
        var link = await Connected(InMemoryMacNodeLink.GoodNode with { MaxVmInstances = 5 });
        var a = await link.CreateGuestAsync(MacGuestKind.MacOsVm, _vm, Ct);
        var b = await link.CreateGuestAsync(MacGuestKind.MacOsVm, _vm, Ct);
        var c = await link.CreateGuestAsync(MacGuestKind.MacOsVm, _vm, Ct);

        await link.LaunchGuestAsync(MacGuestKind.MacOsVm, a, Ct);
        await link.LaunchGuestAsync(MacGuestKind.MacOsVm, b, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => link.LaunchGuestAsync(MacGuestKind.MacOsVm, c, Ct));

        await link.StopGuestAsync(MacGuestKind.MacOsVm, a, force: true, Ct);
        await link.LaunchGuestAsync(MacGuestKind.MacOsVm, c, Ct);
        Assert.Contains(link.Calls, call => call.Operation == "StopForce");
    }

    [Fact]
    public async Task The_simulator_is_not_subject_to_the_vm_cap()
    {
        var link = await Connected();
        for (var i = 0; i < 4; i++)
        {
            var guest = await link.CreateGuestAsync(MacGuestKind.IosSimulator, _sim, Ct);
            await link.LaunchGuestAsync(MacGuestKind.IosSimulator, guest, Ct);
        }

        Assert.Equal(4, link.Calls.Count(c => c.Operation == "Launch"));
    }

    [Fact]
    public void Node_facts_map_to_host_features_on_a_macos_host()
    {
        var facts = InMemoryMacNodeLink.GoodNode.ToHostFacts();

        Assert.Equal("macos", facts.OperatingSystem);
        Assert.Equal("arm64", facts.Architecture);
        Assert.Contains("xcode-simulator", facts.Features);
        Assert.Contains("virtualization-framework", facts.Features);

        var bare = (InMemoryMacNodeLink.GoodNode with { XcodeInstalled = false, VirtualizationAvailable = false }).ToHostFacts();
        Assert.Empty(bare.Features);
    }

    [Fact]
    public void The_macos_host_and_apple_features_are_known_to_the_host_requirement_vocabulary()
    {
        var requirements = new HostRequirements(HostRequirements.CurrentSchemaVersion, ["macos"], ["arm64"], ["virtualization-framework", "xcode-simulator"]);

        Assert.Empty(HostCompatibility.ValidateRequirements(requirements));
        Assert.Empty(HostCompatibility.Check(requirements, InMemoryMacNodeLink.GoodNode.ToHostFacts()));
    }
}
