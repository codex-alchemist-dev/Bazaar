using System.Text;

namespace Bazaar.Server.CodingEdit;

/// <summary>
/// In-memory worktree for tests and the deterministic tool sequences of doc 17, "Testing". Symlinks are simulated: a link resolves
/// inside the worktree, or resolution fails the way the guest agent must report it.
/// </summary>
/// <remarks>
/// Paths are worktree-relative with forward slashes. The fake does not jail paths; the tools jail before they call the port.
/// It counts writes and offers two hooks: <see cref="AfterReadHook"/> simulates a concurrent writer, and
/// <see cref="WriteFilter"/> simulates a guest that stores other bytes than it was given.
/// </remarks>
public sealed class InMemoryWorktreeFiles : IWorktreeFiles
{
    private const int _maxSymlinkHops = 40;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _links = new(StringComparer.Ordinal);
    private int _writeCount;

    /// <summary>Number of writes that reached the store, including restores.</summary>
    public int WriteCount
    {
        get
        {
            lock (_gate)
            {
                return _writeCount;
            }
        }
    }

    /// <summary>Runs after <see cref="ReadAsync"/> has captured its bytes, so a test can change the file the way a concurrent writer would.</summary>
    public Action<string>? AfterReadHook { get; set; }

    /// <summary>Maps (real path, bytes asked for) to the bytes the store keeps. Returning other bytes simulates a corrupt guest write.</summary>
    public Func<string, byte[], byte[]>? WriteFilter { get; set; }

    /// <summary>Stores UTF-8 text at a path, replacing any previous content.</summary>
    public void SetText(string relativePath, string text) => SetBytes(relativePath, Encoding.UTF8.GetBytes(text));

    /// <summary>Stores bytes at a path, replacing any previous content.</summary>
    public void SetBytes(string relativePath, byte[] bytes)
    {
        lock (_gate)
        {
            _files[string.Join('/', Segments(relativePath))] = bytes.ToArray();
        }
    }

    /// <summary>Creates a symlink. The target is worktree-relative, or absolute or drive-marked to simulate a link out of the worktree.</summary>
    public void CreateSymlink(string relativePath, string target)
    {
        lock (_gate)
        {
            _links[string.Join('/', Segments(relativePath))] = target;
        }
    }

    /// <summary>The UTF-8 text stored at a path, following symlinks. Null when no file is there. For test assertions.</summary>
    public string? GetText(string relativePath)
    {
        lock (_gate)
        {
            var real = ResolveLocked(relativePath);
            return real is not null && _files.TryGetValue(real, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;
        }
    }

    /// <inheritdoc />
    public Task<WorktreeFile?> ReadAsync(string relativePath, CancellationToken ct)
    {
        WorktreeFile? result;
        lock (_gate)
        {
            var real = ResolveLocked(relativePath);
            result = real is not null && _files.TryGetValue(real, out var bytes)
                ? new WorktreeFile(bytes.ToArray(), ContentHash.Sha256Hex(bytes))
                : null;
        }

        // The hook runs outside the lock so it may change the fake, as a concurrent writer would.
        AfterReadHook?.Invoke(relativePath);
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<string?> ResolveRealPathAsync(string relativePath, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(ResolveLocked(relativePath));
        }
    }

    /// <inheritdoc />
    public Task WriteAsync(string relativePath, byte[] content, string sha256, CancellationToken ct)
    {
        if (!string.Equals(ContentHash.Sha256Hex(content), sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The content does not hash to the given SHA-256.", nameof(sha256));
        }

        lock (_gate)
        {
            var real = ResolveLocked(relativePath)
                ?? throw new InvalidOperationException("The path resolves outside the worktree.");
            _files[real] = WriteFilter?.Invoke(real, content) ?? content.ToArray();
            _writeCount++;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string relativePath, CancellationToken ct)
    {
        lock (_gate)
        {
            var real = ResolveLocked(relativePath);
            return Task.FromResult(real is not null && _files.ContainsKey(real));
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(string relativePath, CancellationToken ct)
    {
        lock (_gate)
        {
            var real = ResolveLocked(relativePath);
            if (real is not null)
            {
                _files.Remove(real);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListAsync(string relativeDirectory, CancellationToken ct)
    {
        lock (_gate)
        {
            var directory = string.Join('/', Segments(relativeDirectory));
            var prefix = directory.Length == 0 ? "" : directory + "/";
            IReadOnlyList<string> listed = _files.Keys
                .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(listed);
        }
    }

    /// <summary>
    /// Resolves each segment in turn, following links. Returns null when a link target is absolute, a segment climbs above the root,
    /// or the link chain is too long (fail closed). Caller holds the lock.
    /// </summary>
    private string? ResolveLocked(string relativePath)
    {
        var work = Segments(relativePath).ToList();
        var resolved = new List<string>();
        var hops = 0;
        for (var i = 0; i < work.Count; i++)
        {
            var segment = work[i];
            if (segment == "..")
            {
                if (resolved.Count == 0)
                {
                    return null;
                }

                resolved.RemoveAt(resolved.Count - 1);
                continue;
            }

            resolved.Add(segment);
            if (!_links.TryGetValue(string.Join('/', resolved), out var target))
            {
                continue;
            }

            if (++hops > _maxSymlinkHops || IsAbsolute(target))
            {
                return null;
            }

            // The link is replaced by its target. Relative targets resolve from the link's parent, which is where resolved now ends.
            resolved.RemoveAt(resolved.Count - 1);
            work.InsertRange(i + 1, Segments(target));
        }

        return string.Join('/', resolved);
    }

    private static IEnumerable<string> Segments(string path) =>
        path.Split(['/', '\\']).Where(segment => segment.Length > 0 && segment != ".");

    private static bool IsAbsolute(string target) => target.Length > 0 && (target[0] is '/' or '\\' || target.Contains(':'));
}
