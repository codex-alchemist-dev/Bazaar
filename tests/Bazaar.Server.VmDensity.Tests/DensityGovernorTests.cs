using Bazaar.Server.VmDensity.Abstractions;

namespace Bazaar.Server.VmDensity.Tests;

/// <summary>
/// Layout used by most tests: total 16384 MiB, reserve 4096, so 12288 MiB is spendable. Four 4096 MiB VMs would be 16384, so with
/// three running (12288) free is 4096 and available is 0.
/// </summary>
public sealed class DensityGovernorTests
{
    private static async Task<DensityAdmissionException> AssertAdmissionFails(Task<AdmissionTicket> task, DensityAdmissionFailure reason)
    {
        var ex = await Assert.ThrowsAsync<DensityAdmissionException>(() => task);
        Assert.Equal(reason, ex.Reason);
        return ex;
    }

    [Fact]
    public async Task Admits_immediately_when_there_is_room()
    {
        var rig = new Rig();
        await using AdmissionTicket ticket = await rig.Admit(1, 4096);

        var snap = rig.Governor.Snapshot();
        Assert.Equal(4096, snap.ReservedMb);
        Assert.Equal(0, snap.Queued);
        Assert.Equal(16384, snap.HostFreeMb);
        Assert.Contains(snap.RecentEvents, e => e.Kind == DensityEventKinds.Admitted && e.VmId == FakeHost.Id(1));
    }

    [Fact]
    public async Task Confirm_started_converts_the_reservation()
    {
        var rig = new Rig();
        AdmissionTicket ticket = await rig.Admit(1, 4096);
        await ticket.ConfirmStartedAsync();
        Assert.Equal(0, rig.Governor.Snapshot().ReservedMb);
    }

    [Fact]
    public async Task Disposing_an_unconfirmed_ticket_releases_the_reservation()
    {
        var rig = new Rig();
        AdmissionTicket ticket = await rig.Admit(1, 4096);
        await ticket.DisposeAsync();
        Assert.Equal(0, rig.Governor.Snapshot().ReservedMb);
    }

    [Fact]
    public async Task Ticket_release_is_idempotent_and_confirm_then_dispose_is_safe()
    {
        var rig = new Rig();
        AdmissionTicket first = await rig.Admit(1, 4096);
        AdmissionTicket second = await rig.Admit(2, 4096);
        await first.ConfirmStartedAsync();
        await first.ConfirmStartedAsync();
        await first.DisposeAsync();
        await first.DisposeAsync();
        Assert.Equal(4096, rig.Governor.Snapshot().ReservedMb); // the second reservation is untouched
        await second.DisposeAsync();
        Assert.Equal(0, rig.Governor.Snapshot().ReservedMb);
    }

    [Fact]
    public async Task Reservations_stop_two_admissions_spending_the_same_free_memory()
    {
        // 12288 spendable, nothing running. Two 8192 requests: only one fits at a time.
        var rig = new Rig();
        AdmissionTicket first = await rig.Admit(1, 8192);
        Task<AdmissionTicket> second = rig.Admit(2, 8192);
        await Rig.WaitUntil(() => rig.HasEvent(DensityEventKinds.Queued), "second queued");
        Assert.False(second.IsCompleted);
        Assert.Equal(1, rig.Governor.Snapshot().Queued);

        await first.DisposeAsync(); // start failed: reservation released, queue re-evaluated
        AdmissionTicket ticket = await rig.Pump(second);
        Assert.Equal(8192, rig.Governor.Snapshot().ReservedMb);
        await ticket.DisposeAsync();
    }

    [Fact]
    public async Task Saves_idle_victims_in_order_then_admits()
    {
        var rig = new Rig();
        rig.Host.Add(1, DensityGuestOs.Linux, "x", 4096, idleMinutes: 50);
        rig.Host.Add(2, DensityGuestOs.Windows, "y", 4096, idleMinutes: 10);
        rig.Host.Add(3, DensityGuestOs.Windows, "z", 4096, idleMinutes: 20);
        rig.Host.Add(4, DensityGuestOs.Linux, "w", 4096, idleMinutes: 40, busy: true);
        // Four 4096 MiB VMs fill the 16384 MiB host, so a 6000 MiB request needs 6000 + 4096 reserve = 10096 freed: three victims.
        AdmissionTicket ticket = await rig.Pump(rig.Admit(9, 6000));

        Assert.Equal([FakeHost.Id(3), FakeHost.Id(2), FakeHost.Id(1)], rig.Host.SaveCalls);
        Assert.Equal(1, rig.Host.RunningCount);
        Assert.Equal(3, rig.EventCount(DensityEventKinds.SavedForRoom));
        await ticket.DisposeAsync();
    }

