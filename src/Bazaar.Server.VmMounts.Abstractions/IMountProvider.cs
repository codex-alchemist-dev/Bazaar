namespace Bazaar.Server.VmMounts.Abstractions;

/// <summary>A mount the provider must expose or remove. Every path in it has already passed the manager's checks.</summary>
/// <param name="HostPath">Canonical, symlink-resolved host directory.</param>
/// <param name="GuestPath">The server-chosen guest path.</param>
public sealed record MountProviderRequest(
    Guid MountId,
    Guid AgentId,
    Guid VmId,
    GuestOs GuestOs,
    MountKind Kind,
    MountMode Mode,
    string HostPath,
    string GuestPath);

/// <summary>A canary file the provider should watch for reads. Paths are relative to the mount root, with <c>/</c> separators.</summary>
public sealed record CanaryWatch(Guid MountId, IReadOnlyList<string> RelativePaths);

/// <summary>A read of a watched canary file, as the provider saw it.</summary>
public sealed record CanaryAccess(Guid MountId, string RelativePath, DateTimeOffset At);

/// <summary>
/// The OS-specific part of a mount: the SMB share and ACL on a Windows host, the bind or export on a Linux host, and the mount
/// inside the guest. The manager calls it only after its own checks. The Windows and Linux providers are built later from the S8
/// runbooks on the owner's PC; the cloud tests use a directory provider and a fake that live in the test project.
/// </summary>
/// <remarks>
/// Contract for real providers: the share must be created so that it never follows a link (symbolic link, junction or reparse
/// point) out of the mount root, a Soul share must be read-only for the guest account, and the workspace share needs its own guest OS
/// account. The manager's tree scan at attach is advisory only: it is a point-in-time look, and a writable workspace can gain a link
/// or a hard link the moment after. The no-follow rule in the provider is the real control. <see cref="DetachAsync"/> must be idempotent and
/// must not need the guest to answer: it removes host-side access first.
/// </remarks>
public interface IMountProvider
{
    Task AttachAsync(MountProviderRequest request, CancellationToken ct);

    Task DetachAsync(MountProviderRequest request, CancellationToken ct);

    /// <summary>
    /// Starts reporting reads of the named canary files through <paramref name="onAccess"/> (SMB audit on Windows, fanotify or
    /// inotify on Linux). Disposing the result stops the watch. A provider that cannot observe reads returns a no-op watch.
    /// </summary>
    Task<IAsyncDisposable> WatchCanariesAsync(CanaryWatch watch, Func<CanaryAccess, ValueTask> onAccess, CancellationToken ct);
}
