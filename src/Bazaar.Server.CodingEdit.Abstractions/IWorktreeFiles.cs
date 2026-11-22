namespace Bazaar.Server.CodingEdit;

/// <summary>A file's bytes and their SHA-256 (lowercase hex).</summary>
/// <param name="Content">The file bytes.</param>
/// <param name="Sha256">SHA-256 of <paramref name="Content"/>.</param>
public sealed record WorktreeFile(byte[] Content, string Sha256);

/// <summary>
/// The worktree's files as the edit tools see them. Paths are worktree-relative with forward slashes. The port does not jail
/// paths itself: callers pass paths through <c>PathJail</c> first. Real implementations sit behind the guest channel (doc 16).
/// </summary>
public interface IWorktreeFiles
{
    /// <summary>Reads a file, following symlinks. Returns null when the file does not exist.</summary>
    Task<WorktreeFile?> ReadAsync(string relativePath, CancellationToken ct);

    /// <summary>
    /// Resolves every symlink in the path and returns the worktree-relative real path. Returns null when a link in the chain
    /// leaves the worktree, or when resolution fails (fail closed). A missing final component is allowed.
    /// </summary>
    Task<string?> ResolveRealPathAsync(string relativePath, CancellationToken ct);

    /// <summary>
    /// Writes a file, creating it when needed. The store must verify that <paramref name="content"/> hashes to
    /// <paramref name="sha256"/> and write atomically (temp file, then rename).
    /// </summary>
    Task WriteAsync(string relativePath, byte[] content, string sha256, CancellationToken ct);

    /// <summary>True when a file exists at the path, following symlinks.</summary>
    Task<bool> ExistsAsync(string relativePath, CancellationToken ct);

    /// <summary>Deletes a file. Deleting a missing file does nothing.</summary>
    Task DeleteAsync(string relativePath, CancellationToken ct);

    /// <summary>Lists every file under a directory, recursively, as sorted worktree-relative paths. The root is "".</summary>
    Task<IReadOnlyList<string>> ListAsync(string relativeDirectory, CancellationToken ct);
}
