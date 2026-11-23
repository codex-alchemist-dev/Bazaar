namespace Bazaar.Server.VmMounts.Abstractions;

/// <summary>
/// A planted canary as persisted. Only the SHA-256 fingerprint of the value is kept, never the value. <see cref="RetiredAt"/> is set
/// when the mount detaches; a retired canary still matches, because its value showing up later is still a leak.
/// </summary>
public sealed record CanaryRecord(
    Guid Id,
    Guid MountId,
    Guid AgentId,
    Guid VmId,
    string RelativePath,
    string Fingerprint,
    DateTimeOffset PlantedAt,
    DateTimeOffset? RetiredAt);

/// <summary>Persistence for planted canaries (table <c>vm_mount_canary</c>, this module's own migration namespace).</summary>
public interface ICanaryStore
{
    Task InsertAsync(IReadOnlyList<CanaryRecord> canaries, CancellationToken ct);

    /// <summary>Finds a canary by fingerprint, retired or not.</summary>
    Task<CanaryRecord?> FindByFingerprintAsync(string fingerprint, CancellationToken ct);

    /// <summary>The canaries of a mount that are not retired.</summary>
    Task<IReadOnlyList<CanaryRecord>> ListActiveForMountAsync(Guid mountId, CancellationToken ct);

    /// <summary>Marks a mount's canaries retired. Rows are never deleted.</summary>
    Task RetireForMountAsync(Guid mountId, DateTimeOffset at, CancellationToken ct);
}
