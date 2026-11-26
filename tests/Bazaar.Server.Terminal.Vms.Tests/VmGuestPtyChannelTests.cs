using Bazaar.Server.Terminal.Abstractions;
using Bazaar.Server.VmMounts.Abstractions;
using AgentVmInfo = Bazaar.Server.VmTransport.AgentVmInfo;
using GuestPty = Bazaar.Server.Terminal.Abstractions.IGuestPty;

namespace Bazaar.Server.Terminal.Vms.Tests;

public class VmGuestPtyChannelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<GuestPty> Open(VmGuestPtyChannel channel, Guid agent, Guid vm, int cols = 80, int rows = 24) =>
        channel.OpenForAgentAsync(agent.ToString("D"), vm.ToString("D"), "t1", cols, rows, Ct);

    [Fact]
    public async Task Opens_a_terminal_on_the_agents_running_vm_with_the_requested_size()
    {
        var rig = new Rig();
        var (vm, guest) = rig.AddRunningVm(Rig.AgentA, "vm-a");
        var channel = new VmGuestPtyChannel(rig.Directory);

        Assert.True(channel.IsAvailable);
        await using var pty = await Open(channel, Rig.AgentA, vm, 100, 30);

        Assert.Equal([(100, 30)], guest.Opened);
    }

    [Fact]
    public async Task Input_and_resize_reach_the_guest_terminal_in_order()
    {
        var rig = new Rig();
        var (vm, guest) = rig.AddRunningVm(Rig.AgentA, "vm-a");
        await using var pty = await Open(new VmGuestPtyChannel(rig.Directory), Rig.AgentA, vm);

        await pty.WriteAsync(new byte[] { 1, 2 }, Ct);
        await pty.ResizeAsync(120, 40, Ct);
        await pty.WriteAsync(new byte[] { 3 }, Ct);

        Assert.Equal([new byte[] { 1, 2 }, new byte[] { 3 }], guest.Ptys[0].Writes);
        Assert.Equal([(120, 40)], guest.Ptys[0].Resizes);
    }

    [Fact]
    public async Task Output_arrives_in_order_and_the_exit_code_is_reported()
    {
        var rig = new Rig();
        var (vm, guest) = rig.AddRunningVm(Rig.AgentA, "vm-a");
        await using var pty = await Open(new VmGuestPtyChannel(rig.Directory), Rig.AgentA, vm);

        guest.Ptys[0].Emit("one");
        guest.Ptys[0].Emit("two");
        guest.Ptys[0].Emit("three");
        guest.Ptys[0].Exit(7);

        var seen = new List<string>();
        await foreach (var chunk in pty.Output.ReadAllAsync(Ct))
        {
            seen.Add(System.Text.Encoding.UTF8.GetString(chunk));
        }

        Assert.Equal(["one", "two", "three"], seen);
        Assert.Equal(7, await pty.Exited.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task Disposing_closes_the_guest_terminal()
    {
        var rig = new Rig();
        var (vm, guest) = rig.AddRunningVm(Rig.AgentA, "vm-a");
        var pty = await Open(new VmGuestPtyChannel(rig.Directory), Rig.AgentA, vm);

        await pty.DisposeAsync();

        Assert.Equal(1, guest.Ptys[0].CloseCount);
        Assert.Equal(-1, await pty.Exited.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task A_channel_that_ends_mid_session_completes_output_and_reports_minus_one()
    {
        var rig = new Rig();
        var (vm, guest) = rig.AddRunningVm(Rig.AgentA, "vm-a");
        await using var pty = await Open(new VmGuestPtyChannel(rig.Directory), Rig.AgentA, vm);

        guest.End();

        await pty.Output.Completion.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(-1, await pty.Exited.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task A_vm_whose_channel_has_ended_is_refused()
    {
        var rig = new Rig();
        var (vm, guest) = rig.AddRunningVm(Rig.AgentA, "vm-a");
        guest.End();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Open(new VmGuestPtyChannel(rig.Directory), Rig.AgentA, vm));
    }

    [Fact]
    public async Task A_vm_that_is_not_running_has_no_channel_and_is_refused()
    {
        var rig = new Rig();
        var stopped = Guid.NewGuid();
        rig.Store.Vms.Add((Rig.AgentA, new AgentVmInfo(stopped, GuestOs.Linux, "vm-stopped", Retired: false)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Open(new VmGuestPtyChannel(rig.Directory), Rig.AgentA, stopped));
    }

    [Fact]
    public async Task A_retired_vm_is_refused_even_with_an_open_channel()
    {
        var rig = new Rig();
        var (vm, _) = rig.AddRunningVm(Rig.AgentA, "vm-a");
        rig.Store.Vms.Clear();
        rig.Store.Vms.Add((Rig.AgentA, new AgentVmInfo(vm, GuestOs.Linux, "vm-a", Retired: true)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Open(new VmGuestPtyChannel(rig.Directory), Rig.AgentA, vm));
    }

    [Fact]
    public async Task An_agent_cannot_open_a_terminal_in_another_agents_vm()
    {
        var rig = new Rig();
        var (vmA, guestA) = rig.AddRunningVm(Rig.AgentA, "vm-a");
        rig.AddRunningVm(Rig.AgentB, "vm-b");
        var channel = new VmGuestPtyChannel(rig.Directory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Open(channel, Rig.AgentB, vmA));

        Assert.Empty(guestA.Opened);
    }

    [Fact]
    public async Task Each_agent_gets_a_terminal_in_its_own_vm_only()
    {
        var rig = new Rig();
        var (vmA, guestA) = rig.AddRunningVm(Rig.AgentA, "vm-a");
        var (vmB, guestB) = rig.AddRunningVm(Rig.AgentB, "vm-b");
        var channel = new VmGuestPtyChannel(rig.Directory);

        await using var a = await Open(channel, Rig.AgentA, vmA);
        await using var b = await Open(channel, Rig.AgentB, vmB);
        await a.WriteAsync(new byte[] { 65 }, Ct);

        Assert.Single(guestA.Ptys[0].Writes);
        Assert.Empty(guestB.Ptys[0].Writes);
    }

    [Theory]
    [InlineData("not-a-guid", "aaaaaaaa-0000-0000-0000-000000000001")]
    [InlineData("00000000-0000-0000-0000-000000000000", "aaaaaaaa-0000-0000-0000-000000000001")]
    [InlineData("aaaaaaaa-0000-0000-0000-000000000009", "not-a-guid")]
    public async Task Ids_that_are_not_guids_are_refused(string vmId, string agentId)
    {
        var rig = new Rig();
        rig.AddRunningVm(Rig.AgentA, "vm-a");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new VmGuestPtyChannel(rig.Directory).OpenForAgentAsync(agentId, vmId, "t1", 80, 24, Ct));
    }

    [Fact]
    public async Task The_overload_without_an_agent_always_refuses()
    {
        var rig = new Rig();
        var (vm, guest) = rig.AddRunningVm(Rig.AgentA, "vm-a");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new VmGuestPtyChannel(rig.Directory).OpenAsync(vm.ToString("D"), "t1", 80, 24, Ct));

        Assert.Empty(guest.Opened);
    }

    [Fact]
    public async Task Without_a_directory_the_channel_is_unavailable_and_refuses()
    {
        var channel = new VmGuestPtyChannel(null);

        Assert.False(channel.IsAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Open(channel, Rig.AgentA, Guid.NewGuid()));
    }

    [Fact]
    public async Task A_guest_that_fails_the_open_surfaces_the_failure()
    {
        var rig = new Rig();
        var (vm, guest) = rig.AddRunningVm(Rig.AgentA, "vm-a");
        guest.FailOpen = true;

        await Assert.ThrowsAsync<VmTransport.GuestChannelException>(() => Open(new VmGuestPtyChannel(rig.Directory), Rig.AgentA, vm));
    }

    [Fact]
    public async Task A_cancelled_open_does_not_reach_the_guest()
    {
        var rig = new Rig();
        var (vm, guest) = rig.AddRunningVm(Rig.AgentA, "vm-a");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new VmGuestPtyChannel(rig.Directory).OpenForAgentAsync(Rig.AgentA.ToString("D"), vm.ToString("D"), "t1", 80, 24, cts.Token));

        Assert.Empty(guest.Opened);
    }
}
