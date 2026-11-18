using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.Tests.Lifecycle;

/// <summary>A dictionary-backed <see cref="IEnvironmentVmStore"/> with the same compare-and-set semantics as the repository.</summary>
internal sealed class FakeEnvironmentVmStore : IEnvironmentVmStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, EnvironmentVmRecord> _rows = new();

    public Task<EnvironmentVmRecord?> GetAsync(Guid vmId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_rows.GetValueOrDefault(vmId));
    }

    public Task InsertAsync(EnvironmentVmRecord vm, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_rows.ContainsKey(vm.Id))
                throw new InvalidOperationException($"VM {vm.Id} already exists.");
            _rows[vm.Id] = vm;
        }
        return Task.CompletedTask;
    }

    public Task<bool> TryUpdateStateAsync(Guid vmId, VmState expected, VmState next, DateTimeOffset now, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(vmId, out var row) || row.State != expected)
                return Task.FromResult(false);
            _rows[vmId] = row with { State = next, UpdatedAt = now };
            return Task.FromResult(true);
        }
    }

    public Task<bool> SetBackendRefAsync(Guid vmId, string backendRef, DateTimeOffset now, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(vmId, out var row))
                return Task.FromResult(false);
            _rows[vmId] = row with { BackendRef = backendRef, UpdatedAt = now };
            return Task.FromResult(true);
        }
    }

    /// <summary>Seeds a row without going through the service, for test setup.</summary>
    public void Seed(EnvironmentVmRecord vm)
    {
        lock (_gate)
            _rows[vm.Id] = vm;
    }

    /// <summary>Changes a row's state directly, simulating a writer outside the lifecycle service.</summary>
    public void ForceState(Guid vmId, VmState state)
    {
        lock (_gate)
            _rows[vmId] = _rows[vmId] with { State = state };
    }

    public VmState? StateOf(Guid vmId)
    {
        lock (_gate)
            return _rows.TryGetValue(vmId, out var row) ? row.State : null;
    }
}
