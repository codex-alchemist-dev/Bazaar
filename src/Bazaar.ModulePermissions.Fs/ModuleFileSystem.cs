namespace Bazaar.ModulePermissions.Fs;

/// <summary>
/// The only filesystem access a hosted module is given (doc 48 section 1: a call outside the approved set is refused and
/// audited). A module path is <c>root/segment/segment</c> with forward slashes, where <c>root</c> is a name the host supplied
/// in <see cref="ModuleFsRoots"/>. Each call, in this order: the path is checked lexically; the full module path is demanded
/// from the gate as an <c>fs</c> access; the path is resolved under the root and refused if any existing part of it is a
/// symbolic link, junction or other reparse point, or if it would leave the root; then the file is opened without sharing
/// for writes. Anything unclear is a refusal.
/// </summary>
/// <remarks>
/// Doc 48 has one <c>fs</c> class, so one grant covers reading and writing: there is no read/write split here. A path that
/// names only a root is accepted by <see cref="Enumerate"/> alone, and needs a grant of exactly that root name.
/// Residual weakness: a link that something outside this class plants between the walk and the open is caught by a second walk
/// after the open (the handle is then closed), not prevented. Only the host and the operator can create links under a root.
/// </remarks>
public sealed class ModuleFileSystem
{
    private static readonly StringComparison _pathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly IModulePermissionGate _gate;
    private readonly string _moduleId;
    private readonly ModuleFsRoots _roots;
    private readonly ModuleFsOptions _options;

