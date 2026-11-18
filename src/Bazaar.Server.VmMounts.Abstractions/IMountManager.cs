namespace Bazaar.Server.VmMounts.Abstractions;

/// <summary>
/// Owns the scoped mounts of the agent VMs (doc 16 WP8, doc 15 WP8). Operations on one VM never overlap; operations on different
/// VMs run in parallel. Every attach, detach and write-back writes an audit row.
/// </summary>
public interface IMountManager
{
    /// <summary>
    /// Attaches one mount after the path, mode and guest-path checks. A Soul mount also plants canary credentials and copies the
    /// cookie profile into the guest. Throws <see cref="MountRefusedException"/> when a check fails; nothing is left attached then.
    /// </summary>
    Task<AttachedMount> AttachAsync(MountSpec spec, CancellationToken ct);

    /// <summary>
    /// Detaches one mount. A clean detach of a Soul mount writes the guest cookie profile back first. Detaching a mount that is
    /// already detached succeeds with <see cref="DetachResult.WasAlreadyDetached"/> set, so the kill switch can be repeated.
    /// </summary>
    Task<DetachResult> DetachAsync(Guid mountId, DetachReason reason, CancellationToken ct);

    /// <summary>
    /// Detaches every attached mount of an agent, for hard taint and the kill switch (the agent-safety containment calls this). One
    /// failing mount does not stop the others; the failures are thrown together afterwards as an <see cref="AggregateException"/>.
    /// </summary>
    Task<IReadOnlyList<DetachResult>> DetachAllForAgentAsync(Guid agentId, DetachReason reason, CancellationToken ct);

    /// <summary>Reads the guest cookie profile back into the host profile while the mount stays attached (Soul mounts only).</summary>
    Task<WriteBackResult> WriteBackProfileAsync(Guid mountId, CancellationToken ct);

    /// <summary>
    /// Reports that a value was seen outside the guest (for example at the egress proxy). When it matches a canary planted by an
    /// attached mount, the use is sent to <see cref="ICanarySink"/> and true is returned. The value itself is never stored or logged.
    /// </summary>
    Task<bool> ReportCanaryTokenUseAsync(string tokenValue, string source, CancellationToken ct);
}
