using System.Text;
using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmMounts.Containment;

namespace Bazaar.Server.VmMounts.Tests;

/// <summary>Containment detach (hard taint, kill switch): never queues behind guest I/O, never writes back, scrubs the guest.</summary>
public sealed class ContainmentTests
{
    private const string _guestProfile = MountHarness.LinuxProfile;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for a condition.");
            await Task.Delay(10, MountHarness.Ct);
        }
    }

    [Fact]
    public async Task A_containment_detach_does_not_wait_for_a_guest_call_that_never_answers()
    {
        using var h = new MountHarness();
        h.WriteHostProfile("Default/Cookies", "trusted");
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        h.Channel.ListBlocksUntilCancelled = true;
        var writeBack = h.Manager.WriteBackProfileAsync(mount.MountId, MountHarness.Ct);
        await WaitUntilAsync(() => h.Channel.ListCalls == 1);

        // The write-back holds the VM lock and is stuck in the guest. The containment detach must still finish quickly.
        var results = await h.Manager.DetachAllForAgentAsync(h.Agent, DetachReason.HardTaint, MountHarness.Ct).WaitAsync(TimeSpan.FromSeconds(5), MountHarness.Ct);

        Assert.True(Assert.Single(results).Detached);
        Assert.Equal(WriteBackOutcome.Failed, (await writeBack).Outcome);
        Assert.Equal("trusted", h.ReadHostProfile("Default/Cookies"));
        Assert.Contains($"detach:{mount.MountId}", h.Provider.Calls);
    }

    [Fact]
    public async Task A_taint_that_arrives_while_the_profile_is_being_read_stops_the_swap()
    {
        using var h = new MountHarness();
        h.WriteHostProfile("Default/Cookies", "trusted");
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        h.Channel.GuestWrite($"{_guestProfile}/Default/Cookies", Encoding.UTF8.GetBytes("planted"));
        Task? containment = null;
        h.Channel.OnGetStarted = () => containment ??= h.Manager.DetachAllForAgentAsync(h.Agent, DetachReason.HardTaint, MountHarness.Ct);

        var result = await h.Manager.WriteBackProfileAsync(mount.MountId, MountHarness.Ct);
        await containment!;

        Assert.Equal(WriteBackOutcome.Failed, result.Outcome);
        Assert.Equal("trusted", h.ReadHostProfile("Default/Cookies"));
        Assert.Empty(h.Audit.OfKind(VmMountAuditActions.WriteBack));
        Assert.NotNull((await h.Store.GetAsync(mount.MountId, MountHarness.Ct))!.DetachedAt);
    }

    [Fact]
    public async Task A_guest_call_that_exceeds_the_server_side_timeout_fails_the_write_back_but_not_the_detach()
    {
        using var h = new MountHarness(guestIoTimeout: TimeSpan.FromMilliseconds(200));
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        h.Channel.ListBlocksUntilCancelled = true;

        var result = await h.Manager.DetachAsync(mount.MountId, DetachReason.Clean, MountHarness.Ct).WaitAsync(TimeSpan.FromSeconds(10), MountHarness.Ct);

        Assert.True(result.Detached);
        Assert.Equal(WriteBackOutcome.Failed, result.WriteBack);
        Assert.Contains($"detach:{mount.MountId}", h.Provider.Calls);
    }

    [Fact]
    public async Task A_cancelled_clean_detach_still_detaches_host_side_and_reports_a_failed_write_back()
    {
        using var h = new MountHarness();
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        h.Channel.ListBlocksUntilCancelled = true;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(MountHarness.Ct);

        var detach = h.Manager.DetachAsync(mount.MountId, DetachReason.Clean, cts.Token);
        await WaitUntilAsync(() => h.Channel.ListCalls == 1);
        await cts.CancelAsync();
        var result = await detach.WaitAsync(TimeSpan.FromSeconds(10), MountHarness.Ct);

        Assert.True(result.Detached);
        Assert.Equal(WriteBackOutcome.Failed, result.WriteBack);
        Assert.NotNull((await h.Store.GetAsync(mount.MountId, MountHarness.Ct))!.DetachedAt);
    }

    [Fact]
    public async Task Detach_all_goes_on_after_a_mount_whose_detach_is_cancelled()
    {
        using var h = new MountHarness();
        var soul = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        var ws = await h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct);
        h.Provider.FailNextDetach = new OperationCanceledException();

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => h.Manager.DetachAllForAgentAsync(h.Agent, DetachReason.KillSwitch, MountHarness.Ct));

        Assert.Single(ex.InnerExceptions);
        var rows = new[] { await h.Store.GetAsync(soul.MountId, MountHarness.Ct), await h.Store.GetAsync(ws.MountId, MountHarness.Ct) };
        Assert.Equal(1, rows.Count(r => r!.DetachedAt is not null));
    }

    [Fact]
    public async Task A_mount_whose_containment_detach_failed_can_never_be_written_back_afterwards()
    {
        using var h = new MountHarness();
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        h.Provider.FailDetach = new IOException("share busy");
        await Assert.ThrowsAsync<AggregateException>(() => h.Manager.DetachAllForAgentAsync(h.Agent, DetachReason.HardTaint, MountHarness.Ct));
        var getsBefore = h.Channel.GetCalls;

        var ex = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.WriteBackProfileAsync(mount.MountId, MountHarness.Ct));

        Assert.Equal(MountRefusalReason.NotAttached, ex.Reason);
        Assert.Equal(getsBefore, h.Channel.GetCalls);
    }

    [Theory]
    [InlineData(DetachReason.HardTaint)]
    [InlineData(DetachReason.KillSwitch)]
    public async Task A_containment_detach_scrubs_the_guest_and_a_clean_one_does_not(DetachReason reason)
    {
        using var h = new MountHarness();
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);

        var result = await h.Manager.DetachAsync(mount.MountId, reason, MountHarness.Ct);

        Assert.True(result.GuestScrubbed);
        var request = Assert.Single(h.Scrubber.Requests);
        Assert.Equal(new GuestScrubRequest(h.Agent, h.Vm, mount.MountId, reason), request);

        var second = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        var clean = await h.Manager.DetachAsync(second.MountId, DetachReason.Clean, MountHarness.Ct);
        Assert.False(clean.GuestScrubbed);
        Assert.Single(h.Scrubber.Requests);
    }

    [Fact]
    public async Task The_default_scrubber_refuses_so_an_unwired_install_reports_the_failure_but_still_detaches()
    {
        using var h = new MountHarness();
        var manager = h.NewManager(scrubber: new RefusingGuestScrubber());
        var mount = await manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DetachAsync(mount.MountId, DetachReason.HardTaint, MountHarness.Ct));

        Assert.NotNull((await h.Store.GetAsync(mount.MountId, MountHarness.Ct))!.DetachedAt);
        Assert.Contains($"detach:{mount.MountId}", h.Provider.Calls);
        Assert.Single(h.Audit.OfKind(VmMountAuditActions.ScrubFailed));
    }

    [Fact]
    public async Task The_guest_is_scrubbed_even_when_the_provider_cannot_detach()
    {
        using var h = new MountHarness();
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        h.Provider.FailDetach = new IOException("share busy");

        await Assert.ThrowsAsync<IOException>(() => h.Manager.DetachAsync(mount.MountId, DetachReason.KillSwitch, MountHarness.Ct));

        Assert.Single(h.Scrubber.Requests);
        Assert.Null((await h.Store.GetAsync(mount.MountId, MountHarness.Ct))!.DetachedAt);
    }
}