    /// <summary>Creates the file system of one module.</summary>
    public ModuleFileSystem(IModulePermissionGate gate, string moduleId, ModuleFsRoots roots, ModuleFsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentNullException.ThrowIfNull(roots);
        _gate = gate;
        _moduleId = moduleId;
        _roots = roots;
        _options = options ?? new ModuleFsOptions();
        if (_options.MaxPathLength < 1 || _options.MaxSegments < 1 || _options.MaxFileBytes < 0 || _options.MaxEnumerateEntries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "A limit must not be negative or zero.");
        }
    }

    /// <summary>Opens a file for reading. Other readers may share it; writers may not.</summary>
    public FileStream OpenRead(string path) =>
        Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>Opens a file for writing, creating it or truncating it. The directory must exist. No one else may open it meanwhile.</summary>
    public FileStream OpenWrite(string path) =>
        Open(path, FileMode.Create, FileAccess.Write, FileShare.None);

    /// <summary>Opens a file for appending, creating it when missing. The directory must exist. No one else may open it meanwhile.</summary>
    public FileStream OpenAppend(string path) =>
        Open(path, FileMode.Append, FileAccess.Write, FileShare.None);

    /// <summary>Reads a whole file. A file larger than <see cref="ModuleFsOptions.MaxFileBytes"/> is refused.</summary>
    public async ValueTask<byte[]> ReadAllBytesAsync(string path, CancellationToken ct = default)
    {
        await using var stream = Open(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        try
        {
            if (stream.Length > _options.MaxFileBytes)
            {
                throw Refuse("The file is larger than the limit.");
            }

            var buffer = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
            // A file that grew since its length was read is not a whole read; refuse it instead of returning a part.
            if (stream.ReadByte() >= 0)
            {
                throw Refuse("The file changed while it was read.");
            }

            return buffer;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException and not ModuleFsRefusedException)
        {
            throw Refuse("The file could not be read.");
        }
    }

    /// <summary>Writes a whole file, creating it or replacing it. Data larger than <see cref="ModuleFsOptions.MaxFileBytes"/> is refused.</summary>
    public async ValueTask WriteAllBytesAsync(string path, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (data.Length > _options.MaxFileBytes)
        {
            throw Refuse("The data is larger than the limit.");
        }

        await using var stream = Open(path, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.Asynchronous);
        try
        {
            await stream.WriteAsync(data, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException and not ModuleFsRefusedException)
        {
            throw Refuse("The file could not be written.");
        }
    }

    /// <summary>True when a file or directory exists at the path. A path that passes through a link is refused, not false.</summary>
    public bool Exists(string path)
    {
        var full = Resolve(path, allowRootOnly: false);
        return Guard(() => File.Exists(full) || Directory.Exists(full));
    }

    /// <summary>Deletes a file. A missing file is not an error; a directory is refused.</summary>
    public void Delete(string path)
    {
        var full = Resolve(path, allowRootOnly: false);
        Guard(() =>
        {
            if (Directory.Exists(full))
            {
                throw Refuse("The path is a directory.");
            }

            File.Delete(full);
            return true;
        });
    }

    /// <summary>Creates a directory and any missing directories above it, all below the root.</summary>
    public void CreateDirectory(string path)
    {
        var full = Resolve(path, allowRootOnly: false);
        Guard(() =>
        {
            Directory.CreateDirectory(full);
            RecheckLinks(path);
            return true;
        });
    }

    /// <summary>
    /// Lists the names (not paths) in one directory, sorted, without descending. Links are neither returned nor followed, and
    /// so are names the module could not address. A directory with more entries than the limit is refused.
    /// </summary>
    public IReadOnlyList<string> Enumerate(string path)
    {
        var full = Resolve(path, allowRootOnly: true);
        return Guard(() =>
        {
            if (!Directory.Exists(full))
            {
                throw Refuse("The directory does not exist.");
            }

            var names = new List<string>();
            foreach (var entry in new DirectoryInfo(full).EnumerateFileSystemInfos())
            {
                if (IsLink(entry) || !ModuleFsPathRules.IsValidSegment(entry.Name))
                {
                    continue;
                }

                if (names.Count >= _options.MaxEnumerateEntries)
                {
                    throw Refuse("The directory has more entries than the limit.");
                }

                names.Add(entry.Name);
            }

            names.Sort(StringComparer.Ordinal);
            return (IReadOnlyList<string>)names;
        });
    }

    /// <summary>
    /// Moves a file or directory inside one root. Both ends are checked against the gate and both are confined; the
    /// destination must not exist. A move from one root to another is refused.
    /// </summary>
    public void Move(string source, string destination)
    {
        var from = Parse(source, allowRootOnly: false);
        var to = Parse(destination, allowRootOnly: false);
        if (!string.Equals(from.Root, to.Root, StringComparison.Ordinal))
        {
            // Still demand both so the attempt is audited when a grant is missing, then refuse the crossing either way.
            Demand(source);
            Demand(destination);
            throw Refuse("A move must stay inside one root.");
        }

        var fullFrom = Confine(source, from);
        var fullTo = Confine(destination, to);
        Guard(() =>
        {
            if (File.Exists(fullFrom))
            {
                File.Move(fullFrom, fullTo, overwrite: false);
            }
            else if (Directory.Exists(fullFrom))
            {
                Directory.Move(fullFrom, fullTo);
            }
            else
            {
                throw Refuse("The source does not exist.");
            }

            return true;
        });
    }

    private FileStream Open(string path, FileMode mode, FileAccess access, FileShare share, FileOptions options = FileOptions.None)
    {
        var full = Resolve(path, allowRootOnly: false);
        FileStream? stream = null;
        try
        {
            if (Directory.Exists(full))
            {
                throw Refuse("The path is a directory.");
            }

            stream = new FileStream(full, new FileStreamOptions { Mode = mode, Access = access, Share = share, Options = options, BufferSize = 4096 });
            // Second walk with the handle open: a link that appeared after the first walk is caught here and the handle closed.
            RecheckLinks(path);
            var opened = stream;
            stream = null;
            return opened;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException and not ModuleFsRefusedException)
        {
            throw Refuse("The file could not be opened.");
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private string Resolve(string path, bool allowRootOnly) => Confine(path, Parse(path, allowRootOnly));

    private void Demand(string path) => _gate.Demand(_moduleId, new ModuleAccess(ModulePermissionClasses.FileSystem, path));

    private (string Root, string[] Segments) Parse(string path, bool allowRootOnly)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length > _options.MaxPathLength)
        {
            throw Refuse("The path is longer than the limit.");
        }

        // A target the gate cannot match is audited as an invalid target; the gate drops the text and keeps only its length.
        if (!ModuleTargetRules.IsValidTarget(path))
        {
            Demand(path);
            throw Refuse("The path is not a clean module path.");
        }

        if (path[0] == '/' || path[^1] == '/')
        {
            throw Refuse("The path must be written root/segment/segment, with no leading or trailing slash.");
        }

        var parts = path.Split('/');
        if (parts.Length > _options.MaxSegments)
        {
            throw Refuse("The path has more segments than the limit.");
        }

        foreach (var part in parts)
        {
            if (!ModuleFsPathRules.IsValidSegment(part))
            {
                throw Refuse("A path segment is not a safe file name.");
            }
        }

        if (parts.Length == 1 && !allowRootOnly)
        {
            throw Refuse("The path must name something below a root.");
        }

        return (parts[0], parts[1..]);
    }

    private string Confine(string path, (string Root, string[] Segments) parsed)
    {
        Demand(path);
        if (!_roots.TryGetDirectory(parsed.Root, out var rootFull))
        {
            throw Refuse("The root is not known.");
        }

        if (!Directory.Exists(rootFull))
        {
            throw Refuse("The root directory is not available.");
        }

        var full = parsed.Segments.Length == 0 ? rootFull : Path.GetFullPath(Path.Combine([rootFull, .. parsed.Segments]));
        var prefix = rootFull + Path.DirectorySeparatorChar;
        if (parsed.Segments.Length > 0 && !full.StartsWith(prefix, _pathComparison))
        {
            throw Refuse("The path leaves its root.");
        }

        var current = rootFull;
        foreach (var segment in parsed.Segments)
        {
            current = Path.Combine(current, segment);
            if (IsLink(new FileInfo(current)) || IsLink(new DirectoryInfo(current)))
            {
                throw Refuse("The path passes through a link.");
            }
        }

        return full;
    }

    // Walks the path again; the demand was already made, so this only repeats the link check.
    private void RecheckLinks(string path)
    {
        var parsed = Parse(path, allowRootOnly: false);
        if (!_roots.TryGetDirectory(parsed.Root, out var current))
        {
            throw Refuse("The root is not known.");
        }

        foreach (var segment in parsed.Segments)
        {
            current = Path.Combine(current, segment);
            if (IsLink(new FileInfo(current)) || IsLink(new DirectoryInfo(current)))
            {
                throw Refuse("The path passes through a link.");
            }
        }
    }

    // LinkTarget is read whether or not the target exists, so a dangling link is a link too. A reparse point that is not a
    // symbolic link or junction (a cloud placeholder, a mount point) is refused as well.
    private static bool IsLink(FileSystemInfo info)
    {
        try
        {
            if (info.LinkTarget is not null)
            {
                return true;
            }

            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException and not ModuleFsRefusedException)
        {
            throw Refuse("The file operation failed.");
        }
    }

    private static ModuleFsRefusedException Refuse(string reason) => new(reason);
}
