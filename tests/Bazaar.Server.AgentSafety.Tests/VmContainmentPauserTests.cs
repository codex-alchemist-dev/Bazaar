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

        public Task<VmHandle> CreateChildAsync(ImageVersionId parent, VmSpec spec, CancellationToken ct) => throw new NotSupportedException();

        public Task StartAsync(VmHandle vm, CancellationToken ct)
        {
            Calls.Add($"start:{vm.VmId}");
            return Task.CompletedTask;
        }

        public Task StopAsync(VmHandle vm, StopMode mode, CancellationToken ct)
        {
            Calls.Add($"stop-{mode}:{vm.VmId}");
            return Task.CompletedTask;
        }

        public Task SaveStateAsync(VmHandle vm, CancellationToken ct)
        {
            Calls.Add($"save:{vm.VmId}");
            return Task.CompletedTask;
        }

        public Task<CheckpointId> CheckpointAsync(VmHandle vm, CancellationToken ct) => throw new NotSupportedException();
        public Task RestoreAsync(VmHandle vm, CheckpointId id, CancellationToken ct) => throw new NotSupportedException();
        public Task RetireAsync(VmHandle vm, CancellationToken ct) => throw new NotSupportedException();
        public Task<VmState> GetStateAsync(VmHandle vm, CancellationToken ct) => throw new NotSupportedException();
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
