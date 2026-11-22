namespace Bazaar.Archive;

/// <summary>A bundle store on a directory tree: <c>{root}/{kind}/{unit}/{name}</c>. Names are checked so nothing escapes the root.</summary>
public sealed class DirectoryArchiveStore(string root) : IArchiveStore
{
    private string UnitDir(ArchiveUnit unit)
    {
        if (!ArchiveHashing.IsSafeId(unit.Id))
        {
            throw new ArgumentException("Unsafe unit id.", nameof(unit));
        }

        return Path.Combine(root, unit.Kind.ToString().ToLowerInvariant(), unit.Id);
    }

    private string FilePath(ArchiveUnit unit, string name)
    {
        var dir = UnitDir(unit);
        var full = Path.GetFullPath(Path.Combine(dir, name));
        if (!full.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("Name escapes the unit directory.", nameof(name));
        }

        return full;
    }

    public async Task PutAsync(ArchiveUnit unit, string name, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        var path = FilePath(unit, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content.ToArray(), ct);
    }

    public async Task<byte[]?> GetAsync(ArchiveUnit unit, string name, CancellationToken ct)
    {
        var path = FilePath(unit, name);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
    }

    public Task<IReadOnlyList<string>> ListAsync(ArchiveUnit unit, CancellationToken ct)
    {
        var dir = UnitDir(unit);
        IReadOnlyList<string> names = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList()
            : [];
        return Task.FromResult(names);
    }

    public Task<IReadOnlyList<ArchiveUnit>> ListUnitsAsync(CancellationToken ct)
    {
        var units = new List<ArchiveUnit>();
        foreach (var kind in Enum.GetValues<ArchiveUnitKind>())
        {
            var dir = Path.Combine(root, kind.ToString().ToLowerInvariant());
            if (Directory.Exists(dir))
            {
                units.AddRange(Directory.EnumerateDirectories(dir).Select(d => new ArchiveUnit(kind, Path.GetFileName(d))));
            }
        }

        return Task.FromResult<IReadOnlyList<ArchiveUnit>>(units);
    }
}
