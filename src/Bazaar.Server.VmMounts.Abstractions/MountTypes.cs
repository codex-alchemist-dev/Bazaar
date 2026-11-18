namespace Bazaar.Server.VmMounts.Abstractions;

/// <summary>What a scoped mount exposes to a guest (<c>scoped_mount.kind</c>, doc 16 "Scoped mounts").</summary>
public enum MountKind
{
    /// <summary>The agent's Soul folder: metadata and the browser session-cookie profile. Always read-only on the host share.</summary>
    Soul,

    /// <summary>The agent's workspace share for coding worktrees (doc 17). Always read-write, on its own share and guest account.</summary>
    Workspace,
}

/// <summary>Access mode of a scoped mount (<c>scoped_mount.mode</c>).</summary>
public enum MountMode
{
    Ro,
    Rw,
}

/// <summary>Guest operating system of the VM a mount is attached to. It decides the guest path rules.</summary>
public enum GuestOs
{
    Linux,
    Windows,
}

/// <summary>
/// A request to attach one scoped mount. The caller is a server component, never a guest. The manager still checks every field:
/// the host path against the agent's own root and the deny list, the guest path against the server's guest layout, and the mode
/// against the kind (Soul is read-only, workspace is read-write).
/// </summary>
/// <remarks>
/// The checks run once, at attach (time of check). A host path that is changed afterwards, for example by swapping a directory for a
/// link, is not re-checked by the manager: real providers must refuse to follow links at share time (see <see cref="IMountProvider"/>).
/// </remarks>
public sealed record MountSpec(
    Guid AgentId,
    Guid VmId,
    GuestOs GuestOs,
    MountKind Kind,
    MountMode Mode,
    string HostPath,
    string GuestPath);

/// <summary>One attached mount, as the manager reports it after a successful attach.</summary>
/// <param name="CanaryCount">Canary credentials planted in the share. Zero for a workspace mount.</param>
/// <param name="ProfileFilesCopied">Files of the cookie profile copied into the guest working copy. Zero for a workspace mount.</param>
public sealed record AttachedMount(
    Guid MountId,
    Guid AgentId,
    Guid VmId,
    MountKind Kind,
    MountMode Mode,
    string HostPath,
    string GuestPath,
    int CanaryCount,
    int ProfileFilesCopied);

/// <summary>Why a mount is being detached. Everything except <see cref="Clean"/> skips the profile write-back.</summary>
public enum DetachReason
{
    /// <summary>Normal session end or an operator detach. The Soul profile is read back, validated and written host-side.</summary>
    Clean,

    /// <summary>The session reached hard taint (doc 15). The guest is untrusted: no write-back, no guest call.</summary>
    HardTaint,

    /// <summary>The per-agent or global kill switch. No write-back, no guest call.</summary>
    KillSwitch,
}

/// <summary>What happened to the Soul cookie profile during a detach or an explicit write-back.</summary>
public enum WriteBackOutcome
{
    /// <summary>No write-back applies: a workspace mount, a hard-taint detach, or a mount that was already detached.</summary>
    NotAttempted,

    /// <summary>The guest copy was read back, hash-checked, validated and written to the host profile.</summary>
    Written,

    /// <summary>The guest copy failed a hash check or validation. The host profile is unchanged.</summary>
    Rejected,

    /// <summary>The guest could not be reached or the host swap failed. The host profile is unchanged.</summary>
    Failed,
}

/// <summary>Result of a detach. The mount is detached whenever <see cref="Detached"/> is true, whatever happened to the write-back.</summary>
/// <param name="GuestScrubbed">True when a hard-taint or kill-switch detach also ran <see cref="IGuestScrubber"/> successfully.</param>
public sealed record DetachResult(
    Guid MountId,
    bool Detached,
    bool WasAlreadyDetached,
    DetachReason Reason,
    WriteBackOutcome WriteBack,
    bool GuestScrubbed = false);

/// <summary>Result of <see cref="IMountManager.WriteBackProfileAsync"/>.</summary>
public sealed record WriteBackResult(Guid MountId, WriteBackOutcome Outcome, int FilesWritten, string? Detail);

/// <summary>Why the manager refused an operation. Stable names; callers and the audit log use them.</summary>
public enum MountRefusalReason
{
    /// <summary>The host or guest path is malformed: empty, relative, UNC, device, stream, trailing dot or space, reserved name, <c>..</c>.</summary>
    InvalidPath,

    /// <summary>The resolved host path is not inside the agent's own root for this kind of mount.</summary>
    OutsideAgentRoot,

    /// <summary>The host path is on, above or inside a denied location: audit, secrets, key material or another agent's folder.</summary>
    DeniedPath,

    /// <summary>A symbolic link or junction lies on the path or inside the mount tree and leads out of it, or sits on the path at all.</summary>
    LinkNotAllowed,

    /// <summary>The mount tree holds a file the content policy refuses (key material), or is too large to check, and so is not mounted.</summary>
    ContentRefused,

    /// <summary>The mode does not match the kind (Soul is read-only, workspace is read-write).</summary>
    ModeNotAllowed,

    /// <summary>The guest path is not the server's guest path for this kind and OS.</summary>
    GuestPathNotAllowed,

    /// <summary>The guest path already carries an attached mount on this VM.</summary>
    AlreadyAttached,

    /// <summary>The mount does not exist, is already detached, or is not a Soul mount.</summary>
    NotAttached,

    /// <summary>The VM does not belong to the agent, runs another OS than the request names, is retired, or does not exist. Also: the agent already has a Soul mount on another VM (single profile writer).</summary>
    VmNotEligible,
}

/// <summary>The manager refused an operation. Nothing was attached or changed.</summary>
public sealed class MountRefusedException(MountRefusalReason reason, string message) : Exception(message)
{
    public MountRefusalReason Reason { get; } = reason;
}
