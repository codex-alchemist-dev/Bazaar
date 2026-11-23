using Bazaar.Server.Vms.Abstractions;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Ports;
using Bazaar.Server.Vms.Tests.Lifecycle;

namespace Bazaar.Server.Vms.Tests.Ports;

public sealed class LifecycleVmControlTests
{
    private readonly LifecycleHarness _harness = new();

    private LifecycleVmControl Control => new(_harness.Service);

    [Fact]
    public async Task Save_moves_a_running_vm_to_saved_through_the_backend()
    {
        var vm = _harness.AddVm(VmState.Running);

        var result = await Control.SaveAsync(vm.Id, TestContext.Current.CancellationToken);

        Assert.Equal(new VmControlResult(VmControlOutcome.Applied, VmRunState.Saved), result);
        Assert.True(result.IsApplied);
        Assert.Equal(VmState.Saved, _harness.Store.StateOf(vm.Id));
        Assert.Contains(_harness.Backend.Calls, c => c.BackendRef == vm.BackendRef);
    }

    [Theory]
    [InlineData(VmStopMode.Graceful)]
    [InlineData(VmStopMode.Force)]
    public async Task Stop_moves_a_running_vm_to_stopped(VmStopMode mode)
    {
        var vm = _harness.AddVm(VmState.Running);

        var result = await Control.StopAsync(vm.Id, mode, TestContext.Current.CancellationToken);

        Assert.Equal(new VmControlResult(VmControlOutcome.Applied, VmRunState.Stopped), result);
        Assert.Equal(VmState.Stopped, _harness.Store.StateOf(vm.Id));
    }

    [Fact]
    public async Task Saving_a_vm_that_is_not_running_is_refused_and_the_backend_is_not_called()
    {
        var vm = _harness.AddVm(VmState.Stopped);

        var result = await Control.SaveAsync(vm.Id, TestContext.Current.CancellationToken);

        Assert.Equal(new VmControlResult(VmControlOutcome.NotAllowedInState, VmRunState.Stopped), result);
        Assert.False(result.IsApplied);
        Assert.Empty(_harness.Backend.Calls);
        Assert.Equal(VmState.Stopped, _harness.Store.StateOf(vm.Id));
    }

    [Fact]
    public async Task An_unknown_vm_is_refused_with_no_state()
    {
        var result = await Control.StopAsync(Guid.CreateVersion7(), VmStopMode.Graceful, TestContext.Current.CancellationToken);

        Assert.Equal(new VmControlResult(VmControlOutcome.UnknownVm, null), result);
        Assert.Empty(_harness.Backend.Calls);
    }

    [Fact]
    public async Task A_backend_failure_propagates_and_leaves_the_recorded_state()
    {
        var vm = _harness.AddVm(VmState.Running);
        _harness.Backend.Hook = (_, _, _) => throw new InvalidOperationException("hypervisor refused");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Control.SaveAsync(vm.Id, TestContext.Current.CancellationToken));

        Assert.Equal(VmState.Running, _harness.Store.StateOf(vm.Id));
    }

    [Fact]
    public async Task A_state_change_during_the_call_is_reported_as_state_changed()
    {
        var vm = _harness.AddVm(VmState.Running);
        _harness.Backend.Hook = (_, _, _) =>
        {
            _harness.Store.ForceState(vm.Id, VmState.Stopped);
            return Task.CompletedTask;
        };

        var result = await Control.SaveAsync(vm.Id, TestContext.Current.CancellationToken);

        Assert.Equal(new VmControlResult(VmControlOutcome.StateChanged, VmRunState.Stopped), result);
    }
}
