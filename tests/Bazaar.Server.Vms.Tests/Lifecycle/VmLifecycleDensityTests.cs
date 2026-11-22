using Bazaar.Server.VmDensity.Abstractions;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.Tests.Lifecycle;

/// <summary>A governor that records what it was asked and can refuse or block, for the lifecycle's admission path.</summary>
internal sealed class FakeGovernor : IDensityGovernor
{
    private readonly Lock _sync = new();

    public List<DensityVm> Admitted { get; } = [];

    public List<FakeTicket> Tickets { get; } = [];

    public int MemoryChangedCount { get; private set; }

    /// <summary>When set, the next admission throws this.</summary>
    public DensityAdmissionFailure? Refuse { get; set; }

    /// <summary>Runs inside each admission, before the ticket is handed out.</summary>
    public Func<DensityVm, Task>? Hook { get; set; }

    public async Task<AdmissionTicket> AdmitAsync(DensityVm vm, CancellationToken cancellationToken)
    {
        lock (_sync)
            Admitted.Add(vm);
        if (Hook is { } hook)
            await hook(vm).ConfigureAwait(false);
        if (Refuse is { } reason)
            throw new DensityAdmissionException(reason, "no room");
        var ticket = new FakeTicket();
        lock (_sync)
            Tickets.Add(ticket);
        return ticket;
    }

    public DensitySnapshot Snapshot() => new(0, 0, 0, 0, []);

    public void NotifyMemoryChanged()
    {
        lock (_sync)
            MemoryChangedCount++;
    }

    internal sealed class FakeTicket : AdmissionTicket
    {
        public bool Confirmed { get; private set; }

        public bool Disposed { get; private set; }

        public override Task ConfirmStartedAsync()
        {
            Confirmed = true;
            return Task.CompletedTask;
        }

        public override ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>The live device limits, settable by a test.</summary>
internal sealed class FakeDevicePolicy(int maxConcurrent, int idleStopMinutes = 10) : IDeviceGuestPolicy
{
    public int MaxConcurrent { get; set; } = maxConcurrent;

    public int IdleStopMinutes { get; set; } = idleStopMinutes;

    public ValueTask<int> GetMaxConcurrentAsync(CancellationToken ct) => ValueTask.FromResult(MaxConcurrent);

    public ValueTask<int> GetIdleStopMinutesAsync(CancellationToken ct) => ValueTask.FromResult(IdleStopMinutes);
}

/// <summary>Admission by the density governor and the aggregate device-guest cap at start (doc 62 section 9.3).</summary>
public sealed class VmLifecycleDensityTests
{
    private static readonly DateTimeOffset _epoch = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeEnvironmentVmStore _store = new();
    private readonly FakeSource _pair = new("hyperv", VmProviderPlacement.Pair, VmBackendOrdering.PrimaryHypervisor, VmOs.Windows, VmOs.Linux);
    private readonly FakeSource _android = new("android", VmProviderPlacement.OnDemand, VmBackendOrdering.OnDemand, VmOs.Android);
    private readonly FakeSource _microVm = new("microvm", VmProviderPlacement.OnDemand, VmBackendOrdering.OnDemand, VmOs.Linux);
    private readonly FakeGovernor _governor = new();
    private int _nextRef;

    private VmLifecycleService Service(IDensityGovernor? governor = null, IDeviceGuestPolicy? policy = null, IVmActivitySource? activity = null) =>
        new(_store, new VmBackendRouter([_pair, _android, _microVm], new VmPairSelection("hyperv"), new FakeGate("android", "microvm")),
            new VmOperationLocks(), TimeProvider.System, governor, policy, activity);

    private EnvironmentVmRecord Seed(VmState state, VmOs os, string kind, int memoryMb = 2048)
    {
        var vm = new EnvironmentVmRecord(
            Guid.CreateVersion7(), Guid.CreateVersion7(), os, Guid.CreateVersion7(), $"ref-{Interlocked.Increment(ref _nextRef)}", state, 2, memoryMb, 30,
            _epoch, _epoch, kind);
        _store.Seed(vm);
        return vm;
    }

