namespace Bazaar.Server.VmMounts.Abstractions;

/// <summary>Where a file the policy is asked about sits.</summary>
public enum MountContentOrigin
{
    /// <summary>A file already inside a tree about to be mounted (scanned at attach).</summary>
    MountTree,

    /// <summary>A file of the cookie profile, copied into the guest or read back from it.</summary>
    ProfileFile,

    /// <summary>A canary this module is about to plant.</summary>
    Canary,
}

/// <summary>One file the content policy is asked to judge. <see cref="RelativePath"/> uses <c>/</c> separators.</summary>
public sealed record MountContentCandidate(MountKind Kind, MountContentOrigin Origin, string RelativePath, long Length);

/// <summary>The policy's answer. <see cref="Reason"/> is set when the file is refused and goes into the audit row.</summary>
public sealed record MountContentDecision(bool Allowed, string? Reason)
{
    public static MountContentDecision Allow { get; } = new(true, null);

    public static MountContentDecision Refuse(string reason) => new(false, reason);
}

/// <summary>
/// The content-side check: which files may sit in a mount or travel in the cookie profile. This module owns attach, detach, share
/// and profile copy and the host path jail; it does not own the allow-list. <c>Bazaar.Server.Identity.Mount</c> (MountAllowList,
/// MountedFolderWriter) already decides what may be written into the Soul mount, so it is the intended implementation and a
/// follow-up adapts it to this seam. Until then the module registers a conservative default (key material by name).
/// </summary>
public interface IMountContentPolicy
{
    MountContentDecision Evaluate(MountContentCandidate candidate);
}