    [Fact]
    public async Task Two_concurrent_admissions_never_save_the_same_victim_or_double_spend()
    {
        var rig = new Rig();
        rig.Host.Add(1, DensityGuestOs.Windows, "a", 4096, idleMinutes: 30);
        rig.Host.Add(2, DensityGuestOs.Windows, "b", 4096, idleMinutes: 20);
        rig.Host.Add(3, DensityGuestOs.Linux, "c", 4096, busy: true);
        rig.Host.Hold(1);
        rig.Host.Hold(2);

        Task<AdmissionTicket> r1 = rig.Admit(10, 4096, "r1");
        await Rig.WaitUntil(() => rig.Host.SaveCalls.Count == 1, "first save started");
        Task<AdmissionTicket> r2 = rig.Admit(11, 4096, "r2");
        await Rig.WaitUntil(() => rig.Host.SaveCalls.Count == 2, "second save started");

        Assert.Equal(2, rig.Host.SaveCalls.Distinct().Count());
        Assert.False(r1.IsCompleted);
        Assert.False(r2.IsCompleted);
        Assert.Equal(8192, rig.Governor.Snapshot().ReservedMb);

        rig.Host.Release(1);
        rig.Host.Release(2);
        AdmissionTicket t1 = await rig.Pump(r1);
        AdmissionTicket t2 = await rig.Pump(r2);
        Assert.Equal(2, rig.Host.SaveCalls.Count);
        Assert.Equal(8192, rig.Governor.Snapshot().ReservedMb);
        await t1.DisposeAsync();
        await t2.DisposeAsync();
        Assert.Equal(0, rig.Governor.Snapshot().ReservedMb);
    }

    [Fact]
    public async Task A_victim_that_is_being_saved_is_not_picked_by_a_second_admission()
    {
        var rig = new Rig();
        rig.Host.Add(1, DensityGuestOs.Windows, "a", 4096);
        rig.Host.Add(2, DensityGuestOs.Linux, "b", 4096, busy: true);
        rig.Host.Add(3, DensityGuestOs.Linux, "c", 4096, busy: true);
        rig.Host.Hold(1);

        Task<AdmissionTicket> r1 = rig.Admit(10, 4096, "r1");
        await Rig.WaitUntil(() => rig.Host.SaveCalls.Count == 1, "save started");
        Task<AdmissionTicket> r2 = rig.Admit(11, 4096, "r2");
        await Rig.WaitUntil(() => rig.HasEvent(DensityEventKinds.Queued), "second queued");

        Assert.Single(rig.Host.SaveCalls);
        Assert.False(r2.IsCompleted);

        rig.Host.Release(1);
        AdmissionTicket t1 = await rig.Pump(r1);
        Assert.False(r2.IsCompleted); // no room for the second: it waits until the first is released
        await t1.DisposeAsync();
        AdmissionTicket t2 = await rig.Pump(r2);
        Assert.Single(rig.Host.SaveCalls);
        await t2.DisposeAsync();
    }

    [Fact]
    public async Task Queue_is_fifo()
    {
        var rig = new Rig();
        AdmissionTicket holder = await rig.Admit(1, 12288);
        var order = new List<int>();
        var tasks = new List<Task<AdmissionTicket>>();
        for (int i = 2; i <= 4; i++)
        {
            int n = i;
            Task<AdmissionTicket> t = rig.Admit(n, 12288);
            tasks.Add(t);
            await Rig.WaitUntil(() => rig.EventCount(DensityEventKinds.Queued) == n - 1, $"queued {n}");
        }

        await holder.DisposeAsync();
        for (int i = 0; i < 3; i++)
        {
            AdmissionTicket t = await rig.Pump(tasks[i]);
            order.Add(i + 2);
            Assert.DoesNotContain(tasks.Skip(i + 1), x => x.IsCompleted);
            await t.DisposeAsync();
        }

        Assert.Equal([2, 3, 4], order);
    }

