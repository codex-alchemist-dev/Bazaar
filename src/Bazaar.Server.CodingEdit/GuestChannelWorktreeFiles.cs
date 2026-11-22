namespace Bazaar.Server.CodingEdit;

/// <summary>
/// Worktree files over the guest channel. Worktree-relative paths map to absolute guest paths under <paramref name="worktreeRoot"/>.
/// </summary>
/// <remarks>
/// The adapter refuses any path that is not a plain relative path, because a caller that skipped <see cref="PathJail"/> is a bug.
/// A resolved guest path outside the root is reported as null, which the jail turns into a refusal.
/// </remarks>
public sealed class GuestChannelWorktreeFiles(IGuestFileChannel channel, string worktreeRoot) : IWorktreeFiles
{
    private readonly string _root = ValidateRoot(worktreeRoot);

    /// <inheritdoc />
    public async Task<WorktreeFile?> ReadAsync(string relativePath, CancellationToken ct)
    {
        var bytes = await channel.GetFileAsync(GuestPathOf(relativePath), ct);
        return bytes is null ? null : new WorktreeFile(bytes, ContentHash.Sha256Hex(bytes));
    }

    /// <inheritdoc />
    public async Task<string?> ResolveRealPathAsync(string relativePath, CancellationToken ct)
    {
        var real = await channel.ResolveRealPathAsync(GuestPathOf(relativePath), ct);
        if (real is null)
        {
            return null;
        }

        if (real == _root)
        {
            return "";
        }

        return real.StartsWith(_root + "/", StringComparison.Ordinal) ? real[(_root.Length + 1)..] : null;
    }

    /// <inheritdoc />
    public Task WriteAsync(string relativePath, byte[] content, string sha256, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!string.Equals(ContentHash.Sha256Hex(content), sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The content does not hash to the given SHA-256.", nameof(sha256));
        }

        return channel.PutFileAsync(GuestPathOf(relativePath), content, sha256, ct);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string relativePath, CancellationToken ct) =>
        await channel.GetFileAsync(GuestPathOf(relativePath), ct) is not null;

    /// <inheritdoc />
    public Task DeleteAsync(string relativePath, CancellationToken ct) =>
        channel.DeleteFileAsync(GuestPathOf(relativePath), ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListAsync(string relativeDirectory, CancellationToken ct)
    {
        var listed = await channel.ListFilesAsync(GuestPathOf(relativeDirectory), ct);
        var prefix = _root + "/";
        return listed
            .Where(path => path.StartsWith(prefix, StringComparison.Ordinal))
            .Select(path => path[prefix.Length..])
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private string GuestPathOf(string relativePath)
    {
        // The empty path is the root, used only for listing. A file path must pass the lexical jail.
        if (relativePath.Length == 0)
        {
            return _root;
        }

        var lexical = PathJail.Lexical(relativePath);
        if (!lexical.Allowed)
        {
            throw new ArgumentException("The adapter was given a path that did not pass the lexical jail.", nameof(relativePath));
        }

        return _root + "/" + lexical.Path;
    }

    private static string ValidateRoot(string worktreeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreeRoot);
        if (worktreeRoot[0] != '/')
        {
            throw new ArgumentException("The worktree root must be an absolute guest path.", nameof(worktreeRoot));
        }

        return worktreeRoot.TrimEnd('/');
    }
}
