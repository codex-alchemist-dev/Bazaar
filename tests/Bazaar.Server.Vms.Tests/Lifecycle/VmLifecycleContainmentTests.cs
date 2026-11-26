using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.Tests.Lifecycle;

/// <summary>A VM of a contained agent must not start, and a check that cannot answer keeps it down.</summary>
public sealed class VmLifecycleContainmentTests
{
    private sealed class Check(Func<Guid, bool> contained, bool throws = false) : IAgentContainmentCheck
    {
        public Task<bool> IsContainedAsync(Guid agentId, CancellationToken ct) =>
            throws ? throw new InvalidOperationException("store down") : Task.FromResult(contained(agentId));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Start_is_refused_for_a_contained_agent_and_the_backend_is_not_called()
    {
        var h = new LifecycleHarness(new Check(_ => true));
        var vm = h.AddVm(VmState.Stopped);

        var result = await h.Service.StartAsync(vm.Id, Ct);

        var refused = Assert.IsType<VmOperationResult.Refused>(result);
        Assert.Equal(VmRefusalReason.AgentContained, refused.Refusal.Reason);
        Assert.Equal(VmState.Stopped, h.Store.StateOf(vm.Id));
        Assert.Equal(0, h.Backend.CallCount("Start"));
    }

    [Fact]
    public async Task Start_runs_for_an_agent_that_is_not_contained()
    {
        var h = new LifecycleHarness(new Check(_ => false));
        var vm = h.AddVm(VmState.Stopped);

        var result = await h.Service.StartAsync(vm.Id, Ct);

        Assert.Equal(VmState.Running, Assert.IsType<VmOperationResult.Applied>(result).State);
    }

    [Fact]
    public async Task Only_the_contained_agents_vm_is_refused()
    {
        var h = new LifecycleHarness();
        var contained = h.AddVm(VmState.Saved);
        var other = h.AddVm(VmState.Saved);
        var service = new VmLifecycleService(h.Store, h.Backend, h.Locks, TimeProvider.System, new Check(id => id == contained.AgentId));

        Assert.IsType<VmOperationResult.Refused>(await service.StartAsync(contained.Id, Ct));
        Assert.IsType<VmOperationResult.Applied>(await service.StartAsync(other.Id, Ct));
    }

    [Fact]
    public async Task An_unanswerable_check_keeps_the_vm_down()
    {
        var h = new LifecycleHarness(new Check(_ => false, throws: true));
        var vm = h.AddVm(VmState.Stopped);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.StartAsync(vm.Id, Ct));

        Assert.Equal(VmState.Stopped, h.Store.StateOf(vm.Id));
        Assert.Equal(0, h.Backend.CallCount("Start"));
    }

    [Fact]
    public async Task The_release_start_skips_the_check_so_an_owner_release_can_resume_the_vm()
    {
        var h = new LifecycleHarness(new Check(_ => true));
        var vm = h.AddVm(VmState.Saved);

        var result = await h.Service.StartForReleaseAsync(vm.Id, Ct);

        Assert.Equal(VmState.Running, Assert.IsType<VmOperationResult.Applied>(result).State);
    }
}