    [Fact]
    public async Task A_start_is_admitted_before_the_backend_call_and_confirmed_after_it()
    {
        var vm = Seed(VmState.Stopped, VmOs.Android, "android", memoryMb: 3072);
        _governor.Hook = _ =>
        {
            Assert.Empty(_android.Backend!.Calls);
            return Task.CompletedTask;
        };

        var result = await Service(_governor).StartAsync(vm.Id, Ct);

        Assert.IsType<VmOperationResult.Applied>(result);
        var admitted = Assert.Single(_governor.Admitted);
        Assert.Equal(new DensityVm(vm.Id, vm.AgentId.ToString(), DensityGuestOs.Android, 3072), admitted);
        Assert.True(Assert.Single(_governor.Tickets).Confirmed);
        Assert.Equal(["Start"], _android.Backend!.Calls.Select(c => c.Operation));
    }

    [Fact]
    public async Task A_microvm_guest_is_admitted_as_a_linux_guest()
    {
        var vm = Seed(VmState.Stopped, VmOs.Linux, "microvm");

        await Service(_governor).StartAsync(vm.Id, Ct);

        Assert.Equal(DensityGuestOs.Linux, Assert.Single(_governor.Admitted).Os);
    }

    [Fact]
    public async Task A_failed_backend_start_releases_the_ticket_without_confirming_it()
    {
        var vm = Seed(VmState.Stopped, VmOs.Linux, "hyperv");
        _pair.Backend!.Hook = (_, _, _) => throw new InvalidOperationException("boom");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(_governor).StartAsync(vm.Id, Ct));

        var ticket = Assert.Single(_governor.Tickets);
        Assert.False(ticket.Confirmed);
        Assert.True(ticket.Disposed);
        Assert.Equal(VmState.Stopped, _store.StateOf(vm.Id));
    }

    [Fact]
    public async Task A_state_change_after_admission_releases_the_ticket()
    {
        var vm = Seed(VmState.Stopped, VmOs.Linux, "hyperv");
        _governor.Hook = _ =>
        {
            _store.ForceState(vm.Id, VmState.Retired);
            return Task.CompletedTask;
        };

        var result = await Service(_governor).StartAsync(vm.Id, Ct);

        Assert.Equal(VmRefusalReason.ForbiddenTransition, Assert.IsType<VmOperationResult.Refused>(result).Refusal.Reason);
        var ticket = Assert.Single(_governor.Tickets);
        Assert.False(ticket.Confirmed);
        Assert.True(ticket.Disposed);
        Assert.Empty(_pair.Backend!.Calls);
    }

    [Theory]
    [InlineData(DensityAdmissionFailure.QueueTimeout)]
    [InlineData(DensityAdmissionFailure.QueueFull)]
    [InlineData(DensityAdmissionFailure.TooLargeForHost)]
    public async Task An_admission_failure_is_a_capacity_refusal_and_no_backend_is_called(DensityAdmissionFailure failure)
    {
        var vm = Seed(VmState.Saved, VmOs.Windows, "hyperv");
        _governor.Refuse = failure;

        var result = await Service(_governor).StartAsync(vm.Id, Ct);

        var refusal = Assert.IsType<VmOperationResult.Refused>(result).Refusal;
        Assert.Equal(VmRefusalReason.Capacity, refusal.Reason);
        Assert.Equal(VmState.Saved, refusal.CurrentState);
        Assert.Contains(failure.ToString(), refusal.Detail, StringComparison.Ordinal);
        Assert.Empty(_pair.Backend!.Calls);
        Assert.Equal(VmState.Saved, _store.StateOf(vm.Id));
    }

