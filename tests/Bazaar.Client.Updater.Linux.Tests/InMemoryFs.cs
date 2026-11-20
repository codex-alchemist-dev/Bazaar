namespace Bazaar.Client.Updater.Linux.Tests;

/// <summary>A small in-memory file system with files, directories and symbolic links, plus a log of mutating calls.</summary>
internal sealed class InMemoryFs : IUpdateFileSystem
{
    private enum Kind { File, Directory, Link }

    private sealed record Node(Kind Kind, byte[]? Data = null, string? Target = null, UnixFileMode Mode = default);

    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);

    /// <summary>Mutating calls in order, such as <c>rename:/a.tmp-1-&gt;/a</c>.</summary>
    public List<string> Log { get; } = [];

    public UnixFileMode? ModeOf(string path) => _nodes.TryGetValue(path, out var n) ? n.Mode : null;

    public byte[]? Content(string path) => _nodes.TryGetValue(path, out var n) ? n.Data : null;

    public bool Exists(string path) => _nodes.ContainsKey(path);

    public bool DirectoryExists(string path)
    {
        if (!_nodes.TryGetValue(path, out var node))
        {
            return false;
        }

        return node.Kind switch
        {
            Kind.Directory => true,
            Kind.Link => DirectoryExists(Normalize(UnixPath.Combine(UnixPath.GetDirectoryName(path)!, node.Target!))),
            _ => false,
        };
    }

    public bool FileExists(string path) => _nodes.TryGetValue(path, out var n) && n.Kind == Kind.File;

    public void CreateDirectory(string path, UnixFileMode mode)
    {
        string? current = path;
        var chain = new List<string>();
        while (!string.IsNullOrEmpty(current) && current != "/")
        {
            chain.Add(current);
            current = UnixPath.GetDirectoryName(current);
        }

        foreach (string dir in chain.AsEnumerable().Reverse())
        {
            if (!_nodes.ContainsKey(dir))
            {
                _nodes[dir] = new Node(Kind.Directory, Mode: mode);
                Log.Add("mkdir:" + dir);
            }
        }
    }

    public Stream OpenRead(string path) =>
        _nodes.TryGetValue(path, out var n) && n.Kind == Kind.File
            ? new MemoryStream(n.Data!, writable: false)
            : throw new FileNotFoundException(path);

    /// <summary>Resolves "." and ".." segments of an absolute path with '/' separators on every host.</summary>
    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (segment != ".")
            {
                parts.Add(segment);
            }
        }

        return "/" + string.Join('/', parts);
    }

    public Stream CreateFile(string path)
    {
        if (_nodes.ContainsKey(path))
        {
            throw new IOException("exists: " + path);
        }

        string parent = UnixPath.GetDirectoryName(path)!;
        if (!_nodes.TryGetValue(parent, out var p) || p.Kind != Kind.Directory)
        {
            throw new DirectoryNotFoundException(parent);
        }

        _nodes[path] = new Node(Kind.File, [], Mode: UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Log.Add("create:" + path);
        return new Commit(this, path);
    }

    public void SetUnixMode(string path, UnixFileMode mode) => _nodes[path] = _nodes[path] with { Mode = mode };

    public void DeleteFile(string path)
    {
        Log.Add("deletefile:" + path);
        _nodes.Remove(path);
    }

    public void DeleteDirectory(string path)
    {
        Log.Add("deletedir:" + path);
        foreach (string key in _nodes.Keys.Where(k => k == path || k.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
        {
            _nodes.Remove(key);
        }
    }

    public string? ReadLink(string path) => _nodes.TryGetValue(path, out var n) && n.Kind == Kind.Link ? n.Target : null;

    public void CreateSymbolicLink(string path, string target)
    {
        if (_nodes.ContainsKey(path))
        {
            throw new IOException("exists: " + path);
        }

        _nodes[path] = new Node(Kind.Link, Target: target);
        Log.Add("link:" + path + "->" + target);
    }

    public void RenameOver(string source, string destination)
    {
        if (!_nodes.TryGetValue(source, out var node))
        {
            throw new FileNotFoundException(source);
        }

        if (_nodes.TryGetValue(destination, out var existing) && existing.Kind == Kind.Directory)
        {
            throw new IOException("destination is a directory");
        }

        _nodes.Remove(source);
        _nodes[destination] = node;
        Log.Add("rename:" + source + "->" + destination);
    }

    public void MoveDirectory(string source, string destination)
    {
        if (_nodes.ContainsKey(destination))
        {
            throw new IOException("exists: " + destination);
        }

        foreach (string key in _nodes.Keys.Where(k => k == source || k.StartsWith(source + "/", StringComparison.Ordinal)).ToList())
        {
            _nodes[destination + key[source.Length..]] = _nodes[key];
            _nodes.Remove(key);
        }

        Log.Add("movedir:" + source + "->" + destination);
    }

    public IReadOnlyList<string> EnumerateDirectories(string path) =>
        _nodes.Where(p => p.Value.Kind == Kind.Directory && UnixPath.GetDirectoryName(p.Key) == path).Select(p => p.Key).ToList();

    public IEnumerable<string> Paths => _nodes.Keys;

    /// <summary>Stores the written bytes when the stream is disposed.</summary>
    private sealed class Commit : MemoryStream
    {
        private readonly InMemoryFs _fs;
        private readonly string _path;

        public Commit(InMemoryFs fs, string path)
        {
            _fs = fs;
            _path = path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _fs._nodes[_path] = _fs._nodes[_path] with { Data = ToArray() };
            }

            base.Dispose(disposing);
        }
    }
}
