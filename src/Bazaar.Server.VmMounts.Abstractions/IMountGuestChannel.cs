namespace Bazaar.Server.VmMounts.Abstractions;

/// <summary>A regular file in a guest directory, as the guest reports it. <see cref="Sha256"/> is lower-case hex computed by the guest.</summary>
public sealed record GuestFileInfo(string RelativePath, long Length, string Sha256);

/// <summary>
/// The smallest guest wire this module needs. The transport module (doc 16 WP7, built in parallel) owns <c>IGuestChannel</c>; an
/// adapter there maps these four calls onto its <c>PutFile</c>, <c>GetFile</c> and <c>Exec</c>. <see cref="ListFilesAsync"/> and
/// <see cref="DeleteDirectoryAsync"/> are the only calls beyond the file transfer in the doc 16 interface sketch.
/// </summary>
public interface IMountGuestChannel
{
    /// <summary>Writes a file in the guest. The guest verifies <paramref name="sha256"/> and refuses a mismatch.</summary>
    Task PutFileAsync(Guid vmId, string guestPath, Stream content, string sha256, CancellationToken ct);

    /// <summary>Reads a guest file into <paramref name="sink"/>.</summary>
    Task GetFileAsync(Guid vmId, string guestPath, Stream sink, CancellationToken ct);

    /// <summary>Lists the regular files under a guest directory, recursively. Links and special files are not listed.</summary>
    Task<IReadOnlyList<GuestFileInfo>> ListFilesAsync(Guid vmId, string guestDirectory, CancellationToken ct);

    /// <summary>Removes a guest directory and everything in it. A missing directory is not an error.</summary>
    Task DeleteDirectoryAsync(Guid vmId, string guestDirectory, CancellationToken ct);
}
