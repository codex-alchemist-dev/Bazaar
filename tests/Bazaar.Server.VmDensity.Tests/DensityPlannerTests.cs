using Bazaar.Server.VmDensity.Abstractions;

namespace Bazaar.Server.VmDensity.Tests;

public sealed class DensityPlannerTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static VmDensityOptions Opts(Action<VmDensityOptions>? configure = null)
    {
        var o = new VmDensityOptions { HostReserveMb = 1000, MinIdleBeforeSave = TimeSpan.FromMinutes(2), MaxSavesPerAdmission = 4 };
        configure?.Invoke(o);
        return o;
    }

    private static DensityVm Req(int mb = 1000, string agent = "req", int n = 900) =>
        new(FakeHost.Id(n), agent, DensityGuestOs.Linux, mb);

    private static RunningVm Run(
        int n,
        int mb = 1000,
        DensityGuestOs os = DensityGuestOs.Linux,
        string agent = "other",
        double idleMin = 30,
        bool busy = false,
        bool canSave = true) =>
        new(new DensityVm(FakeHost.Id(n), agent, os, mb), new VmActivity(busy, false, false, _now - TimeSpan.FromMinutes(idleMin)), canSave);

    private static AdmissionPlan Plan(
        DensityVm request,
        long totalMb,
        long freeMb,
        IReadOnlyList<RunningVm>? running = null,
        long reservedMb = 0,
        int reservedCount = 0,
        VmDensityOptions? options = null,
        long pendingFreeMb = 0,
        int pendingCount = 0) =>
        DensityPlanner.Plan(request, new HostMemory(totalMb, freeMb), reservedMb, reservedCount, running ?? [], options ?? Opts(), _now, pendingFreeMb, pendingCount);

    private static int[] VictimNumbers(AdmissionPlan plan) =>
        [.. Assert.IsType<AdmissionPlan.SaveThenAdmit>(plan).Victims.Select(v => (int)v.Vm.VmId.ToByteArray()[0] + (v.Vm.VmId.ToByteArray()[1] << 8))];

    [Fact]
    public void Admits_when_free_memory_covers_need_plus_reserve()
    {
        Assert.IsType<AdmissionPlan.Admit>(Plan(Req(1000), 10_000, 2000));
    }

    [Fact]
    public void Admits_at_exact_fit()
    {
        // available = 2000 - 1000 = 1000 == need
        Assert.IsType<AdmissionPlan.Admit>(Plan(Req(1000), 10_000, 2000));
    }

    [Fact]
    public void One_megabyte_short_needs_a_victim()
    {
        var plan = Plan(Req(1000), 10_000, 1999, [Run(1, 500)]);
        Assert.Equal([1], VictimNumbers(plan));
    }

    [Fact]
    public void Reservations_reduce_available_memory()
    {
        // 2000 free would fit, but 500 is reserved by another admission.
        var plan = Plan(Req(1000), 10_000, 2000, [Run(1, 500)], reservedMb: 500, reservedCount: 1);
        Assert.Equal([1], VictimNumbers(plan));
    }

    [Fact]
    public void Exact_shortfall_arithmetic_with_reservations()
    {
        // free 3000, reserve 1000, reserved 700 -> available 1300; need 2000 -> shortfall 700.
        var running = new[] { Run(1, 600), Run(2, 700) };
        var plan = Plan(Req(2000), 10_000, 3000, running, reservedMb: 700, reservedCount: 1);
        Assert.Equal([1, 2], VictimNumbers(plan)); // 600 < 700, so both, in LRU order (1 is older? equal idle: VmId)
    }

    [Fact]
    public void Shortfall_exactly_covered_by_one_victim()
    {
        var plan = Plan(Req(2000), 10_000, 3000, [Run(1, 700)], reservedMb: 700, reservedCount: 1);
        Assert.Equal([1], VictimNumbers(plan));
    }

    [Fact]
    public void Rejects_a_vm_larger_than_total_minus_reserve()
    {
        var plan = Plan(Req(9001), 10_000, 10_000);
        Assert.Equal(DensityAdmissionFailure.TooLargeForHost, Assert.IsType<AdmissionPlan.Reject>(plan).Reason);
    }

    [Fact]
    public void Does_not_reject_a_vm_exactly_total_minus_reserve()
    {
        Assert.IsType<AdmissionPlan.Admit>(Plan(Req(9000), 10_000, 10_000));
    }

    [Fact]
    public void Idle_windows_vms_go_before_idle_linux_vms()
    {
        var running = new[] { Run(1, 1000, DensityGuestOs.Linux, idleMin: 100), Run(2, 1000, DensityGuestOs.Windows, idleMin: 5) };
        var plan = Plan(Req(1000), 10_000, 1000, running);
        Assert.Equal([2], VictimNumbers(plan));
    }

    [Fact]
    public void Least_recently_used_goes_first_within_a_group()
    {
        var running = new[] { Run(1, 1000, idleMin: 10), Run(2, 1000, idleMin: 50), Run(3, 1000, idleMin: 30) };
        var plan = Plan(Req(2000), 10_000, 1000, running);
        Assert.Equal([2, 3], VictimNumbers(plan));
    }

    [Fact]
    public void Ties_are_broken_by_vm_id()
    {
        var running = new[] { Run(5, 1000, idleMin: 30), Run(3, 1000, idleMin: 30), Run(4, 1000, idleMin: 30) };
        var plan = Plan(Req(2000), 10_000, 1000, running);
        Assert.Equal([3, 4], VictimNumbers(plan));
    }

    [Fact]
    public void Order_is_stable_regardless_of_input_order()
    {
        var a = new[] { Run(1, 1000, DensityGuestOs.Windows, idleMin: 10), Run(2, 1000, DensityGuestOs.Windows, idleMin: 20), Run(3) };
        var plan1 = Plan(Req(2000), 10_000, 1000, a);
        var plan2 = Plan(Req(2000), 10_000, 1000, [.. a.Reverse()]);
        Assert.Equal(VictimNumbers(plan1), VictimNumbers(plan2));
        Assert.Equal([2, 1], VictimNumbers(plan1));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Busy_vms_are_never_victims(bool exec, bool terminal, bool browser)
    {
        var busy = new RunningVm(
            new DensityVm(FakeHost.Id(1), "other", DensityGuestOs.Windows, 2000),
            new VmActivity(exec, terminal, browser, _now - TimeSpan.FromHours(5)),
            true);
        Assert.IsType<AdmissionPlan.Wait>(Plan(Req(1000), 10_000, 500, [busy]));
    }

    [Fact]
    public void Busy_vm_is_skipped_for_an_idle_one()
    {
        var running = new[] { Run(1, 1000, DensityGuestOs.Windows, busy: true), Run(2, 1000, DensityGuestOs.Linux) };
        Assert.Equal([2], VictimNumbers(Plan(Req(1000), 10_000, 1000, running)));
    }

    [Fact]
    public void Vms_that_cannot_save_state_are_never_victims()
    {
        var running = new[] { Run(1, 2000, DensityGuestOs.Windows, canSave: false) };
        Assert.IsType<AdmissionPlan.Wait>(Plan(Req(1000), 10_000, 500, running));
    }

    [Fact]
    public void Vm_idle_exactly_the_minimum_qualifies()
    {
        var running = new[] { Run(1, 2000, idleMin: 2) };
        Assert.Equal([1], VictimNumbers(Plan(Req(1000), 10_000, 500, running)));
    }

    [Fact]
    public void Vm_idle_less_than_the_minimum_does_not_qualify()
    {
        var running = new[] { Run(1, 2000, idleMin: 1.99) };
        Assert.IsType<AdmissionPlan.Wait>(Plan(Req(1000), 10_000, 500, running));
    }

    [Fact]
    public void Same_agent_idle_vm_is_a_last_resort_only()
    {
        var running = new[] { Run(1, 1000, DensityGuestOs.Windows, agent: "req"), Run(2, 1000, DensityGuestOs.Linux, agent: "other") };
        // One victim needed: the other agent's Linux VM goes even though the requester's Windows VM sorts first.
        Assert.Equal([2], VictimNumbers(Plan(Req(1000), 10_000, 1000, running)));
    }

    [Fact]
    public void Same_agent_idle_vm_is_used_when_others_do_not_cover()
    {
        var running = new[] { Run(1, 1000, DensityGuestOs.Windows, agent: "req"), Run(2, 1000, DensityGuestOs.Linux, agent: "other") };
        Assert.Equal([2, 1], VictimNumbers(Plan(Req(2000), 10_000, 1000, running)));
    }

    [Fact]
    public void Same_agent_idle_vm_is_used_when_it_is_the_only_candidate()
    {
        var running = new[] { Run(1, 1000, agent: "req") };
        Assert.Equal([1], VictimNumbers(Plan(Req(1000), 10_000, 1000, running)));
    }

    [Fact]
    public void Same_agent_busy_vm_is_never_saved()
    {
        var running = new[] { Run(1, 2000, agent: "req", busy: true) };
        Assert.IsType<AdmissionPlan.Wait>(Plan(Req(1000), 10_000, 500, running));
    }

    [Fact]
    public void Picks_the_fewest_victims_in_order()
    {
        // The first victim already covers the shortfall: the second is not taken.
        var running = new[] { Run(1, 3000, idleMin: 90), Run(2, 3000, idleMin: 80) };
        Assert.Equal([1], VictimNumbers(Plan(Req(2000), 10_000, 1000, running)));
    }

    [Fact]
    public void Takes_several_victims_when_one_is_not_enough()
    {
        var running = new[] { Run(1, 500, idleMin: 90), Run(2, 500, idleMin: 80), Run(3, 500, idleMin: 70), Run(4, 500, idleMin: 60) };
        Assert.Equal([1, 2, 3], VictimNumbers(Plan(Req(1500), 10_000, 1000, running)));
    }

    [Fact]
    public void Queues_when_more_than_max_saves_would_be_needed()
    {
        var running = Enumerable.Range(1, 5).Select(i => Run(i, 500, idleMin: 100 - i)).ToList();
        var plan = Plan(Req(2500), 10_000, 1000, running, options: Opts(o => o.MaxSavesPerAdmission = 4));
        Assert.IsType<AdmissionPlan.Wait>(plan);
    }

    [Fact]
    public void Exactly_max_saves_is_allowed()
    {
        var running = Enumerable.Range(1, 5).Select(i => Run(i, 500, idleMin: 100 - i)).ToList();
        var plan = Plan(Req(2000), 10_000, 1000, running, options: Opts(o => o.MaxSavesPerAdmission = 4));
        Assert.Equal(4, Assert.IsType<AdmissionPlan.SaveThenAdmit>(plan).Victims.Count);
    }

    [Fact]
    public void Queues_when_all_candidates_together_cannot_cover()
    {
        var running = new[] { Run(1, 300), Run(2, 300) };
        Assert.IsType<AdmissionPlan.Wait>(Plan(Req(2000), 10_000, 1000, running));
    }

    [Fact]
    public void Max_running_forces_a_victim_even_when_memory_suffices()
    {
        var running = new[] { Run(1, 100), Run(2, 100) };
        var plan = Plan(Req(100), 100_000, 90_000, running, options: Opts(o => o.MaxRunning = 2));
        Assert.Equal([1], VictimNumbers(plan));
    }

    [Fact]
    public void Max_running_admits_below_the_cap()
    {
        var running = new[] { Run(1, 100) };
        Assert.IsType<AdmissionPlan.Admit>(Plan(Req(100), 100_000, 90_000, running, options: Opts(o => o.MaxRunning = 2)));
    }

    [Fact]
    public void Max_running_counts_reservations()
    {
        var running = new[] { Run(1, 100) };
        var plan = Plan(Req(100), 100_000, 90_000, running, reservedMb: 100, reservedCount: 1, options: Opts(o => o.MaxRunning = 2));
        Assert.Equal([1], VictimNumbers(plan));
    }

    [Fact]
    public void Max_running_queues_when_nothing_can_be_saved()
    {
        var running = new[] { Run(1, 100, busy: true), Run(2, 100, busy: true) };
        Assert.IsType<AdmissionPlan.Wait>(Plan(Req(100), 100_000, 90_000, running, options: Opts(o => o.MaxRunning = 2)));
    }

    [Fact]
    public void Max_running_zero_means_no_cap()
    {
        var running = Enumerable.Range(1, 50).Select(i => Run(i, 10, busy: true)).ToList();
        Assert.IsType<AdmissionPlan.Admit>(Plan(Req(100), 100_000, 90_000, running, options: Opts(o => o.MaxRunning = 0)));
    }

    [Fact]
    public void Max_running_over_the_cap_needs_several_victims()
    {
        var running = new[] { Run(1, 100), Run(2, 100), Run(3, 100) };
        var plan = Plan(Req(100), 100_000, 90_000, running, options: Opts(o => o.MaxRunning = 2));
        Assert.Equal(2, Assert.IsType<AdmissionPlan.SaveThenAdmit>(plan).Victims.Count);
    }

    [Fact]
    public void Victims_cover_memory_and_count_together()
    {
        // Count needs one victim; memory shortfall (1200) needs more than the first small victim provides.
        var running = new[] { Run(1, 100, idleMin: 90), Run(2, 1500, idleMin: 80) };
        var plan = Plan(Req(2000), 100_000, 1800, running, options: Opts(o => o.MaxRunning = 2));
        Assert.Equal([1, 2], VictimNumbers(plan));
    }

    [Fact]
    public void Requests_own_vm_is_never_a_victim()
    {
        var req = Req(1000, n: 7);
        var running = new[] { new RunningVm(req, new VmActivity(false, false, false, _now - TimeSpan.FromHours(1)), true) };
        Assert.IsType<AdmissionPlan.Wait>(Plan(req, 10_000, 500, running));
    }

    [Fact]
    public void Pending_saves_that_cover_the_shortfall_make_the_request_wait()
    {
        var running = new[] { Run(1, 1000) };
        Assert.IsType<AdmissionPlan.Wait>(Plan(Req(1000), 10_000, 1000, running, pendingFreeMb: 1000, pendingCount: 1));
    }

    [Fact]
    public void Pending_saves_that_partly_cover_reduce_the_victims()
    {
        var running = new[] { Run(1, 1000, idleMin: 90), Run(2, 1000, idleMin: 80) };
        var plan = Plan(Req(2000), 10_000, 1000, running, pendingFreeMb: 1000, pendingCount: 1);
        Assert.Equal([1], VictimNumbers(plan));
    }

    [Fact]
    public void Pending_saves_still_count_as_running_for_the_cap()
    {
        // Cap 2, one running visible, one save in flight: starting now would make three.
        var running = new[] { Run(1, 100, busy: true) };
        var plan = Plan(Req(100), 100_000, 90_000, running, options: Opts(o => o.MaxRunning = 2), pendingFreeMb: 100, pendingCount: 1);
        Assert.IsType<AdmissionPlan.Wait>(plan);
    }

    [Fact]
    public void Planner_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => DensityPlanner.Plan(null!, new HostMemory(1, 1), 0, 0, [], Opts(), _now));
        Assert.Throws<ArgumentNullException>(() => DensityPlanner.Plan(Req(), null!, 0, 0, [], Opts(), _now));
    }
}
