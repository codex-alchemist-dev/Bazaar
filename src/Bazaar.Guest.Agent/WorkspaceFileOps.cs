using System.Text.Json;
using Bazaar.Contracts.Vms;

namespace Bazaar.Guest.Agent;

/// <summary>
/// DeleteFile, ListFiles and ResolveRealPath (protocol version 3). Every path is relative to the primary allowed root
/// (the worktree root). The guest does its own checks: absolute paths and <c>..</c> are refused, symlinks are followed
/// before the root check, and anything that leaves the root fails closed. Each request gets exactly one answer.
/// </summary>
internal sealed class WorkspaceFileOps
{
    /// <summary>Most entries one listing carries. Past it the answer is an error, so the frame stays under its limit.</summary>
    internal const int MaxListEntries = 2000;

    private const string _outsideRoot = "the path is outside the worktree root";
    private const string _noRoot = "no worktree root is configured";

    private readonly PathGuard _paths;
    private readonly TextWriter _log;

    /// <summary>Creates the handler.</summary>
    public WorkspaceFileOps(PathGuard paths, TextWriter log)
    {
        _paths = paths;
        _log = log;
    }

    /// <summary>Deletes one file or one symlink (never a link's target), and answers with a <see cref="DeleteFileResult"/>.</summary>
    public ValueTask DeleteAsync(DeleteFile request, OutboundQueue outbound, CancellationToken ct) =>
        outbound.SendAsync(new DeleteFileResult(request.Path, Delete(request.Path, out var error), error), ct);

    /// <summary>Lists one directory, non-recursive, and answers with a <see cref="ListFilesResult"/>.</summary>
    public ValueTask ListAsync(ListFiles request, OutboundQueue outbound, CancellationToken ct) =>
        outbound.SendAsync(new ListFilesResult(request.Dir, List(request.Dir, out var error), error), ct);

    /// <summary>Resolves symlinks and answers with a <see cref="ResolveRealPathResult"/>.</summary>
    public ValueTask ResolveAsync(ResolveRealPath request, OutboundQueue outbound, CancellationToken ct)
    {
        string? error = null;
        string? real = null;
        if (_paths.PrimaryRoot is null)
        {
            error = _noRoot;
        }
        else if (!PathGuard.IsSafeRelative(request.Path, allowRoot: true))
        {
            error = "the path is refused: it must be relative and must not contain '..'";
        }
        else if (_paths.ResolveRelative(request.Path, includeRoot: true) is { } resolved)
        {
            real = _paths.ToRelative(resolved);
        }

        if (real is null)
        {
            _log.WriteLine($"ResolveRealPath for '{request.Path}' gave no path.");
        }

        return outbound.SendAsync(new ResolveRealPathResult(request.Path, real, error), ct);
    }

    private bool Delete(string path, out string? error)
    {
        error = null;
        if (_paths.PrimaryRoot is null)
        {
            error = _noRoot;
            return false;
        }

        if (!PathGuard.IsSafeRelative(path, allowRoot: false))
        {
            error = "the path is refused: it must be relative, name a file and not contain '..'";
            return false;
        }

        // Resolve the parent, not the leaf: a symlink leaf is removed itself, never followed to its target.
        var trimmed = path.Replace('\\', '/').TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        var leaf = trimmed[(slash + 1)..];
        var parent = _paths.ResolveRelative(slash < 0 ? string.Empty : trimmed[..slash], includeRoot: true);
        if (parent is null || leaf is "." or "")
        {
            error = parent is null ? _outsideRoot : "the path does not name a file";
            return false;
        }

        var target = Path.Combine(parent, leaf);
        try
        {
            var info = new FileInfo(target);
            var isLink = info.LinkTarget is not null;
            if (!isLink && Directory.Exists(target))
            {
                error = "the path is a directory";
                return false;
            }

            if (!isLink && !info.Exists)
            {
                error = "the file is missing";
                return false;
            }

            if (isLink && OperatingSystem.IsWindows() && Directory.Exists(target))
            {
                Directory.Delete(target);
            }
            else
            {
                File.Delete(target);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.WriteLine($"DeleteFile could not remove '{path}': {ex.Message}");
            error = "the file could not be removed";
            return false;
        }
    }

    private List<GuestFileEntry> List(string dir, out string? error)
    {
        error = null;
        if (_paths.PrimaryRoot is null)
        {
            error = _noRoot;
            return [];
        }

        if (!PathGuard.IsSafeRelative(dir, allowRoot: true))
        {
            error = "the path is refused: it must be relative and must not contain '..'";
            return [];
        }

        var real = _paths.ResolveRelative(dir, includeRoot: true);
        if (real is null)
        {
            error = _outsideRoot;
            return [];
        }

        try
        {
            var entries = new List<GuestFileEntry>();
            foreach (var info in new DirectoryInfo(real).EnumerateFileSystemInfos())
            {
                if (entries.Count >= MaxListEntries)
                {
                    error = $"the directory has more than {MaxListEntries} entries";
                    return [];
                }

                var isDirectory = info is DirectoryInfo;
                entries.Add(new GuestFileEntry(info.Name, isDirectory ? 0 : ((FileInfo)info).Length, isDirectory));
            }

            entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            var size = JsonSerializer.SerializeToUtf8Bytes(new ListFilesResult(dir, entries, null), GuestJsonContext.Default.ListFilesResult).Length;
            if (size > GuestFrameCodec.MaxFrameBytes - 1024)
            {
                error = "the directory listing is too large for one frame";
                return [];
            }

            return entries;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.WriteLine($"ListFiles could not read '{dir}': {ex.Message}");
            error = "the directory is missing or cannot be read";
            return [];
        }
    }
}
