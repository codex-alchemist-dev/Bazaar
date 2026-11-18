using Bazaar.Server.Vms.Images;

namespace Bazaar.Server.Vms.Tests.Images;

/// <summary>
/// An in-memory <see cref="IImageFileSystem"/> for tests that must observe writes. It counts every mutating call, so a test
/// can prove the verifier never changes the image.
/// </summary>
internal sealed class FakeImageFileSystem : IImageFileSystem
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _readOnly = new(StringComparer.Ordinal);

    public int MutationCount { get; private set; }

    public void Add(string path, byte[] content, bool isReadOnly = false)
    {
        _files[path] = content;
        if (isReadOnly)
            _readOnly.Add(path);
    }

    public bool FileExists(string path) => _files.ContainsKey(path);

    public Stream OpenRead(string path) => new MemoryStream(_files[path], writable: false);

    public long GetLength(string path) => _files[path].Length;

    public bool IsReadOnly(string path) => _readOnly.Contains(path);

    public void SetReadOnly(string path, bool isReadOnly)
    {
        MutationCount++;
        if (isReadOnly)
            _readOnly.Add(path);
        else
            _readOnly.Remove(path);
    }
}