    [Fact]
    public async Task A_newer_small_request_does_not_jump_an_older_large_one()
    {
        var rig = new Rig();
        AdmissionTicket holder = await rig.Admit(1, 8192); // 4096 left
        Task<AdmissionTicket> big = rig.Admit(2, 8192);
        await Rig.WaitUntil(() => rig.EventCount(DensityEventKinds.Queued) == 1, "big queued");
        Task<AdmissionTicket> small = rig.Admit(3, 1024);
        await Rig.WaitUntil(() => rig.Governor.Snapshot().Queued == 2, "small queued");
        rig.Time.Advance(TimeSpan.FromSeconds(6));
        await Task.Delay(30, TestContext.Current.CancellationToken);
        Assert.False(small.IsCompleted);

        await holder.DisposeAsync();
        AdmissionTicket bigTicket = await rig.Pump(big);
        await bigTicket.DisposeAsync();
        AdmissionTicket smallTicket = await rig.Pump(small);
        await smallTicket.DisposeAsync();
    }

    [Fact]
    public async Task A_smaller_request_may_pass_when_the_option_allows_it()
    {
        var rig = new Rig(o => o.AllowSmallerToPass = true);
        AdmissionTicket holder = await rig.Admit(1, 8192);
        Task<AdmissionTicket> big = rig.Admit(2, 8192);
        await Rig.WaitUntil(() => rig.EventCount(DensityEventKinds.Queued) == 1, "big queued");
        Task<AdmissionTicket> small = rig.Admit(3, 1024);
        AdmissionTicket smallTicket = await rig.Pump(small);
        Assert.False(big.IsCompleted);
        await smallTicket.DisposeAsync();
        await holder.DisposeAsync();
        await (await rig.Pump(big)).DisposeAsync();
    }

    [Fact]
    public async Task Notify_memory_changed_dequeues_without_waiting_for_the_timer()
    {
        var rig = new Rig(o => o.RecheckInterval = TimeSpan.FromHours(1));
        rig.Host.Add(1, DensityGuestOs.Linux, "a", 12288, busy: true);
        Task<AdmissionTicket> waiting = rig.Admit(9, 4096);
        await Rig.WaitUntil(() => rig.HasEvent(DensityEventKinds.Queued), "queued");

        rig.Host.Remove(1);
        rig.Governor.NotifyMemoryChanged();
        await using AdmissionTicket ticket = await waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(rig.HasEvent(DensityEventKinds.Dequeued));
    }

