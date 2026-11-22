using System.Security.Cryptography;
using Bazaar.Server.CodingEdit;
using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingRunner.Handlers;

namespace Bazaar.Server.Host.Coding;

/// <summary>
/// A read-only, path-confined view of one worktree directory on the host (doc 17, host sources for worktree files). Every path is
/// checked lexically first (no absolute paths, <c>..</c>, backslashes, colons, device names, <c>.git</c>) and then resolved
/// component by component: a link whose target leaves the real worktree root makes the path unresolvable. Write and delete are not
/// offered, because edits go through the guest channel (doc 16).
/// </summary>
public sealed class ConfinedWorktreeFiles : IWorktreeFiles
{
    /// <summary>Most files one listing returns; a bigger tree is cut here, in sorted order.</summary>
    public const int MaxListedFiles = 100_000;

    private const int _maxLinkHops = 40;
    private static readonly HashSet<string> _deviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly string _root;

    /// <summary>Creates the view. <paramref name="realRoot"/> must be absolute with every link already resolved.</summary>
    public ConfinedWorktreeFiles(string realRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realRoot);
        _root = realRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// True when the path is a plain worktree-relative path: forward slashes, no empty, <c>.</c> or <c>..</c> segment, no drive or
    /// stream colon, no control character, no trailing dot or space, no Windows device name, no <c>.git</c> segment. The empty
    /// string (the root) is not a plain path; the callers that allow it check for it first.
    /// </summary>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 4096 || path[0] == '/')
        {
            return false;
        }

        foreach (var c in path)
        {
            if (c is '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c))
            {
                return false;
            }
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment[^1] is '.' or ' ' || segment[0] == ' ')
            {
                return false;
            }

            if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var dot = segment.IndexOf('.', StringComparison.Ordinal);
            var stem = dot < 0 ? segment : segment[..dot];
            if (_deviceNames.Contains(stem.TrimEnd(' ')))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">The path is not a plain worktree-relative path.</exception>
    /// <exception cref="UnauthorizedAccessException">The path resolves outside the worktree.</exception>
    public async Task<WorktreeFile?> ReadAsync(string relativePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireSafe(relativePath);
        var real = Resolve(relativePath) ?? throw new UnauthorizedAccessException("The path resolves outside the worktree.");
        var info = new FileInfo(real);
        if (!info.Exists)
        {
            return null;
        }

        // One byte past the read tool's cap is enough for it to report "too large" without loading a huge file.
        var limit = ReadToolHandler.MaxFileBytes + 1;
        var buffer = new byte[Math.Min(info.Length, limit)];
        await using var stream = new FileStream(real, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        var content = read == buffer.Length ? buffer : buffer[..read];
        return new WorktreeFile(content, Convert.ToHexStringLower(SHA256.HashData(content)));
    }

    /// <inheritdoc />
    public Task<string?> ResolveRealPathAsync(string relativePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsSafeRelativePath(relativePath))
        {
            return Task.FromResult<string?>(null);
        }

        var real = Resolve(relativePath);
        return Task.FromResult(real is null ? null : ToRelative(real));
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">This source is read-only.</exception>
    public Task WriteAsync(string relativePath, byte[] content, string sha256, CancellationToken ct) =>
        throw new NotSupportedException("The host file source is read-only; edits go through the guest channel (doc 16).");

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string relativePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsSafeRelativePath(relativePath))
        {
            return Task.FromResult(false);
        }

        var real = Resolve(relativePath);
        return Task.FromResult(real is not null && File.Exists(real));
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">This source is read-only.</exception>
    public Task DeleteAsync(string relativePath, CancellationToken ct) =>
        throw new NotSupportedException("The host file source is read-only; edits go through the guest channel (doc 16).");

    /// <inheritdoc />
    /// <exception cref="ArgumentException">The directory is not a plain worktree-relative path.</exception>
    /// <exception cref="UnauthorizedAccessException">The directory resolves outside the worktree.</exception>
    public Task<IReadOnlyList<string>> ListAsync(string relativeDirectory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string directory;
        if (relativeDirectory.Length == 0)
        {
            directory = _root;
        }
        else
        {
            RequireSafe(relativeDirectory);
            directory = Resolve(relativeDirectory) ?? throw new UnauthorizedAccessException("The path resolves outside the worktree.");
        }

        var files = new List<string>();
        if (Directory.Exists(directory))
        {
            Walk(directory, files, ct);
        }

        files.Sort(StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyList<string>>(files);
    }

    private void Walk(string directory, List<string> files, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0 && files.Count < MaxListedFiles)
        {
            ct.ThrowIfCancellationRequested();
            var current = pending.Pop();
            List<FileSystemInfo> entries;
            try
            {
                entries = [.. new DirectoryInfo(current).EnumerateFileSystemInfos()];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (!IsSafeRelativePath(entry.Name))
                {
                    continue;
                }

                if (entry is DirectoryInfo dir)
                {
                    // A directory link is never followed, so a loop or a way out cannot be walked.
                    if (dir.LinkTarget is null)
                    {
                        pending.Push(dir.FullName);
                    }

                    continue;
                }

                if (files.Count >= MaxListedFiles)
                {
                    return;
                }

                files.Add(ToRelative(entry.FullName));
            }
        }
    }

    private static void RequireSafe(string relativePath)
    {
        if (!IsSafeRelativePath(relativePath))
        {
            throw new ArgumentException("The path is not a plain worktree-relative path.", nameof(relativePath));
        }
    }

    /// <summary>
    /// Walks the path from the real root, replacing each link with its target and requiring every step to stay inside the root.
    /// Returns the absolute real path, or null when it leaves the root, loops, or has a missing component before the last one. A
    /// missing last component is allowed.
    /// </summary>
    private string? Resolve(string relativePath)
    {
        var pending = new Stack<string>(relativePath.Split('/').Reverse());
        var current = _root;
        var hops = 0;
        while (pending.Count > 0)
        {
            var segment = pending.Pop();
            var next = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.LinkTarget is not null)
            {
                if (++hops > _maxLinkHops)
                {
                    return null;
                }

                var target = Path.GetFullPath(Path.IsPathRooted(info.LinkTarget) ? info.LinkTarget : Path.Combine(current, info.LinkTarget));
                if (!Inside(target))
                {
                    return null;
                }

                // Re-walk the target's own components from the root, so a link inside the target is checked too.
                var rest = Path.GetRelativePath(_root, target).Replace('\\', '/');
                if (rest != ".")
                {
                    foreach (var part in rest.Split('/').Reverse())
                    {
                        pending.Push(part);
                    }
                }

                current = _root;
                continue;
            }

            if (!info.Exists && pending.Count > 0)
            {
                return null;
            }

            current = next;
        }

        return Inside(current) ? current : null;
    }

    private bool Inside(string path) => WorkSharePaths.IsEqualOrUnder(path, _root);

    private string ToRelative(string absolute)
    {
        var relative = Path.GetRelativePath(_root, absolute).Replace('\\', '/');
        return relative == "." ? string.Empty : relative;
    }
}
