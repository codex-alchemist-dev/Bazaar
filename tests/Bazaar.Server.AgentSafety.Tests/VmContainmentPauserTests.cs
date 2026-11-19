using Bazaar.Server.AgentSafety.Containment;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.AgentSafety.Tests;

public sealed class VmContainmentPauserTests
{
    /// <summary>An in-memory environment store with the compare-and-set the lifecycle service relies on.</summary>
    private sealed class FakeVmStore : IEnvironmentVmStore
    {
        public Dictionary<Guid, EnvironmentVmRecord> Vms { get; } = [];

        public Task<EnvironmentVmRecord?> GetAsync(Guid vmId, CancellationToken ct) =>
            Task.FromResult(Vms.TryGetValue(vmId, out var vm) ? vm : null);

        public Task InsertAsync(EnvironmentVmRecord vm, CancellationToken ct)
        {
            Vms[vm.Id] = vm;
            return Task.CompletedTask;
        }

        public Task<bool> TryUpdateStateAsync(Guid vmId, VmState expected, VmState newState, DateTimeOffset now, CancellationToken ct)
        {
            if (!Vms.TryGetValue(vmId, out var vm) || vm.State != expected)
            {
                return Task.FromResult(false);
            }

            Vms[vmId] = vm with { State = newState, UpdatedAt = now };
            return Task.FromResult(true);
        }

        public Task<bool> SetBackendRefAsync(Guid vmId, string backendRef, DateTimeOffset now, CancellationToken ct)
        {
            if (!Vms.TryGetValue(vmId, out var vm))
            {
                return Task.FromResult(false);
            }

            Vms[vmId] = vm with { BackendRef = backendRef, UpdatedAt = now };
            return Task.FromResult(true);
        }
    }

    /// <summary>A backend that records each call, with the save capability set by the test.</summary>
    private sealed class FakeBackend(bool supportsSaveState) : IVmBackend
    {
        public List<string> Calls { get; } = [];
        public bool SupportsSaveState { get; } = supportsSaveState;

        /// <summary>VMs whose save or stop throws.</summary>
        public HashSet<Guid> FailFor { get; } = [];

        /// <summary>What the backend itself reports for a VM; a VM not listed throws, as before.</summary>
        public Dictionary<Guid, VmState> Reported { get; } = [];

        public Task<VmHandle> CreateChildAsync(ImageVersionId parent, VmSpec spec, CancellationToken ct) => throw new NotSupportedException();

        public Task StartAsync(VmHandle vm, CancellationToken ct)
        {
            Calls.Add($"start:{vm.VmId}");
            return Task.CompletedTask;
        }

        public Task StopAsync(VmHandle vm, StopMode mode, CancellationToken ct)
        {
            Calls.Add($"stop-{mode}:{vm.VmId}");
            return FailFor.Contains(vm.VmId) ? throw new InvalidOperationException("stop failed") : Task.CompletedTask;
        }

        public Task SaveStateAsync(VmHandle vm, CancellationToken ct)
        {
            Calls.Add($"save:{vm.VmId}");
            return FailFor.Contains(vm.VmId) ? throw new InvalidOperationException("save failed") : Task.CompletedTask;
        }

        public Task<CheckpointId> CheckpointAsync(VmHandle vm, CancellationToken ct) => throw new NotSupportedException();
        public Task RestoreAsync(VmHandle vm, CheckpointId id, CancellationToken ct) => throw new NotSupportedException();
        public Task RetireAsync(VmHandle vm, CancellationToken ct) => throw new NotSupportedException();
        public Task<VmState> GetStateAsync(VmHandle vm, CancellationToken ct) =>
            Reported.TryGetValue(vm.VmId, out var state) ? Task.FromResult(state) : throw new NotSupportedException();
    }

    private sealed class FakeDirectory(IReadOnlyList<EnvironmentVmRecord> vms) : IAgentVmDirectory
    {
        public Task<IReadOnlyList<EnvironmentVmRecord>> ListAsync(Guid agentId, CancellationToken ct) => Task.FromResult(vms);
    }