    [Fact]
    public async Task The_recheck_timer_dequeues_without_a_notice()
    {
        var rig = new Rig(o => o.RecheckInterval = TimeSpan.FromSeconds(5));
        rig.Host.Add(1, DensityGuestOs.Linux, "a", 12288, busy: true);
        Task<AdmissionTicket> waiting = rig.Admit(9, 4096);
        await Rig.WaitUntil(() => rig.HasEvent(DensityEventKinds.Queued), "queued");

        rig.Host.Remove(1);
        await using AdmissionTicket ticket = await rig.Pump(waiting, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Queue_timeout_fails_the_request_and_leaves_nothing_behind()
    {
        var rig = new Rig(o => o.QueueTimeout = TimeSpan.FromMinutes(1));
        rig.Host.Add(1, DensityGuestOs.Linux, "a", 12288, busy: true);
        Task<AdmissionTicket> waiting = rig.Admit(9, 4096);
        await Rig.WaitUntil(() => rig.HasEvent(DensityEventKinds.Queued), "queued");

        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await AssertAdmissionFails(waiting, DensityAdmissionFailure.QueueTimeout);
        var snap = rig.Governor.Snapshot();
        Assert.Equal(0, snap.Queued);
        Assert.Equal(0, snap.ReservedMb);
        Assert.True(rig.HasEvent(DensityEventKinds.TimedOut));
    }

    [Fact]
    public async Task Zero_queue_timeout_means_do_not_wait()
    {
        var rig = new Rig(o => o.QueueTimeout = TimeSpan.Zero);
        rig.Host.Add(1, DensityGuestOs.Linux, "a", 12288, busy: true);
        await AssertAdmissionFails(rig.Admit(9, 4096), DensityAdmissionFailure.QueueTimeout);
        Assert.Equal(0, rig.Governor.Snapshot().Queued);
    }

    [Fact]
    public async Task Zero_queue_timeout_still_admits_when_there_is_room()
    {
        var rig = new Rig(o => o.QueueTimeout = TimeSpan.Zero);
        await using AdmissionTicket ticket = await rig.Admit(1, 4096);
    }

    [Fact]
    public async Task A_full_queue_rejects_the_next_request()
    {
        var rig = new Rig(o => o.MaxQueueLength = 2);
        AdmissionTicket holder = await rig.Admit(1, 12288);
        Task<AdmissionTicket> a = rig.Admit(2, 4096);
        Task<AdmissionTicket> b = rig.Admit(3, 4096);
        await Rig.WaitUntil(() => rig.Governor.Snapshot().Queued == 2, "two queued");

        await AssertAdmissionFails(rig.Admit(4, 4096), DensityAdmissionFailure.QueueFull);
        Assert.Equal(2, rig.Governor.Snapshot().Queued);

        await holder.DisposeAsync();
        await (await rig.Pump(a)).DisposeAsync();
        await (await rig.Pump(b)).DisposeAsync();
    }

    [Fact]
    public async Task A_vm_too_large_for_the_host_is_rejected()
    {
        var rig = new Rig();
        await AssertAdmissionFails(rig.Admit(1, 12289), DensityAdmissionFailure.TooLargeForHost);
        var snap = rig.Governor.Snapshot();
        Assert.Equal(0, snap.Queued);
        Assert.Equal(0, snap.ReservedMb);
    }

    [Fact]
    public async Task A_vm_exactly_total_minus_reserve_is_admitted()
    {
        var rig = new Rig();
        await using AdmissionTicket ticket = await rig.Admit(1, 12288);
    }

    [Fact]
    public async Task Cancelling_a_queued_request_removes_it()
    {
        var rig = new Rig();
        rig.Host.Add(1, DensityGuestOs.Linux, "a", 12288, busy: true);
        using var cts = new CancellationTokenSource();
        Task<AdmissionTicket> waiting = rig.Admit(9, 4096, ct: cts.Token);
        await Rig.WaitUntil(() => rig.HasEvent(DensityEventKinds.Queued), "queued");

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        var snap = rig.Governor.Snapshot();
        Assert.Equal(0, snap.Queued);
        Assert.Equal(0, snap.ReservedMb);
    }

    [Fact]
    public async Task A_cancelled_head_lets_the_next_request_through()
    {
        var rig = new Rig();
        AdmissionTicket holder = await rig.Admit(1, 8192);
        using var cts = new CancellationTokenSource();
        Task<AdmissionTicket> head = rig.Admit(2, 12288, ct: cts.Token);
        await Rig.WaitUntil(() => rig.EventCount(DensityEventKinds.Queued) == 1, "head queued");
        Task<AdmissionTicket> next = rig.Admit(3, 2048);
        await Rig.WaitUntil(() => rig.Governor.Snapshot().Queued == 2, "next queued");

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => head);
        await (await rig.Pump(next)).DisposeAsync();
        await holder.DisposeAsync();
    }

    [Fact]
    public async Task Cancelling_while_a_save_is_in_flight_leaves_no_reservation()
    {
        var rig = new Rig();
        rig.Host.Add(1, DensityGuestOs.Windows, "a", 4096);
        rig.Host.Add(2, DensityGuestOs.Linux, "b", 4096, busy: true);
        rig.Host.Add(3, DensityGuestOs.Linux, "c", 4096, busy: true);
        rig.Host.Hold(1);
        using var cts = new CancellationTokenSource();
        Task<AdmissionTicket> admit = rig.Admit(9, 4096, ct: cts.Token);
        await Rig.WaitUntil(() => rig.Host.SaveCalls.Count == 1, "save started");
        Assert.Equal(4096, rig.Governor.Snapshot().ReservedMb);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => admit);
        Assert.Equal(0, rig.Governor.Snapshot().ReservedMb);
        Assert.Equal(0, rig.Governor.Snapshot().Queued);

        rig.Host.Release(1); // the save finishes anyway; it is not repeated
        await Rig.WaitUntil(() => rig.HasEvent(DensityEventKinds.SavedForRoom), "save finished");
        await using AdmissionTicket again = await rig.Pump(rig.Admit(10, 4096));
        Assert.Single(rig.Host.SaveCalls);
    }

