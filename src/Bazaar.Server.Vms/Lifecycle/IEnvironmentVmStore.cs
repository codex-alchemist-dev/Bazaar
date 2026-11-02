using Bazaar.Server.Vms.Environments;

namespace Bazaar.Server.Vms.Lifecycle;

/// <summary>
/// The part of <c>environment_vm</c> the lifecycle service needs. State changes are compare-and-set so a concurrent change
/// is detected. <see cref="EnvironmentVmStore"/> is the production adapter.
/// </summary>
public interface IEnvironmentVmStore
{
    Task<EnvironmentVmRecord?> GetAsync(Guid vmId, CancellationToken ct);

    Task InsertAsync(EnvironmentVmRecord vm, CancellationToken ct);

    /// <summary>Moves a VM from <paramref name="expected"/> to <paramref name="newState"/>. Returns false when the state differs or the row is missing.</summary>
    Task<bool> TryUpdateStateAsync(Guid vmId, VmState expected, VmState newState, DateTimeOffset now, CancellationToken ct);

    /// <summary>Records the backend handle. Returns false when no VM has that id.</summary>
    Task<bool> SetBackendRefAsync(Guid vmId, string backendRef, DateTimeOffset now, CancellationToken ct);
}