    [Fact]
    public async Task An_unknown_vm_or_a_forbidden_start_never_reaches_the_governor()
    {
        var running = Seed(VmState.Running, VmOs.Linux, "hyperv");
        var service = Service(_governor);

        var unknown = await service.StartAsync(Guid.CreateVersion7(), Ct);
        var forbidden = await service.StartAsync(running.Id, Ct);

        Assert.Equal(VmRefusalReason.UnknownVm, Assert.IsType<VmOperationResult.Refused>(unknown).Refusal.Reason);
        Assert.Equal(VmRefusalReason.ForbiddenTransition, Assert.IsType<VmOperationResult.Refused>(forbidden).Refusal.Reason);
        Assert.Empty(_governor.Admitted);
    }

    [Fact]
    public async Task Without_a_governor_or_a_device_policy_a_start_goes_straight_to_the_backend()
    {
        var vm = Seed(VmState.Stopped, VmOs.Android, "android");

        Assert.IsType<VmOperationResult.Applied>(await Service().StartAsync(vm.Id, Ct));
        Assert.Equal(["Start"], _android.Backend!.Calls.Select(c => c.Operation));
    }

    [Fact]
    public async Task Stop_save_and_retire_tell_the_governor_memory_changed()
    {
        var a = Seed(VmState.Running, VmOs.Linux, "hyperv");
        var b = Seed(VmState.Running, VmOs.Linux, "hyperv");
        var service = Service(_governor);

        await service.StopAsync(a.Id, StopMode.Graceful, Ct);
        await service.SaveAsync(b.Id, Ct);
        await service.RetireAsync(a.Id, Ct);
        await service.StopAsync(Guid.CreateVersion7(), StopMode.Graceful, Ct); // refused: no notification

        Assert.Equal(3, _governor.MemoryChangedCount);
    }

    [Fact]
    public async Task The_device_cap_counts_running_guests_of_every_device_provider_together()
    {
        Seed(VmState.Running, VmOs.Android, "android");
        Seed(VmState.Running, VmOs.Linux, "microvm");
        var next = Seed(VmState.Stopped, VmOs.Android, "android");

        var result = await Service(policy: new FakeDevicePolicy(2)).StartAsync(next.Id, Ct);

        var refusal = Assert.IsType<VmOperationResult.Refused>(result).Refusal;
        Assert.Equal(VmRefusalReason.Capacity, refusal.Reason);
        Assert.Contains("vms.devices.max_concurrent", refusal.Detail, StringComparison.Ordinal);
        Assert.Empty(_android.Backend!.Calls);
        Assert.Equal(VmState.Stopped, _store.StateOf(next.Id));
    }

    [Fact]
    public async Task Pair_vms_are_never_counted_or_capped()
    {
        Seed(VmState.Running, VmOs.Windows, "hyperv");
        Seed(VmState.Running, VmOs.Linux, "hyperv");
        var device = Seed(VmState.Stopped, VmOs.Android, "android");
        var pair = Seed(VmState.Stopped, VmOs.Linux, "hyperv");
        var service = Service(policy: new FakeDevicePolicy(1));

        Assert.IsType<VmOperationResult.Applied>(await service.StartAsync(device.Id, Ct));
        Assert.IsType<VmOperationResult.Applied>(await service.StartAsync(pair.Id, Ct));
    }

