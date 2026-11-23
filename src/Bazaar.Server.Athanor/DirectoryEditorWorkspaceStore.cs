using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Bazaar.Server.Athanor;

/// <summary>
/// On-disk <see cref="IEditorWorkspaceStore"/> for Vault and git-worktree workspaces (doc 57 section 6). Roots come from an
/// <see cref="IEditorWorkspaceResolver"/>. Rules, in the order they apply:
/// <list type="bullet">
/// <item>The path is re-validated (<see cref="EditorPath"/>) and every segment is walked from the root: a segment that is a symlink or
/// reparse point (file or directory, inside or outside the root) refuses the path, as does a <c>.git</c> segment (hooks and config are not
/// editable here) and a Windows device name. Nothing is created: a missing file is <c>NotFound</c>.</item>
/// <item>A read refuses a file over the cap from its length, and never reads more than the cap plus one byte.</item>
/// <item>A write is compare-and-set under a per-file lock: the current bytes are hashed (SHA-256, lower-case hex, the revision), and only a
/// match writes a temp file in the same directory and renames it over the target, so a reader never sees half a file. The file mode is kept
/// on Unix. A read-only workspace refuses every write.</item>
/// </list>
/// The checks run before and after the file is opened, so a link swapped in during the call is caught in most cases; a swap in the few
/// microseconds between the last check and the rename is not excluded (the server's own users are the only writers to a workspace).
/// The lock covers this process; the server runs one instance per install.
/// </summary>
public sealed class DirectoryEditorWorkspaceStore : IEditorWorkspaceStore
{
    private static readonly HashSet<string> _deviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly IEditorWorkspaceResolver _resolver;
    private readonly IAthanorLimits _limits;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public DirectoryEditorWorkspaceStore(IEditorWorkspaceResolver resolver, IAthanorLimits limits)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(limits);
        _resolver = resolver;
        _limits = limits;
    }

    /// <summary>The revision of the given content: lower-case hex SHA-256.</summary>
    public static string RevisionOf(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));

    public async ValueTask<EditorWorkspaceInfo?> GetWorkspaceAsync(string workspaceId, CancellationToken cancellationToken)
    {
        var root = await _resolver.ResolveAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return null;
        }

        if (!Directory.Exists(root.RootPath))
        {
            throw new EditorStoreUnavailableException("The workspace directory is missing.");
        }

        return new EditorWorkspaceInfo(root.Id, root.Kind, root.ReadOnly);
    }

    public async ValueTask<EditorReadResult> ReadAsync(string workspaceId, string path, long maxBytes, CancellationToken cancellationToken)
    {
        var root = await RequireRootAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var target = Locate(root, path);
        if (target is null)
        {
            return new EditorReadResult(EditorReadStatus.Blocked);
        }

        try
        {
            if (!File.Exists(target))
            {
                return new EditorReadResult(EditorReadStatus.NotFound);
            }

            await using var stream = OpenRead(target);
            if (!ChainIsClean(root, path))
            {
                return new EditorReadResult(EditorReadStatus.Blocked);
            }

            if (stream.Length > maxBytes)
            {
                return new EditorReadResult(EditorReadStatus.TooLarge, stream.Length);
            }

            var bytes = await ReadAtMostAsync(stream, maxBytes, cancellationToken).ConfigureAwait(false);
            return bytes is null
                ? new EditorReadResult(EditorReadStatus.TooLarge, maxBytes + 1)
                : new EditorReadResult(EditorReadStatus.Ok, bytes.LongLength, RevisionOf(bytes), bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new EditorStoreUnavailableException("The file could not be read.", e);
        }
    }

    public async ValueTask<EditorWriteResult> WriteAsync(string workspaceId, string path, string baseRevision, byte[] content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var root = await RequireRootAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        if (root.ReadOnly)
        {
            return new EditorWriteResult(EditorWriteStatus.Blocked);
        }

        var target = Locate(root, path);
        if (target is null)
        {
            return new EditorWriteResult(EditorWriteStatus.Blocked);
        }

        var gate = _locks.GetOrAdd(target, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await WriteLockedAsync(root, path, target, baseRevision, content, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new EditorStoreUnavailableException("The file could not be written.", e);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<EditorWriteResult> WriteLockedAsync(
        EditorWorkspaceRoot root, string path, string target, string baseRevision, byte[] content, CancellationToken cancellationToken)
    {
        if (!File.Exists(target))
        {
            return new EditorWriteResult(EditorWriteStatus.NotFound);
        }

        byte[]? current;
        await using (var stream = OpenRead(target))
        {
            if (!ChainIsClean(root, path))
            {
                return new EditorWriteResult(EditorWriteStatus.Blocked);
            }

            current = stream.Length > _limits.MaxOpenBytes
                ? null
                : await ReadAtMostAsync(stream, _limits.MaxOpenBytes, cancellationToken).ConfigureAwait(false);
        }

        if (current is null)
        {
            return new EditorWriteResult(EditorWriteStatus.TooLarge);
        }

        var currentRevision = RevisionOf(current);
        if (!string.Equals(currentRevision, baseRevision, StringComparison.Ordinal))
        {
            return new EditorWriteResult(EditorWriteStatus.Conflict, ServerRevision: currentRevision, ServerContent: current);
        }

        var directory = Path.GetDirectoryName(target)!;
        var temp = Path.Combine(directory, ".athanor-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = new FileStream(
                temp,
                new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous | FileOptions.WriteThrough }))
            {
                await output.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, File.GetUnixFileMode(target));
            }

            // Last look before the rename: a link planted since the first check refuses the write.
            if (!ChainIsClean(root, path))
            {
                return new EditorWriteResult(EditorWriteStatus.Blocked);
            }

            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        return new EditorWriteResult(EditorWriteStatus.Written, NewRevision: RevisionOf(content));
    }

    private static FileStream OpenRead(string target) => new(
        target,
        new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete, Options = FileOptions.Asynchronous });

    private async ValueTask<EditorWorkspaceRoot> RequireRootAsync(string workspaceId, CancellationToken cancellationToken)
    {
        var root = await _resolver.ResolveAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        if (root is null || !Directory.Exists(root.RootPath))
        {
            throw new EditorStoreUnavailableException("The workspace is not available.");
        }

        return root;
    }

    /// <summary>The full path of a file inside the root, or null when the path is refused (see the class summary).</summary>
    private static string? Locate(EditorWorkspaceRoot root, string path)
    {
        if (!EditorPath.TryNormalize(path, out var normalized, out _) || !string.Equals(normalized, path, StringComparison.Ordinal))
        {
            return null;
        }

        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.RootPath));
        var segments = path.Split('/');
        if (segments.Any(IsRefusedSegment))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(rootFull, string.Join(Path.DirectorySeparatorChar, segments)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, comparison))
        {
            return null;
        }

        return ChainIsClean(root, path) ? full : null;
    }

    private static bool IsRefusedSegment(string segment)
    {
        if (string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var dot = segment.IndexOf('.', StringComparison.Ordinal);
        var stem = dot < 0 ? segment : segment[..dot];
        return _deviceNames.Contains(stem);
    }

    /// <summary>True when no segment from the root down to the file is a symlink or reparse point. A missing segment is clean (it is NotFound later).</summary>
    private static bool ChainIsClean(EditorWorkspaceRoot root, string path)
    {
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.RootPath));
        foreach (var segment in path.Split('/'))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists && info.LinkTarget is null)
            {
                return true;
            }

            if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<byte[]?> ReadAtMostAsync(Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
