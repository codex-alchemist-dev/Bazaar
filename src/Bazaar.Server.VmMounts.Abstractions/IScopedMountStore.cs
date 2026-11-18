namespace Bazaar.Server.VmMounts.Abstractions;

/// <summary>One row of <c>scoped_mount</c>. A null <see cref="DetachedAt"/> means the mount is attached.</summary>
public sealed record ScopedMountRow(
    Guid Id,
    Guid AgentId,
    Guid VmId,
    MountKind Kind,
    string HostPath,
    string GuestPath,
    MountMode Mode,
    DateTimeOffset? DetachedAt);

/// <summary>
/// Access to the <c>scoped_mount</c> table. The table belongs to the <c>vms</c> component (migration vms/0001); this module adds no
/// migration. The database allows one attached mount per VM and guest path.
/// </summary>
public interface IScopedMountStore
{
    /// <summary>Inserts an attached row. Returns false when the VM and guest path already carry an attached mount.</summary>
    Task<bool> TryInsertAsync(ScopedMountRow row, CancellationToken ct);

    Task<ScopedMountRow?> GetAsync(Guid mountId, CancellationToken ct);

    /// <summary>The attached mounts of one agent across its VMs, in id order.</summary>
    Task<IReadOnlyList<ScopedMountRow>> ListAttachedForAgentAsync(Guid agentId, CancellationToken ct);

    /// <summary>Marks an attached mount as detached. Returns false when the row is missing or already detached.</summary>
    Task<bool> MarkDetachedAsync(Guid mountId, DateTimeOffset at, CancellationToken ct);
}
