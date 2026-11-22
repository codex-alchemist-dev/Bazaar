using Bazaar.Server.BrowserPool.LinuxHost;
using Microsoft.Win32.SafeHandles;

namespace Bazaar.Server.BrowserPool.LinuxHost.Tests;

/// <summary>
/// Stands in for the FIFO factory off Linux: each virtual FIFO path maps to a plain file in a temp directory, so no
/// Linux-only path is ever touched. It records what was created and removed.
/// </summary>
internal sealed class FakeFifoFactory : IFifoFactory, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-fifo-fake-" + Guid.NewGuid().ToString("N")[..12]);

    public List<string> Created { get; } = [];

    public List<string> Deleted { get; } = [];

    public string? Directory { get; private set; }

    public void Create(string directory, string commandsFifo, string repliesFifo)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(_root);
        foreach (var path in new[] { commandsFifo, repliesFifo })
        {
            Created.Add(path);
            File.WriteAllBytes(Real(path), []);
        }
    }

    public SafeFileHandle OpenReadWrite(string path) => File.OpenHandle(Real(path), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

    public SafeFileHandle OpenRead(string path) => File.OpenHandle(Real(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    public void Delete(string path)
    {
        Deleted.Add(path);
        try
        {
            File.Delete(Real(path));
        }
        catch (IOException)
        {
            // The temp file is removed with the directory in Dispose.
        }
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private string Real(string virtualPath) => Path.Combine(_root, virtualPath.Replace('/', '_'));
}
