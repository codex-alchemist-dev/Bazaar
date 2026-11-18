using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.Tests.Reconciliation;

/// <summary>
/// An <see cref="IVmBackend"/> whose reported state is scripted per backend ref. Reconciliation only asks for state, so any other
/// call is recorded as unexpected and throws; a test asserts that none happened, which is how "never deletes" is checked.
/// </summary>
internal sealed class ScriptedVmBackend : IVmBackend
{
    private readonly object _gate = new();
    private readonly Dictionary<string, VmState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Exception> _failures = new(StringComparer.Ordinal);
    private readonly List<string> _stateQueries = [];
    private readonly List<string> _unexpectedCalls = [];

    /// <summary>The backend refs that <see cref="GetStateAsync"/> was asked about, in call order.</summary>
    public IReadOnlyList<string> StateQueries
    {
        get
        {
            lock (_gate)
                return _stateQueries.ToList();
        }
    }

    /// <summary>Operations other than <see cref="GetStateAsync"/> that were called. Must stay empty.</summary>
    public IReadOnlyList<string> UnexpectedCalls
    {
        get
        {
            lock (_gate)
                return _unexpectedCalls.ToList();
        }
    }

    public void Report(string backendRef, VmState state)
    {
        lock (_gate)
            _states[backendRef] = state;
    }

    public void Fail(string backendRef, Exception error)
    {
        lock (_gate)
            _failures[backendRef] = error;
    }

    public Task<VmState> GetStateAsync(VmHandle vm, CancellationToken ct)
    {
        lock (_gate)
        {
            _stateQueries.Add(vm.BackendRef);
            if (_failures.TryGetValue(vm.BackendRef, out var error))
                return Task.FromException<VmState>(error);
            if (_states.TryGetValue(vm.BackendRef, out var state))
                return Task.FromResult(state);
        }
        return Task.FromException<VmState>(new InvalidOperationException($"No state scripted for {vm.BackendRef}."));
    }

    public Task<VmHandle> CreateChildAsync(ImageVersionId parent, VmSpec spec, CancellationToken ct) => Unexpected<VmHandle>(nameof(CreateChildAsync));

    public Task StartAsync(VmHandle vm, CancellationToken ct) => Unexpected(nameof(StartAsync));

    public Task StopAsync(VmHandle vm, StopMode mode, CancellationToken ct) => Unexpected(nameof(StopAsync));

    public Task SaveStateAsync(VmHandle vm, CancellationToken ct) => Unexpected(nameof(SaveStateAsync));

    public Task<CheckpointId> CheckpointAsync(VmHandle vm, CancellationToken ct) => Unexpected<CheckpointId>(nameof(CheckpointAsync));

    public Task RestoreAsync(VmHandle vm, CheckpointId id, CancellationToken ct) => Unexpected(nameof(RestoreAsync));

    public Task RetireAsync(VmHandle vm, CancellationToken ct) => Unexpected(nameof(RetireAsync));

    private Task<bool> Unexpected(string operation) => Unexpected<bool>(operation);

    private Task<T> Unexpected<T>(string operation)
    {
        lock (_gate)
            _unexpectedCalls.Add(operation);
        return Task.FromException<T>(new InvalidOperationException($"The reconciler must not call {operation}."));
    }
}
