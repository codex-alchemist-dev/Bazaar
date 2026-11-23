using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmMounts.Paths;

namespace Bazaar.Server.VmMounts.Providers;

/// <summary>
/// The provider-side re-check of a <see cref="MountProviderRequest"/>. The manager has already validated it; a provider is the last
/// code before a host directory is exposed, so it checks again and fails closed: ids set, Soul read-only, and the host path accepted by
/// the same <see cref="HostPathGuard"/> the manager uses and equal to the canonical path the guard returns.
/// </summary>
/// <remarks>The guard touches the file system on construction, so it is built on first use, not when the provider is resolved.</remarks>
internal sealed class MountRequestValidator(VmMountOptions options)
{
    private readonly Lazy<HostPathGuard> _guard = new(() => new HostPathGuard(options), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Returns the canonical mount root, or throws <see cref="MountRefusedException"/>.</summary>
    public string ValidateAttach(MountProviderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIds(request);
        if (!Enum.IsDefined(request.Kind) || !Enum.IsDefined(request.Mode) || !Enum.IsDefined(request.GuestOs))
            throw new MountRefusedException(MountRefusalReason.ModeNotAllowed, "The mount kind, mode or guest OS is not a known value.");

        // Never widen: a Soul share is read-only for the guest whatever the caller asked.
        if (request.Kind == MountKind.Soul && request.Mode != MountMode.Ro)
            throw new MountRefusedException(MountRefusalReason.ModeNotAllowed, "A Soul mount is read-only.");

        GuardedPath guarded;
        try
        {
            guarded = _guard.Value.Check(request.AgentId, request.Kind, request.HostPath);
        }
        catch (ArgumentException)
        {
            // The guard trims trailing separators before it normalises, so a path that is only separators ("/") reaches GetFullPath empty.
            // Whatever the cause, a path the guard cannot read is a refusal, not an unhandled error.
            throw new MountRefusedException(MountRefusalReason.InvalidPath, "The host path could not be checked.");
        }

        var comparison = options.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!guarded.MountRoot.Equals(request.HostPath.TrimEnd('/', '\\'), comparison))
            throw new MountRefusedException(MountRefusalReason.InvalidPath, "The host path is not the canonical mount root.");
        return guarded.MountRoot;
    }

    /// <summary>The checks a detach needs: only the ids, because removing access must work even when the host path is gone.</summary>
    public static void ValidateIds(MountProviderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MountId == Guid.Empty || request.AgentId == Guid.Empty || request.VmId == Guid.Empty)
            throw new MountRefusedException(MountRefusalReason.InvalidPath, "The mount, agent and VM ids must be set.");
    }
}