    [Fact]
    public async Task A_cap_change_applies_to_the_next_start_and_lowering_it_stops_nothing()
    {
        var running = Seed(VmState.Running, VmOs.Android, "android");
        var next = Seed(VmState.Stopped, VmOs.Linux, "microvm");
        var policy = new FakeDevicePolicy(1);
        var service = Service(policy: policy);

        Assert.IsType<VmOperationResult.Refused>(await service.StartAsync(next.Id, Ct));
        policy.MaxConcurrent = 2;
        Assert.IsType<VmOperationResult.Applied>(await service.StartAsync(next.Id, Ct));
        policy.MaxConcurrent = 1;

        Assert.Equal(VmState.Running, _store.StateOf(running.Id));
        Assert.Equal(VmState.Running, _store.StateOf(next.Id));
        Assert.Empty(_android.Backend!.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_cap_of_zero_or_less_means_no_aggregate_cap(int cap)
    {
        Seed(VmState.Running, VmOs.Android, "android");
        Seed(VmState.Running, VmOs.Android, "android");
        var next = Seed(VmState.Stopped, VmOs.Android, "android");

        Assert.IsType<VmOperationResult.Applied>(await Service(policy: new FakeDevicePolicy(cap)).StartAsync(next.Id, Ct));
    }

    [Fact]
    public async Task Two_concurrent_starts_cannot_both_take_the_last_slot()
    {
        var a = Seed(VmState.Stopped, VmOs.Android, "android");
        var b = Seed(VmState.Stopped, VmOs.Linux, "microvm");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _android.Backend!.Hook = async (_, _, _) =>
        {
            entered.SetResult();
            await release.Task;
        };
        var service = Service(policy: new FakeDevicePolicy(1));

        var first = service.StartAsync(a.Id, Ct);
        await entered.Task.WaitAsync(Ct);
        var second = await service.StartAsync(b.Id, Ct);
        release.SetResult();

        Assert.Equal(VmRefusalReason.Capacity, Assert.IsType<VmOperationResult.Refused>(second).Refusal.Reason);
        Assert.IsType<VmOperationResult.Applied>(await first);
        Assert.Empty(_microVm.Backend!.Calls);
    }

    [Fact]
    public async Task A_slot_is_released_when_the_governor_refuses_the_start()
    {
        var vm = Seed(VmState.Stopped, VmOs.Android, "android");
        var service = Service(_governor, new FakeDevicePolicy(1));
        _governor.Refuse = DensityAdmissionFailure.QueueTimeout;
        Assert.IsType<VmOperationResult.Refused>(await service.StartAsync(vm.Id, Ct));

        _governor.Refuse = null;

        Assert.IsType<VmOperationResult.Applied>(await service.StartAsync(vm.Id, Ct));
    }

    [Fact]
    public async Task Running_vms_are_listed_with_their_backend_save_support_and_default_activity()
    {
        _android.Backend!.SupportsSaveState = true;
        var android = Seed(VmState.Running, VmOs.Android, "android");
        var pair = Seed(VmState.Running, VmOs.Linux, "hyperv");
        Seed(VmState.Stopped, VmOs.Linux, "microvm");

        var running = await Service().ListRunningAsync(Ct);

        Assert.Equal(2, running.Count);
        var a = Assert.Single(running, r => r.Vm.Id == android.Id);
        Assert.True(a.IsDeviceGuest);
        Assert.True(a.CanSaveState);
        Assert.Equal("android", a.Kind);
        Assert.Equal(new VmActivity(false, false, false, _epoch), a.Activity);
        var p = Assert.Single(running, r => r.Vm.Id == pair.Id);
        Assert.False(p.IsDeviceGuest);
        Assert.False(p.CanSaveState);
    }

    [Fact]
    public async Task Activity_comes_from_the_source_and_last_use_is_never_before_the_last_start()
    {
        var busy = Seed(VmState.Running, VmOs.Android, "android");
        var stale = Seed(VmState.Running, VmOs.Android, "android");
        var source = new FakeActivity
        {
            [busy.Id] = new VmActivity(true, false, false, _epoch.AddMinutes(5)),
            [stale.Id] = new VmActivity(false, false, false, _epoch.AddMinutes(-30)),
        };

        var running = await Service(activity: source).ListRunningAsync(Ct);

        Assert.Equal(new VmActivity(true, false, false, _epoch.AddMinutes(5)), Assert.Single(running, r => r.Vm.Id == busy.Id).Activity);
        Assert.Equal(_epoch, Assert.Single(running, r => r.Vm.Id == stale.Id).Activity.LastUsedAt);
    }

    private sealed class FakeActivity : Dictionary<Guid, VmActivity>, IVmActivitySource
    {
        public ValueTask<VmActivity?> GetAsync(Guid vmId, CancellationToken ct) => ValueTask.FromResult(TryGetValue(vmId, out var a) ? a : null);
    }
}