    [Fact]
    public async Task Timing_out_while_a_save_is_in_flight_leaves_no_reservation()
    {
        var rig = new Rig(o => o.QueueTimeout = TimeSpan.FromMinutes(1));
        rig.Host.Add(1, DensityGuestOs.Windows, "a", 4096);
        rig.Host.Add(2, DensityGuestOs.Linux, "b", 4096, busy: true);
        rig.Host.Add(3, DensityGuestOs.Linux, "c", 4096, busy: true);
        rig.Host.Hold(1);
        Task<AdmissionTicket> admit = rig.Admit(9, 4096);
        await Rig.WaitUntil(() => rig.Host.SaveCalls.Count == 1, "save started");

        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await AssertAdmissionFails(admit, DensityAdmissionFailure.QueueTimeout);
        Assert.Equal(0, rig.Governor.Snapshot().ReservedMb);
        rig.Host.Release(1);
    }

    [Fact]
    public async Task A_failed_save_is_recorded_and_the_next_victim_is_used()
    {
        var rig = new Rig();
        rig.Host.Add(1, DensityGuestOs.Windows, "a", 4096, idleMinutes: 50);
        rig.Host.Add(2, DensityGuestOs.Linux, "b", 4096, idleMinutes: 40);
        rig.Host.Add(3, DensityGuestOs.Linux, "c", 4096, busy: true);
        rig.Host.FailSave(1);

        await using AdmissionTicket ticket = await rig.Pump(rig.Admit(9, 4096));
        Assert.Equal([FakeHost.Id(1), FakeHost.Id(2)], rig.Host.SaveCalls);
        Assert.Equal(1, rig.EventCount(DensityEventKinds.SaveFailed));
        Assert.Equal(1, rig.EventCount(DensityEventKinds.SavedForRoom));
    }

    [Fact]
    public async Task A_failed_victim_is_not_retried_and_the_request_waits()
    {
        var rig = new Rig();
        rig.Host.Add(1, DensityGuestOs.Windows, "a", 4096);
        rig.Host.Add(2, DensityGuestOs.Linux, "b", 4096, busy: true);
        rig.Host.Add(3, DensityGuestOs.Linux, "c", 4096, busy: true);
        rig.Host.FailSave(1);

        Task<AdmissionTicket> admit = rig.Admit(9, 4096);
        await Rig.WaitUntil(() => rig.HasEvent(DensityEventKinds.Queued), "queued");
        rig.Time.Advance(TimeSpan.FromSeconds(6));
        await Task.Delay(30, TestContext.Current.CancellationToken);
        Assert.Single(rig.Host.SaveCalls);
        Assert.False(admit.IsCompleted);

        rig.Host.Remove(2); // a busy VM finishes
        rig.Governor.NotifyMemoryChanged();
        await (await rig.Pump(admit)).DisposeAsync();
    }

    [Fact]
    public async Task A_saved_vm_still_listed_by_a_stale_directory_is_not_saved_again()
    {
        var rig = new Rig();
        rig.Host.KeepListedAfterSave = true;
        rig.Host.Add(1, DensityGuestOs.Windows, "a", 4096);
        rig.Host.Add(2, DensityGuestOs.Linux, "b", 4096, busy: true);
        rig.Host.Add(3, DensityGuestOs.Linux, "c", 4096, busy: true);

        AdmissionTicket first = await rig.Pump(rig.Admit(9, 4096));
        await first.ConfirmStartedAsync();
        rig.Host.Add(9, DensityGuestOs.Linux, "req", 4096, busy: true); // it booted, so memory is full again
        Task<AdmissionTicket> second = rig.Admit(10, 4096);
        await Rig.WaitUntil(() => rig.EventCount(DensityEventKinds.Queued) == 1, "second queued");
        Assert.Single(rig.Host.SaveCalls);
        Assert.False(second.IsCompleted);
        rig.Host.Remove(9);
        rig.Governor.NotifyMemoryChanged();
        await (await rig.Pump(second)).DisposeAsync();
    }

