using Bazaar.Server.Vms.Environments;

namespace Bazaar.Server.Vms.Lifecycle;

/// <summary>Production <see cref="IEnvironmentVmStore"/>: a thin wrapper over <see cref="EnvironmentRepository"/>.</summary>
public sealed class EnvironmentVmStore(EnvironmentRepository repository) : IEnvironmentVmStore
{
    public Task<EnvironmentVmRecord?> GetAsync(Guid vmId, CancellationToken ct) =>
        repository.GetVmAsync(vmId, ct);

    public Task InsertAsync(EnvironmentVmRecord vm, CancellationToken ct) =>
        repository.InsertVmAsync(vm, ct);

    public Task<bool> TryUpdateStateAsync(Guid vmId, VmState expected, VmState newState, DateTimeOffset now, CancellationToken ct) =>
        repository.TryUpdateVmStateAsync(vmId, expected, newState, now, ct);

    public Task<bool> SetBackendRefAsync(Guid vmId, string backendRef, DateTimeOffset now, CancellationToken ct) =>
        repository.SetBackendRefAsync(vmId, backendRef, now, ct);
}
