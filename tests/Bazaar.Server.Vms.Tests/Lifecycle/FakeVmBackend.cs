using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.Tests.Lifecycle;

/// <summary>
/// An in-memory <see cref="IVmBackend"/>. It records every call, tracks how many calls overlap per VM and across VMs, and runs
/// an optional <see cref="Hook"/> inside each call so a test can block it on a gate, fail it, or change the store under it.
/// </summary>
internal sealed class FakeVmBackend : IVmBackend
{
    public sealed record BackendCall(string Operation, string BackendRef);

    private readonly object _gate = new();
    private readonly List<BackendCall> _calls = [];
    private readonly Dictionary<string, int> _inFlightByRef = new(StringComparer.Ordinal);
    private int _inFlightTotal;
    private int _nextChild;

    /// <summary>Runs inside each call, after it is recorded. Receives the operation name, the backend ref and the token.</summary>
    public Func<string, string, CancellationToken, Task>? Hook { get; set; }

    /// <summary>The most calls that were in flight for any one backend ref at the same time.</summary>
    public int MaxInFlightPerVm { get; private set; }

    /// <summary>The most calls that were in flight across all refs at the same time.</summary>
    public int MaxInFlightTotal { get; private set; }

    /// <summary>What <see cref="GetStateAsync"/> reports.</summary>
    public VmState ReportedState { get; set; } = VmState.Stopped;

    public IReadOnlyList<BackendCall> Calls
    {
        get
        {
            lock (_gate)
                return _calls.ToList();
        }
    }

    public int CallCount(string operation)
    {
        lock (_gate)
            return _calls.Count(c => c.Operation == operation);
    }

    public Task<VmHandle> CreateChildAsync(ImageVersionId parent, VmSpec spec, CancellationToken ct) => CreateChildCoreAsync(ct);

    private async Task<VmHandle> CreateChildCoreAsync(CancellationToken ct)
    {
        int n;
        lock (_gate)
            n = ++_nextChild;
        var backendRef = $"fake-child-{n}";
        await InvokeAsync("Create", backendRef, ct).ConfigureAwait(false);
        return new VmHandle(Guid.CreateVersion7(), backendRef);
    }

    public Task StartAsync(VmHandle vm, CancellationToken ct) => InvokeAsync("Start", vm.BackendRef, ct);

    public Task StopAsync(VmHandle vm, StopMode mode, CancellationToken ct) => InvokeAsync("Stop", vm.BackendRef, ct);

    public Task SaveStateAsync(VmHandle vm, CancellationToken ct) => InvokeAsync("Save", vm.BackendRef, ct);

    public async Task<CheckpointId> CheckpointAsync(VmHandle vm, CancellationToken ct)
    {
        await InvokeAsync("Checkpoint", vm.BackendRef, ct).ConfigureAwait(false);
        return new CheckpointId(Guid.CreateVersion7());
    }

    public Task RestoreAsync(VmHandle vm, CheckpointId id, CancellationToken ct) => InvokeAsync("Restore", vm.BackendRef, ct);

    public Task RetireAsync(VmHandle vm, CancellationToken ct) => InvokeAsync("Retire", vm.BackendRef, ct);

    public async Task<VmState> GetStateAsync(VmHandle vm, CancellationToken ct)
    {
        await InvokeAsync("GetState", vm.BackendRef, ct).ConfigureAwait(false);
        return ReportedState;
    }

    private async Task InvokeAsync(string operation, string backendRef, CancellationToken ct)
    {
        lock (_gate)
        {
            _calls.Add(new BackendCall(operation, backendRef));
            _inFlightTotal++;
            _inFlightByRef[backendRef] = _inFlightByRef.GetValueOrDefault(backendRef) + 1;
            MaxInFlightTotal = Math.Max(MaxInFlightTotal, _inFlightTotal);
            MaxInFlightPerVm = Math.Max(MaxInFlightPerVm, _inFlightByRef[backendRef]);
        }

        try
        {
            if (Hook is { } hook)
                await hook(operation, backendRef, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _inFlightTotal--;
                _inFlightByRef[backendRef]--;
            }
        }
    }
}
