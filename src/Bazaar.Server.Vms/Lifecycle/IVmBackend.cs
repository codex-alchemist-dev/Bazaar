using Bazaar.Server.Vms.Environments;

namespace Bazaar.Server.Vms.Lifecycle;

/// <summary>
/// The VM provider (doc 16, "Data and interfaces"). Hyper-V on Windows is the first implementation. Only the server calls it,
/// never a guest. The lifecycle service checks transitions before calling it and records the state after it returns.
/// </summary>
public interface IVmBackend
{
    Task<VmHandle> CreateChildAsync(ImageVersionId parent, VmSpec spec, CancellationToken ct);

    Task StartAsync(VmHandle vm, CancellationToken ct);

    Task StopAsync(VmHandle vm, StopMode mode, CancellationToken ct);

    Task SaveStateAsync(VmHandle vm, CancellationToken ct);

    /// <summary>Takes a checkpoint. Valid only for a stopped or saved VM.</summary>
    Task<CheckpointId> CheckpointAsync(VmHandle vm, CancellationToken ct);

    Task RestoreAsync(VmHandle vm, CheckpointId id, CancellationToken ct);

    Task RetireAsync(VmHandle vm, CancellationToken ct);

    Task<VmState> GetStateAsync(VmHandle vm, CancellationToken ct);
}
