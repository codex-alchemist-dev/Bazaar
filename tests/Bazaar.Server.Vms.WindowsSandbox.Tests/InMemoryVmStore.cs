using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.WindowsSandbox.Tests;

/// <summary>A dictionary-backed store with compare-and-set state changes, enough to drive the real lifecycle service.</summary>
internal sealed class InMemoryVmStore : IEnvironmentVmStore
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
            _rows[vm.Id] = vm;
        return Task.CompletedTask;
    }

    public Task<bool> TryUpdateStateAsync(Guid vmId, VmState expected, VmState newState, DateTimeOffset now, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(vmId, out var row) || row.State != expected)
                return Task.FromResult(false);
            _rows[vmId] = row with { State = newState, UpdatedAt = now };
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

    public VmState? StateOf(Guid vmId)
    {
        lock (_gate)
            return _rows.GetValueOrDefault(vmId)?.State;
    }
}
