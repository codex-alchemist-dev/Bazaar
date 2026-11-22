using Bazaar.Server.CodingEdit;

namespace Bazaar.Server.CodingEdit.Tests;

/// <summary>A guest file store for adapter tests. Paths are absolute guest paths.</summary>
internal sealed class InMemoryGuestFileChannel : IGuestFileChannel
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    /// <summary>Resolves a guest path. The default returns the path unchanged; a test may replace it to simulate a link out of the worktree.</summary>
    public Func<string, string?> RealPath { get; set; } = path => path;

    /// <summary>The number of writes the guest accepted.</summary>
    public int PutCount { get; private set; }

    /// <summary>Stores bytes at an absolute guest path.</summary>
    public void Set(string guestPath, byte[] bytes) => _files[guestPath] = bytes;

    /// <summary>Returns the bytes at an absolute guest path, or null.</summary>
    public byte[]? Get(string guestPath) => _files.TryGetValue(guestPath, out var bytes) ? bytes : null;

    /// <inheritdoc />
    public Task<byte[]?> GetFileAsync(string guestPath, CancellationToken ct) => Task.FromResult(Get(guestPath));

    /// <inheritdoc />
    public Task PutFileAsync(string guestPath, byte[] content, string sha256, CancellationToken ct)
    {
        // The guest rejects content that does not match its hash, as the port requires.
        if (!string.Equals(ContentHash.Sha256Hex(content), sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The guest rejected content whose hash does not match.");
        }

        _files[guestPath] = content.ToArray();
        PutCount++;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteFileAsync(string guestPath, CancellationToken ct)
    {
        _files.Remove(guestPath);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListFilesAsync(string guestDirectory, CancellationToken ct)
    {
        var prefix = guestDirectory.TrimEnd('/') + "/";
        IReadOnlyList<string> listed = _files.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        return Task.FromResult(listed);
    }

    /// <inheritdoc />
    public Task<string?> ResolveRealPathAsync(string guestPath, CancellationToken ct) => Task.FromResult(RealPath(guestPath));
}
