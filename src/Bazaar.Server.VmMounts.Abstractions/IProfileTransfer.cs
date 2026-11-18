namespace Bazaar.Server.VmMounts.Abstractions;

/// <summary>One file of a transferred profile. <see cref="RelativePath"/> uses <c>/</c> separators.</summary>
public sealed record ProfileFile(string RelativePath, long Length, string Sha256);

/// <summary>The files of a profile copy, sorted by path, with one digest over the whole list.</summary>
public sealed record ProfileManifest(IReadOnlyList<ProfileFile> Files, string Digest)
{
    public int Count => Files.Count;

    public long TotalBytes => Files.Sum(f => f.Length);
}

/// <summary>Why a profile transfer was rejected.</summary>
public enum ProfileTransferFailure
{
    /// <summary>A file's bytes do not match the hash or length given for it.</summary>
    HashMismatch,

    /// <summary>The profile broke a rule: unsafe path, refused content, a link, too many files, or too many bytes.</summary>
    Invalid,

    /// <summary>The guest channel failed.</summary>
    Channel,
}

/// <summary>A profile transfer failed. Nothing partial is left in the destination.</summary>
public sealed class ProfileTransferException(ProfileTransferFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public ProfileTransferFailure Failure { get; } = failure;
}

/// <summary>
/// Moves the browser cookie profile between the host Soul folder and the guest's local working copy (doc 16 Conflicts 1). The guest
/// is untrusted: everything read back is hash-checked and validated before it lands host-side.
/// </summary>
/// <remarks>
/// Validation is structural only: safe paths, no aliasing, size and count limits, the content policy's name rules, and bytes that match
/// what the guest listed. It cannot tell whether the cookie database itself is benign. A compromised guest can write any cookies, and
/// the host copies them to the profile on a clean detach. The defences are that a hard-taint session is never written back, and that
/// the hash only guards against corruption, not against a lying guest.
/// </remarks>
public interface IProfileTransfer
{
    /// <summary>Replaces the guest working copy with the host profile, file by file, each hash-checked. Returns what was copied.</summary>
    Task<ProfileManifest> PushAsync(Guid vmId, GuestOs guestOs, string hostProfileDirectory, string guestProfileDirectory, CancellationToken ct);

    /// <summary>
    /// Reads the guest working copy into <paramref name="hostStagingDirectory"/>, which must be empty or absent. On any failure the
    /// staging directory is removed and a <see cref="ProfileTransferException"/> is thrown. The caller swaps a good result into place.
    /// </summary>
    Task<ProfileManifest> PullAsync(Guid vmId, GuestOs guestOs, string guestProfileDirectory, string hostStagingDirectory, CancellationToken ct);
}
