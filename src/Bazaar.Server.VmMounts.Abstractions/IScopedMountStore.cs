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
    /// <summary>
    /// Inserts an attached row only if the VM exists, belongs to the row's agent, runs <paramref name="guestOs"/> and is not
    /// Retired (an INSERT...SELECT over <c>environment_vm</c>, so the check and the insert are one statement).
    /// </summary>
    Task<MountInsertResult> TryInsertAsync(ScopedMountRow row, GuestOs guestOs, CancellationToken ct);

    /// <summary>The OS of a VM, from <c>environment_vm</c>. Null when the VM does not exist.</summary>
    Task<GuestOs?> GetVmGuestOsAsync(Guid vmId, CancellationToken ct);

    /// <summary>Every attached mount, in id order. Used at startup to re-arm canary watches.</summary>
    Task<IReadOnlyList<ScopedMountRow>> ListAttachedAsync(CancellationToken ct);

    Task<ScopedMountRow?> GetAsync(Guid mountId, CancellationToken ct);

    /// <summary>The attached mounts of one agent across its VMs, in id order.</summary>
    Task<IReadOnlyList<ScopedMountRow>> ListAttachedForAgentAsync(Guid agentId, CancellationToken ct);

    /// <summary>Marks an attached mount as detached. Returns false when the row is missing or already detached.</summary>
    Task<bool> MarkDetachedAsync(Guid mountId, DateTimeOffset at, CancellationToken ct);
}

/// <summary>Outcome of <see cref="IScopedMountStore.TryInsertAsync"/>.</summary>
public enum MountInsertResult
{
    Inserted,

    /// <summary>The VM and guest path already carry an attached mount.</summary>
    GuestPathTaken,

    /// <summary>The VM is missing, belongs to another agent, runs another OS or is Retired.</summary>
    VmNotEligible,
}