    [Fact]
    public async Task Waits_for_saved_memory_to_come_back_before_admitting()
    {
        var rig = new Rig();
        rig.Host.DelayMemoryReturn = true;
        rig.Host.Add(1, DensityGuestOs.Windows, "a", 4096);
        rig.Host.Add(2, DensityGuestOs.Linux, "b", 4096, busy: true);
        rig.Host.Add(3, DensityGuestOs.Linux, "c", 4096, busy: true);

        Task<AdmissionTicket> admit = rig.Admit(9, 4096);
        await Rig.WaitUntil(() => rig.HasEvent(DensityEventKinds.SavedForRoom), "saved");
        rig.Time.Advance(TimeSpan.FromMilliseconds(500));
        await Task.Delay(30, TestContext.Current.CancellationToken);
        Assert.False(admit.IsCompleted);

        rig.Host.ReturnMemory();
        await (await rig.Pump(admit, TimeSpan.FromMilliseconds(500))).DisposeAsync();
        Assert.Single(rig.Host.SaveCalls);
    }

    [Fact]
    public async Task Gives_up_settling_and_requeues_without_saving_more_than_needed()
    {
        var rig = new Rig(o => o.SaveSettleChecks = 2);
        rig.Host.DelayMemoryReturn = true;
        rig.Host.Add(1, DensityGuestOs.Windows, "a", 4096);
        rig.Host.Add(2, DensityGuestOs.Linux, "b", 4096, busy: true);
        rig.Host.Add(3, DensityGuestOs.Linux, "c", 4096, busy: true);

        Task<AdmissionTicket> admit = rig.Admit(9, 4096);
        await Rig.WaitUntil(() => rig.HasEvent(DensityEventKinds.SavedForRoom), "saved");
        await rig.PumpUntil(() => rig.Governor.Snapshot().ReservedMb == 0); // gave up settling: the reservation is not held while it waits again

        Assert.False(admit.IsCompleted);
        Assert.Equal(1, rig.Governor.Snapshot().Queued);

        rig.Host.ReturnMemory();
        rig.Governor.NotifyMemoryChanged();
        await (await rig.Pump(admit)).DisposeAsync();
        Assert.Single(rig.Host.SaveCalls);
    }

    [Fact]
    public async Task Disabled_governor_admits_everything_untracked()
    {
        var rig = new Rig(o => o.Enabled = false);
        rig.Host.Add(1, DensityGuestOs.Linux, "a", 16384, busy: true);
        await using AdmissionTicket ticket = await rig.Admit(9, 99_999);
        await ticket.ConfirmStartedAsync();
        Assert.Equal(0, rig.Governor.Snapshot().ReservedMb);
        Assert.Empty(rig.Host.SaveCalls);
    }

