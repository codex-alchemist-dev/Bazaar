using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.Tests.Lifecycle;

/// <summary>Wires the service to the fake store and backend, and seeds VMs in a given state.</summary>
internal sealed class LifecycleHarness
{
    private static readonly DateTimeOffset _epoch = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private int _nextRef;

    public FakeEnvironmentVmStore Store { get; } = new();

    public FakeVmBackend Backend { get; } = new();

    public VmOperationLocks Locks { get; } = new();

    public VmLifecycleService Service { get; }

    public LifecycleHarness()
    {
        Service = new VmLifecycleService(Store, Backend, Locks, TimeProvider.System);
    }

    /// <summary>Seeds a VM in <paramref name="state"/> with a backend ref. Its id is new on every call.</summary>
    public EnvironmentVmRecord AddVm(VmState state, VmOs os = VmOs.Linux)
    {
        var n = Interlocked.Increment(ref _nextRef);
        var vm = new EnvironmentVmRecord(
            Guid.CreateVersion7(), Guid.CreateVersion7(), os, Guid.CreateVersion7(), $"fake-vm-{n}", state, 2, 2048, 30, _epoch, _epoch);
        Store.Seed(vm);
        return vm;
    }
}
