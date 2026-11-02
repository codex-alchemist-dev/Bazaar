using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.Tests.Lifecycle;

/// <summary>The lifecycle service against the fake store and backend: transitions, refusals, failures, locking and ordering.</summary>
public sealed class VmLifecycleServiceTests
{
    private static readonly DateTimeOffset _epoch = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] _stopThenStart = ["Stop", "Start"];

    public static TheoryData<VmState> PowerOffStates() => new() { VmState.Stopped, VmState.Saved, VmState.Restored };

    public static TheoryData<StopMode> StopModes() => new() { StopMode.Graceful, StopMode.Force };

    public static TheoryData<VmState> CheckpointableStates() => new() { VmState.Stopped, VmState.Saved };

    public static TheoryData<VmState> RetirableStates() => new() { VmState.Stopped, VmState.Saved, VmState.Checkpointed, VmState.Restored };

    public static TheoryData<VmState> RestoredNextStates() => new() { VmState.Running, VmState.Stopped };

    public static TheoryData<VmState, string> ForbiddenOperations() => new()
    {
        { VmState.Running, "Start" },
        { VmState.Absent, "Start" },
        { VmState.Provisioning, "Start" },
        { VmState.Checkpointed, "Start" },
        { VmState.Retired, "Start" },
        { VmState.Stopped, "Stop" },
        { VmState.Saved, "Stop" },
        { VmState.Checkpointed, "Stop" },
        { VmState.Retired, "Stop" },
        { VmState.Stopped, "Save" },
        { VmState.Saved, "Save" },
        { VmState.Restored, "Save" },
        { VmState.Retired, "Save" },
        { VmState.Stopped, "Restore" },
        { VmState.Running, "Restore" },
        { VmState.Retired, "Restore" },
        { VmState.Running, "Retire" },
        { VmState.Absent, "Retire" },
        { VmState.Retired, "Retire" },
    };

    [Fact]
    public async Task Create_provisions_and_ends_stopped_with_the_backend_ref_recorded()
    {
        var h = new LifecycleHarness();
        var vm = NewRecord(VmState.Absent);

        var result = await h.Service.CreateAsync(vm, TestContext.Current.CancellationToken);

        Assert.Equal(VmState.Stopped, Assert.IsType<VmOperationResult.Applied>(result).State);
        var row = await h.Store.GetAsync(vm.Id, TestContext.Current.CancellationToken);
        Assert.Equal(VmState.Stopped, row!.State);
        Assert.StartsWith("fake-child-", row.BackendRef, StringComparison.Ordinal);
        Assert.Equal(1, h.Backend.CallCount("Create"));
        Assert.Equal(0, h.Locks.ActiveCount);
    }

    [Fact]
    public async Task Create_refuses_a_record_that_is_not_absent_and_does_not_call_the_backend()
    {
        var h = new LifecycleHarness();
        var vm = NewRecord(VmState.Stopped);

        var result = await h.Service.CreateAsync(vm, TestContext.Current.CancellationToken);

        var refused = Assert.IsType<VmOperationResult.Refused>(result);
        Assert.Equal(VmRefusalReason.ForbiddenTransition, refused.Refusal.Reason);
        Assert.Empty(h.Backend.Calls);
        Assert.Null(h.Store.StateOf(vm.Id));
    }

    [Fact]
    public async Task Failed_create_returns_the_row_to_absent_and_rethrows()
    {
        var h = new LifecycleHarness();
        h.Backend.Hook = async (op, _, _) =>
        {
            if (op == "Create")
                throw new InvalidOperationException("image rejected");
            await Task.CompletedTask;
        };
        var vm = NewRecord(VmState.Absent);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.CreateAsync(vm, TestContext.Current.CancellationToken));

        Assert.Equal(VmState.Absent, h.Store.StateOf(vm.Id));
        Assert.Equal(0, h.Locks.ActiveCount);
    }

    [Theory]
    [MemberData(nameof(PowerOffStates))]
    public async Task Start_from_a_powered_off_state_runs_the_vm(VmState from)
    {
        var h = new LifecycleHarness();
        var vm = h.AddVm(from);

        var result = await h.Service.StartAsync(vm.Id, TestContext.Current.CancellationToken);

        Assert.Equal(VmState.Running, Assert.IsType<VmOperationResult.Applied>(result).State);
        Assert.Equal(VmState.Running, h.Store.StateOf(vm.Id));
        Assert.Equal(1, h.Backend.CallCount("Start"));
    }

    [Theory]
    [MemberData(nameof(StopModes))]
    public async Task Stop_from_running_stops_the_vm_in_either_mode(StopMode mode)
    {
        var h = new LifecycleHarness();
        var vm = h.AddVm(VmState.Running);

        var result = await h.Service.StopAsync(vm.Id, mode, TestContext.Current.CancellationToken);

        Assert.Equal(VmState.Stopped, Assert.IsType<VmOperationResult.Applied>(result).State);
        Assert.Equal(VmState.Stopped, h.Store.StateOf(vm.Id));
        Assert.Equal(1, h.Backend.CallCount("Stop"));
    }

    [Fact]
    public async Task Save_from_running_saves_the_vm()
    {
        var h = new LifecycleHarness();
        var vm = h.AddVm(VmState.Running);

        var result = await h.Service.SaveAsync(vm.Id, TestContext.Current.CancellationToken);

        Assert.Equal(VmState.Saved, Assert.IsType<VmOperationResult.Applied>(result).State);
        Assert.Equal(VmState.Saved, h.Store.StateOf(vm.Id));
    }

    [Theory]
    [MemberData(nameof(CheckpointableStates))]
    public async Task Checkpoint_from_stopped_or_saved_returns_the_backend_id(VmState from)
    {
        var h = new LifecycleHarness();
        var vm = h.AddVm(from);

        var result = await h.Service.CheckpointAsync(vm.Id, TestContext.Current.CancellationToken);

        var applied = Assert.IsType<VmCheckpointResult.Applied>(result);
        Assert.NotEqual(default, applied.Checkpoint);
        Assert.Equal(VmState.Checkpointed, h.Store.StateOf(vm.Id));
        Assert.Equal(1, h.Backend.CallCount("Checkpoint"));
    }

    [Fact]
    public async Task Restore_from_checkpointed_moves_to_restored()
    {
        var h = new LifecycleHarness();
        var vm = h.AddVm(VmState.Checkpointed);

        var result = await h.Service.RestoreAsync(vm.Id, new CheckpointId(Guid.CreateVersion7()), TestContext.Current.CancellationToken);

        Assert.Equal(VmState.Restored, Assert.IsType<VmOperationResult.Applied>(result).State);
        Assert.Equal(VmState.Restored, h.Store.StateOf(vm.Id));
        Assert.Equal(1, h.Backend.CallCount("Restore"));
    }

    [Theory]
    [MemberData(nameof(RestoredNextStates))]
    public async Task Restored_vm_can_be_started_or_stopped_by_the_backend_power_state(VmState next)
    {
        var h = new LifecycleHarness();
        var vm = h.AddVm(VmState.Restored);

        var result = next == VmState.Running
            ? await h.Service.StartAsync(vm.Id, TestContext.Current.CancellationToken)
            : await h.Service.StopAsync(vm.Id, StopMode.Graceful, TestContext.Current.CancellationToken);

        Assert.Equal(next, Assert.IsType<VmOperationResult.Applied>(result).State);
        Assert.Equal(next, h.Store.StateOf(vm.Id));
    }

    [Theory]
    [MemberData(nameof(RetirableStates))]
    public async Task Retire_from_any_non_running_state_retires_the_vm(VmState from)
    {
        var h = new LifecycleHarness();
        var vm = h.AddVm(from);

        var result = await h.Service.RetireAsync(vm.Id, TestContext.Current.CancellationToken);

        Assert.Equal(VmState.Retired, Assert.IsType<VmOperationResult.Applied>(result).State);
        Assert.Equal(VmState.Retired, h.Store.StateOf(vm.Id));
        Assert.Equal(1, h.Backend.CallCount("Retire"));
    }

    [Theory]
    [MemberData(nameof(ForbiddenOperations))]
    public async Task Forbidden_operation_is_refused_and_never_reaches_the_backend(VmState from, string operation)
    {
        var h = new LifecycleHarness();
        var vm = h.AddVm(from);

        var result = await InvokeAsync(h, operation, vm.Id);

        var refused = Assert.IsType<VmOperationResult.Refused>(result);
        Assert.Equal(VmRefusalReason.ForbiddenTransition, refused.Refusal.Reason);
        Assert.Equal(from, refused.Refusal.CurrentState);
        Assert.Empty(h.Backend.Calls);
        Assert.Equal(from, h.Store.StateOf(vm.Id));
    }

    [Fact]
    public async Task Checkpoint_while_running_is_refused_and_never_reaches_the_backend()
    {
        var h = new LifecycleHarness();
        var vm = h.AddVm(VmState.Running);

        var result = await h.Service.CheckpointAsync(vm.Id, TestContext.Current.CancellationToken);

        var refused = Assert.IsType<VmCheckpointResult.Refused>(result);
        Assert.Equal(VmRefusalReason.ForbiddenTransition, refused.Refusal.Reason);
        Assert.Equal(VmState.Running, refused.Refusal.CurrentState);
        Assert.Empty(h.Backend.Calls);
    }

    [Fact]
    public async Task Operation_on_an_unknown_vm_is_refused_as_unknown()
    {
        var h = new LifecycleHarness();

        var result = await h.Service.StartAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken);

        var refused = Assert.IsType<VmOperationResult.Refused>(result);
        Assert.Equal(VmRefusalReason.UnknownVm, refused.Refusal.Reason);
        Assert.Empty(h.Backend.Calls);
    }

    [Fact]
    public async Task Get_state_reads_the_recorded_state_and_null_for_unknown()
    {
        var h = new LifecycleHarness();
        var vm = h.AddVm(VmState.Saved);

        Assert.Equal(VmState.Saved, await h.Service.GetStateAsync(vm.Id, TestContext.Current.CancellationToken));
        Assert.Null(await h.Service.GetStateAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken));
        Assert.Empty(h.Backend.Calls);
    }

    [Fact]
    public async Task Backend_failure_during_stop_leaves_the_state_unchanged_and_rethrows()
    {
        var h = new LifecycleHarness();
        var vm = h.AddVm(VmState.Running);
        h.Backend.Hook = async (op, _, _) =>
        {
            if (op == "Stop")
                throw new InvalidOperationException("hypervisor refused the stop");
            await Task.CompletedTask;
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Service.StopAsync(vm.Id, StopMode.Force, TestContext.Current.CancellationToken));

        Assert.Equal("hypervisor refused the stop", ex.Message);
        Assert.Equal(VmState.Running, h.Store.StateOf(vm.Id));
        Assert.Equal(0, h.Locks.ActiveCount);
    }

    [Fact]
    public async Task State_changed_underneath_the_backend_call_is_refused_not_applied()
    {
        var h = new LifecycleHarness();
        var vm = h.AddVm(VmState.Running);
        h.Backend.Hook = async (op, _, _) =>
        {
            // Another writer moves the row while the backend is working.
            if (op == "Stop")
                h.Store.ForceState(vm.Id, VmState.Saved);
            await Task.CompletedTask;
        };

        var result = await h.Service.StopAsync(vm.Id, StopMode.Graceful, TestContext.Current.CancellationToken);

        var refused = Assert.IsType<VmOperationResult.Refused>(result);
        Assert.Equal(VmRefusalReason.StateChanged, refused.Refusal.Reason);
        Assert.Equal(VmState.Saved, refused.Refusal.CurrentState);
        Assert.Equal(VmState.Stopped, refused.Refusal.RequestedState);
        Assert.Equal(VmState.Saved, h.Store.StateOf(vm.Id));
        Assert.Equal(1, h.Backend.CallCount("Stop"));
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_the_lock_throws_and_does_not_call_the_backend()
    {
        var h = new LifecycleHarness();
        var vm = h.AddVm(VmState.Running);
        var release = new TaskCompletionSource<bool>();
        var stopEntered = new TaskCompletionSource<bool>();
        h.Backend.Hook = async (op, _, _) =>
        {
            if (op == "Stop")
            {
                stopEntered.TrySetResult(true);
                await release.Task;
            }
        };

        var stop = h.Service.StopAsync(vm.Id, StopMode.Graceful, TestContext.Current.CancellationToken);
        await stopEntered.Task;

        using var cts = new CancellationTokenSource();
        var start = h.Service.StartAsync(vm.Id, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => start);
        Assert.Equal(0, h.Backend.CallCount("Start"));

        release.SetResult(true);
        await stop;
        Assert.Equal(VmState.Stopped, h.Store.StateOf(vm.Id));
        Assert.Equal(0, h.Locks.ActiveCount);
    }

    [Fact]
    public async Task A_stop_in_flight_blocks_a_start_until_the_stop_completes()
    {
        var h = new LifecycleHarness();
        var vm = h.AddVm(VmState.Running);
        var release = new TaskCompletionSource<bool>();
        var stopEntered = new TaskCompletionSource<bool>();
        h.Backend.Hook = async (op, _, _) =>
        {
            if (op == "Stop")
            {
                stopEntered.TrySetResult(true);
                await release.Task;
            }
        };

        var stop = h.Service.StopAsync(vm.Id, StopMode.Graceful, TestContext.Current.CancellationToken);
        await stopEntered.Task;

        var start = h.Service.StartAsync(vm.Id, TestContext.Current.CancellationToken);
        Assert.False(start.IsCompleted);
        Assert.Equal(0, h.Backend.CallCount("Start"));

        release.SetResult(true);
        await stop;
        var startResult = await start;

        Assert.Equal(VmState.Running, Assert.IsType<VmOperationResult.Applied>(startResult).State);
        Assert.Equal(_stopThenStart, h.Backend.Calls.Select(c => c.Operation));
        Assert.Equal(0, h.Locks.ActiveCount);
    }

    [Fact]
    public async Task Two_operations_on_one_vm_never_overlap_and_the_second_sees_the_first_result()
    {
        var h = new LifecycleHarness();
        var vm = h.AddVm(VmState.Running);
        var release = new TaskCompletionSource<bool>();
        var stopEntered = new TaskCompletionSource<bool>();
        h.Backend.Hook = async (op, _, _) =>
        {
            if (op == "Stop")
            {
                stopEntered.TrySetResult(true);
                await release.Task;
            }
        };

        var first = h.Service.StopAsync(vm.Id, StopMode.Graceful, TestContext.Current.CancellationToken);
        await stopEntered.Task;
        var second = h.Service.StopAsync(vm.Id, StopMode.Force, TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);

        release.SetResult(true);
        var firstResult = await first;
        var secondResult = await second;

        Assert.IsType<VmOperationResult.Applied>(firstResult);
        var refused = Assert.IsType<VmOperationResult.Refused>(secondResult);
        Assert.Equal(VmRefusalReason.ForbiddenTransition, refused.Refusal.Reason);
        Assert.Equal(VmState.Stopped, refused.Refusal.CurrentState);
        Assert.Equal(1, h.Backend.CallCount("Stop"));
        Assert.Equal(1, h.Backend.MaxInFlightPerVm);
        Assert.Equal(0, h.Locks.ActiveCount);
    }

    [Fact]
    public async Task Operations_on_different_vms_run_in_parallel()
    {
        var h = new LifecycleHarness();
        var a = h.AddVm(VmState.Running);
        var b = h.AddVm(VmState.Running);
        var release = new TaskCompletionSource<bool>();
        var aEntered = new TaskCompletionSource<bool>();
        h.Backend.Hook = async (op, backendRef, _) =>
        {
            if (op == "Stop" && backendRef == a.BackendRef)
            {
                aEntered.TrySetResult(true);
                await release.Task;
            }
        };

        var stopA = h.Service.StopAsync(a.Id, StopMode.Graceful, TestContext.Current.CancellationToken);
        await aEntered.Task;

        // A is still held inside the backend. B has its own lock, so this completes without waiting for A.
        var stopB = await h.Service.StopAsync(b.Id, StopMode.Graceful, TestContext.Current.CancellationToken);

        Assert.IsType<VmOperationResult.Applied>(stopB);
        Assert.Equal(VmState.Stopped, h.Store.StateOf(b.Id));
        Assert.Equal(VmState.Running, h.Store.StateOf(a.Id));

        release.SetResult(true);
        await stopA;
        Assert.Equal(2, h.Backend.MaxInFlightTotal);
        Assert.Equal(1, h.Backend.MaxInFlightPerVm);
        Assert.Equal(0, h.Locks.ActiveCount);
    }

    [Fact]
    public async Task Lock_table_is_empty_after_a_full_lifecycle()
    {
        var h = new LifecycleHarness();
        var ct = TestContext.Current.CancellationToken;
        var vm = NewRecord(VmState.Absent);

        await h.Service.CreateAsync(vm, ct);
        await h.Service.StartAsync(vm.Id, ct);
        await h.Service.SaveAsync(vm.Id, ct);
        await h.Service.StartAsync(vm.Id, ct);
        await h.Service.StopAsync(vm.Id, StopMode.Force, ct);
        await h.Service.CheckpointAsync(vm.Id, ct);
        await h.Service.RestoreAsync(vm.Id, new CheckpointId(Guid.CreateVersion7()), ct);
        await h.Service.RetireAsync(vm.Id, ct);

        Assert.Equal(VmState.Retired, h.Store.StateOf(vm.Id));
        Assert.Equal(0, h.Locks.ActiveCount);
    }

    private static EnvironmentVmRecord NewRecord(VmState state) => new(
        Guid.CreateVersion7(), Guid.CreateVersion7(), VmOs.Linux, Guid.CreateVersion7(), null, state, 2, 2048, 30, _epoch, _epoch);

    private static Task<VmOperationResult> InvokeAsync(LifecycleHarness h, string operation, Guid vmId)
    {
        var ct = TestContext.Current.CancellationToken;
        return operation switch
        {
            "Start" => h.Service.StartAsync(vmId, ct),
            "Stop" => h.Service.StopAsync(vmId, StopMode.Graceful, ct),
            "Save" => h.Service.SaveAsync(vmId, ct),
            "Restore" => h.Service.RestoreAsync(vmId, new CheckpointId(Guid.CreateVersion7()), ct),
            "Retire" => h.Service.RetireAsync(vmId, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };
    }
}