    [Fact]
    public async Task Governor_without_a_probe_admits_everything()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var governor = new DensityGovernor(Microsoft.Extensions.Options.Options.Create(new VmDensityOptions()), time);
        Assert.False(governor.IsActive);
        await using AdmissionTicket ticket = await governor.AdmitAsync(Rig.Vm(1, 1_000_000), CancellationToken.None);
        Assert.Equal(0, governor.Snapshot().Queued);
    }

    [Fact]
    public async Task A_failing_probe_admits_untracked_and_records_it()
    {
        var rig = new Rig();
        rig.Host.ProbeFailure = new InvalidOperationException("probe down");
        await using AdmissionTicket ticket = await rig.Admit(1, 4096);
        Assert.Equal(0, rig.Governor.Snapshot().ReservedMb);
        Assert.Equal(1, rig.EventCount(DensityEventKinds.ProbeFailed));
        Assert.Equal(0, rig.Governor.Snapshot().Queued);
    }

    [Fact]
    public async Task Max_running_saves_an_idle_vm_even_with_free_memory()
    {
        var rig = new Rig(o => o.MaxRunning = 2, totalMb: 65536);
        rig.Host.Add(1, DensityGuestOs.Windows, "a", 1024);
        rig.Host.Add(2, DensityGuestOs.Linux, "b", 1024);
        await using AdmissionTicket ticket = await rig.Pump(rig.Admit(9, 1024));
        Assert.Equal([FakeHost.Id(1)], rig.Host.SaveCalls);
    }

    [Fact]
    public async Task Does_not_save_a_vm_idle_for_less_than_the_minimum()
    {
        var rig = new Rig();
        rig.Host.Add(1, DensityGuestOs.Windows, "a", 4096, idleMinutes: 1);
        rig.Host.Add(2, DensityGuestOs.Linux, "b", 4096, busy: true);
        rig.Host.Add(3, DensityGuestOs.Linux, "c", 4096, busy: true);
        Task<AdmissionTicket> admit = rig.Admit(9, 4096);
        await Rig.WaitUntil(() => rig.HasEvent(DensityEventKinds.Queued), "queued");
        Assert.Empty(rig.Host.SaveCalls);

        rig.Time.Advance(TimeSpan.FromMinutes(1)); // now idle for two minutes
        await (await rig.Pump(admit, TimeSpan.FromSeconds(5))).DisposeAsync();
        Assert.Equal([FakeHost.Id(1)], rig.Host.SaveCalls);
    }

    [Fact]
    public async Task Snapshot_reports_counts_and_event_order()
    {
        var rig = new Rig();
        rig.Host.Add(1, DensityGuestOs.Linux, "a", 8192, busy: true);
        AdmissionTicket first = await rig.Admit(2, 4096);
        Task<AdmissionTicket> waiting = rig.Admit(3, 4096);
        await Rig.WaitUntil(() => rig.HasEvent(DensityEventKinds.Queued), "queued");

        var queuedSnap = rig.Governor.Snapshot();
        Assert.Equal(1, queuedSnap.Queued);
        Assert.Equal(4096, queuedSnap.ReservedMb);
        Assert.Equal(1, queuedSnap.Running);
        Assert.Equal(16384 - 8192, queuedSnap.HostFreeMb);

        await first.DisposeAsync();
        AdmissionTicket second = await rig.Pump(waiting);
        string[] kinds = [.. rig.Governor.Snapshot().RecentEvents.Select(e => e.Kind)];
        Assert.Equal([DensityEventKinds.Admitted, DensityEventKinds.Queued, DensityEventKinds.Dequeued, DensityEventKinds.Admitted], kinds);
        await second.DisposeAsync();
    }

    [Fact]
    public async Task The_event_ring_keeps_only_recent_events()
    {
        var rig = new Rig();
        for (int i = 0; i < 150; i++)
            await (await rig.Admit(i, 1024)).DisposeAsync();
        var events = rig.Governor.Snapshot().RecentEvents;
        Assert.Equal(100, events.Count);
        Assert.Equal(FakeHost.Id(149), events[^1].VmId);
    }

    [Fact]
    public async Task Disposing_the_governor_cancels_waiting_requests()
    {
        var rig = new Rig();
        rig.Host.Add(1, DensityGuestOs.Linux, "a", 12288, busy: true);
        Task<AdmissionTicket> waiting = rig.Admit(9, 4096);
        await Rig.WaitUntil(() => rig.HasEvent(DensityEventKinds.Queued), "queued");

        await rig.Governor.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, rig.Governor.Snapshot().Queued);
    }

    [Fact]
    public async Task Cancelled_token_before_the_call_throws_without_queueing()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Admit(1, 1024, ct: cts.Token));
        Assert.Equal(0, rig.Governor.Snapshot().Queued);
    }

    [Fact]
    public async Task Readmitting_a_saved_vm_clears_its_cooldown()
    {
        var rig = new Rig();
        rig.Host.Add(1, DensityGuestOs.Windows, "a", 4096);
        rig.Host.Add(2, DensityGuestOs.Linux, "b", 4096, busy: true);
        rig.Host.Add(3, DensityGuestOs.Linux, "c", 4096, busy: true);
        AdmissionTicket t = await rig.Pump(rig.Admit(9, 4096));
        await t.DisposeAsync();

        // VM 1 starts again (it was saved earlier), and later becomes an idle victim once more.
        await (await rig.Admit(1, 4096, "a")).DisposeAsync();
        rig.Host.Add(1, DensityGuestOs.Windows, "a", 4096);
        AdmissionTicket again = await rig.Pump(rig.Admit(10, 4096));
        Assert.Equal([FakeHost.Id(1), FakeHost.Id(1)], rig.Host.SaveCalls);
        await again.DisposeAsync();
    }
}
