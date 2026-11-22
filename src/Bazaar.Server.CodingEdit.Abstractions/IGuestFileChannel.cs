namespace Bazaar.Server.CodingEdit;

/// <summary>
/// The guest file operations the worktree adapter needs. Paths are absolute guest paths. This is a local port: the server
/// composition adapts it to the guest channel (doc 16). The current <c>IGuestChannel</c> in Bazaar.Server has only exec and
/// file put and get, so the composition must add the other operations to the guest protocol.
/// </summary>
public interface IGuestFileChannel
{
    /// <summary>Reads a file. Returns null when it does not exist.</summary>
    Task<byte[]?> GetFileAsync(string guestPath, CancellationToken ct);

    /// <summary>Writes a file. The guest rejects content whose SHA-256 (hex) does not match <paramref name="sha256"/>.</summary>
    Task PutFileAsync(string guestPath, byte[] content, string sha256, CancellationToken ct);

    /// <summary>Deletes a file. Deleting a missing file does nothing.</summary>
    Task DeleteFileAsync(string guestPath, CancellationToken ct);

    /// <summary>Lists the files under a directory, recursively, as absolute guest paths.</summary>
    Task<IReadOnlyList<string>> ListFilesAsync(string guestDirectory, CancellationToken ct);

    /// <summary>
    /// Resolves every symlink in an absolute guest path and returns the resolved absolute path. Returns null when the chain cannot
    /// be resolved. The guest agent does this inside the worktree (doc 17, "Behaviour": symlinks that leave the worktree are refused).
    /// </summary>
    Task<string?> ResolveRealPathAsync(string guestPath, CancellationToken ct);
}
