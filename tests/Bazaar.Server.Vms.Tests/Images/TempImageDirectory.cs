using Bazaar.Server.Vms.Images;

namespace Bazaar.Server.Vms.Tests.Images;

/// <summary>
/// A throwaway directory under the system temp path for one test, removed on <see cref="Dispose"/>. Read-only files are made
/// writable again first, so the cleanup works on every OS.
/// </summary>
internal sealed class TempImageDirectory : IDisposable
{
    private readonly PhysicalImageFileSystem _files = new();

    public TempImageDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "bazaar-vms-img-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>Full path of a file under the directory.</summary>
    public string FilePath(string name) => Path.Combine(Root, name);

    /// <summary>Writes content to a file, replacing any earlier content and clearing read-only protection first.</summary>
    public string WriteFile(string name, string content)
    {
        var path = FilePath(name);
        if (File.Exists(path))
            _files.SetReadOnly(path, isReadOnly: false);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (!Directory.Exists(Root))
            return;

        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            _files.SetReadOnly(file, isReadOnly: false);

        Directory.Delete(Root, recursive: true);
    }
}
