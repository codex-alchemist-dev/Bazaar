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

    // Delete runs on the thread that killed the browser (the exit continuation is synchronous), while a test reads the lists.
    private readonly Lock _gate = new();
    private readonly List<string> _created = [];
    private readonly List<string> _deleted = [];

    public IReadOnlyList<string> Created
    {
        get
        {
            lock (_gate)
            {
                return [.. _created];
            }
        }
    }

    public IReadOnlyList<string> Deleted
    {
        get
        {
            lock (_gate)
            {
                return [.. _deleted];
            }
        }
    }

    public string? Directory { get; private set; }

    public void Create(string directory, string commandsFifo, string repliesFifo)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(_root);
        foreach (var path in new[] { commandsFifo, repliesFifo })
        {
            lock (_gate)
            {
                _created.Add(path);
            }

            File.WriteAllBytes(Real(path), []);
        }
    }

    public SafeFileHandle OpenReadWrite(string path) => File.OpenHandle(Real(path), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

    public SafeFileHandle OpenRead(string path) => File.OpenHandle(Real(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    public void Delete(string path)
    {
        lock (_gate)
        {
            _deleted.Add(path);
        }

        try
        {
            File.Delete(Real(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The temp file is removed with the directory in Dispose. On Windows a file that is still open elsewhere, or already
            // marked for deletion, refuses the delete with "access denied" rather than an IOException.
        }
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private string Real(string virtualPath) => Path.Combine(_root, virtualPath.Replace('/', '_'));
}