    private static EnvironmentVmRecord Vm(Guid agent, VmState state) =>
        new(Guid.NewGuid(), agent, VmOs.Windows, Guid.NewGuid(), "backend-ref", state, 2, 4096, 60, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static (VmContainmentPauser Pauser, FakeVmStore Store, FakeBackend Backend) Build(
        bool supportsSaveState, IReadOnlyList<EnvironmentVmRecord> vms)
    {
        var store = new FakeVmStore();
        foreach (var vm in vms)
        {
            store.Vms[vm.Id] = vm;
        }

        var backend = new FakeBackend(supportsSaveState);
        var lifecycle = new VmLifecycleService(store, backend, new VmOperationLocks(), TimeProvider.System);
        return (new VmContainmentPauser(new FakeDirectory(vms), lifecycle, backend), store, backend);
    }

    [Fact]
    public async Task A_backend_that_can_save_saves_the_running_vms_and_leaves_stopped_ones_alone()
    {
        var agent = Guid.NewGuid();
        var running = Vm(agent, VmState.Running);
        var stopped = Vm(agent, VmState.Stopped);
        var (pauser, store, backend) = Build(supportsSaveState: true, [running, stopped]);

        await pauser.PauseAsync(agent.ToString(), TestContext.Current.CancellationToken);

        Assert.Equal([$"save:{running.Id}"], backend.Calls);
        Assert.Equal(VmState.Saved, store.Vms[running.Id].State);
        Assert.Equal(VmState.Stopped, store.Vms[stopped.Id].State);
    }

    [Fact]
    public async Task A_backend_that_cannot_save_power_cuts_the_running_vms()
    {
        var agent = Guid.NewGuid();
        var running = Vm(agent, VmState.Running);
        var (pauser, store, backend) = Build(supportsSaveState: false, [running]);

        await pauser.PauseAsync(agent.ToString(), TestContext.Current.CancellationToken);

        Assert.Equal([$"stop-Force:{running.Id}"], backend.Calls);
        Assert.Equal(VmState.Stopped, store.Vms[running.Id].State);
    }

    [Fact]
    public async Task A_vm_that_cannot_be_paused_does_not_leave_the_agents_other_vm_running()
    {
        var agent = Guid.NewGuid();
        var broken = Vm(agent, VmState.Running);
        var healthy = Vm(agent, VmState.Running);
        var (pauser, store, backend) = Build(supportsSaveState: true, [broken, healthy]);
        backend.FailFor.Add(broken.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pauser.PauseAsync(agent.ToString(), TestContext.Current.CancellationToken));

        Assert.Equal(VmState.Running, store.Vms[broken.Id].State);
        Assert.Equal(VmState.Saved, store.Vms[healthy.Id].State);
    }

    [Fact]
    public async Task Every_failed_vm_is_reported_when_more_than_one_fails()
    {
        var agent = Guid.NewGuid();
        var first = Vm(agent, VmState.Running);
        var second = Vm(agent, VmState.Running);
        var (pauser, _, backend) = Build(supportsSaveState: true, [first, second]);
        backend.FailFor.Add(first.Id);
        backend.FailFor.Add(second.Id);

        var error = await Assert.ThrowsAsync<AggregateException>(() => pauser.PauseAsync(agent.ToString(), TestContext.Current.CancellationToken));

        Assert.Equal(2, error.InnerExceptions.Count);
    }

    [Fact]
    public async Task A_restored_vm_that_the_backend_runs_is_power_cut_and_one_that_is_off_is_left_alone()
    {
        var agent = Guid.NewGuid();
        var live = Vm(agent, VmState.Restored);
        var off = Vm(agent, VmState.Restored);
        var (pauser, store, backend) = Build(supportsSaveState: true, [live, off]);
        backend.Reported[live.Id] = VmState.Running;
        backend.Reported[off.Id] = VmState.Stopped;

        await pauser.PauseAsync(agent.ToString(), TestContext.Current.CancellationToken);

        Assert.Equal([$"stop-Force:{live.Id}"], backend.Calls);
        Assert.Equal(VmState.Stopped, store.Vms[live.Id].State);
        Assert.Equal(VmState.Restored, store.Vms[off.Id].State);
    }

    [Fact]
    public async Task A_vm_that_stopped_running_before_its_turn_counts_as_paused()
    {
        var agent = Guid.NewGuid();
        var listedRunning = Vm(agent, VmState.Running);
        var (pauser, store, backend) = Build(supportsSaveState: true, [listedRunning]);
        // The directory still lists it as running, but an idle save got there first.
        store.Vms[listedRunning.Id] = listedRunning with { State = VmState.Saved };

        await pauser.PauseAsync(agent.ToString(), TestContext.Current.CancellationToken);

        Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task Resume_starts_the_saved_and_stopped_vms_and_skips_retired_ones()
    {
        var agent = Guid.NewGuid();
        var saved = Vm(agent, VmState.Saved);
        var stopped = Vm(agent, VmState.Stopped);
        var retired = Vm(agent, VmState.Retired);
        var (pauser, store, backend) = Build(supportsSaveState: true, [saved, stopped, retired]);

        await pauser.ResumeAsync(agent.ToString(), TestContext.Current.CancellationToken);

        Assert.Equal(VmState.Running, store.Vms[saved.Id].State);
        Assert.Equal(VmState.Running, store.Vms[stopped.Id].State);
        Assert.Equal(VmState.Retired, store.Vms[retired.Id].State);
        Assert.Equal(2, backend.Calls.Count(call => call.StartsWith("start:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_non_guid_agent_id_is_refused_because_its_vms_cannot_be_found()
    {
        var (pauser, _, _) = Build(supportsSaveState: false, []);

        await Assert.ThrowsAsync<ArgumentException>(() => pauser.PauseAsync("agent-a", TestContext.Current.CancellationToken));
    }
}
