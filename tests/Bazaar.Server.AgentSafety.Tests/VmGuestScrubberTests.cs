using Bazaar.Server.AgentSafety.Containment;
using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.AgentSafety.Tests;

public sealed class VmGuestScrubberTests
{
    internal sealed class FakeVmStore : IEnvironmentVmStore
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

        public Task<bool> SetBackendRefAsync(Guid vmId, string backendRef, DateTimeOffset now, CancellationToken ct) => Task.FromResult(true);
    }

    internal sealed class FakeBackend : IVmBackend
    {
        public List<string> Calls { get; } = [];

        public Task<VmHandle> CreateChildAsync(ImageVersionId parent, VmSpec spec, CancellationToken ct) => throw new NotSupportedException();

        public Task StartAsync(VmHandle vm, CancellationToken ct) => throw new NotSupportedException();

        public Task StopAsync(VmHandle vm, StopMode mode, CancellationToken ct)
        {
            Calls.Add($"stop-{mode}");
            return Task.CompletedTask;
        }

        public Task SaveStateAsync(VmHandle vm, CancellationToken ct) => throw new NotSupportedException();

        public Task<CheckpointId> CheckpointAsync(VmHandle vm, CancellationToken ct) => throw new NotSupportedException();

        public Task RestoreAsync(VmHandle vm, CheckpointId id, CancellationToken ct) => throw new NotSupportedException();

        public Task RetireAsync(VmHandle vm, CancellationToken ct)
        {
            Calls.Add("retire");
            return Task.CompletedTask;
        }

        public Task<VmState> GetStateAsync(VmHandle vm, CancellationToken ct) => throw new NotSupportedException();
    }

    private static (VmGuestScrubber Sut, FakeVmStore Store, FakeBackend Backend, EnvironmentVmRecord Vm) Build(VmState state)
    {
        var vm = new EnvironmentVmRecord(Guid.NewGuid(), Guid.NewGuid(), VmOs.Linux, Guid.NewGuid(), "ref", state, 2, 4096, 60, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var store = new FakeVmStore();
        store.Vms[vm.Id] = vm;
        var backend = new FakeBackend();
        var lifecycle = new VmLifecycleService(store, backend, new VmOperationLocks(), TimeProvider.System);
        return (new VmGuestScrubber(lifecycle), store, backend, vm);
    }

    private static GuestScrubRequest Request(EnvironmentVmRecord vm, DetachReason reason = DetachReason.HardTaint) => new(vm.AgentId, vm.Id, Guid.NewGuid(), reason);

    [Fact]
    public async Task A_running_vm_is_force_stopped_before_its_disk_is_discarded()
    {
        var (sut, store, backend, vm) = Build(VmState.Running);

        await sut.ScrubAsync(Request(vm), TestContext.Current.CancellationToken);

        Assert.Equal(["stop-Force", "retire"], backend.Calls);
        Assert.Equal(VmState.Retired, store.Vms[vm.Id].State);
    }

    [Theory]
    [InlineData(VmState.Stopped)]
    [InlineData(VmState.Saved)]
    public async Task A_paused_vm_is_retired_without_another_stop(VmState state)
    {
        var (sut, store, backend, vm) = Build(state);

        await sut.ScrubAsync(Request(vm), TestContext.Current.CancellationToken);

        Assert.Equal(["retire"], backend.Calls);
        Assert.Equal(VmState.Retired, store.Vms[vm.Id].State);
    }

    [Fact]
    public async Task On_the_kill_switch_the_default_only_pauses_and_keeps_the_disk()
    {
        var (sut, store, backend, vm) = Build(VmState.Running);

        await sut.ScrubAsync(Request(vm, DetachReason.KillSwitch), TestContext.Current.CancellationToken);

        Assert.Equal(["stop-Force"], backend.Calls);
        Assert.Equal(VmState.Stopped, store.Vms[vm.Id].State);
    }

    [Fact]
    public async Task The_scrub_action_per_reason_is_configurable()
    {
        var (_, store, backend, vm) = Build(VmState.Running);
        var lifecycle = new VmLifecycleService(store, backend, new VmOperationLocks(), TimeProvider.System);
        var sut = new VmGuestScrubber(lifecycle, new GuestScrubOptions { KillSwitch = GuestScrubAction.Retire, HardTaint = GuestScrubAction.PauseOnly });

        await sut.ScrubAsync(Request(vm, DetachReason.HardTaint), TestContext.Current.CancellationToken);
        Assert.Equal(VmState.Stopped, store.Vms[vm.Id].State);

        await sut.ScrubAsync(Request(vm, DetachReason.KillSwitch), TestContext.Current.CancellationToken);
        Assert.Equal(VmState.Retired, store.Vms[vm.Id].State);
    }

    [Fact]
    public async Task Scrubbing_twice_is_a_no_op_the_second_time()
    {
        var (sut, _, backend, vm) = Build(VmState.Running);

        await sut.ScrubAsync(Request(vm), TestContext.Current.CancellationToken);
        await sut.ScrubAsync(Request(vm), TestContext.Current.CancellationToken);

        Assert.Equal(["stop-Force", "retire"], backend.Calls);
    }

    [Fact]
    public async Task An_unknown_vm_and_a_provisioning_vm_fail_instead_of_claiming_a_scrub()
    {
        var (sut, _, _, vm) = Build(VmState.Provisioning);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ScrubAsync(Request(vm), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.ScrubAsync(new GuestScrubRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DetachReason.HardTaint), TestContext.Current.CancellationToken));
    }
}
