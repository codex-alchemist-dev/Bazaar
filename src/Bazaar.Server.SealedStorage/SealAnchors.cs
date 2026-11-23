using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.SealedStorage;

/// <summary>In-process anchor. Detects rollback only within one process lifetime; for tests and for hosts that publish the point elsewhere.</summary>
public sealed class InMemorySealAnchor : ISealAnchor
{
    private readonly ConcurrentDictionary<string, SealAnchorPoint> _points = new(StringComparer.Ordinal);

    public ValueTask<SealAnchorPoint?> GetAsync(string scope, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_points.TryGetValue(scope, out var point) ? point : null);

    public ValueTask PublishAsync(string scope, SealAnchorPoint point, CancellationToken cancellationToken)
    {
        _points.AddOrUpdate(scope, point, (_, old) => point.Generation > old.Generation ? point : old);
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearAsync(string scope, CancellationToken cancellationToken)
    {
        _points.TryRemove(scope, out _);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Local counter file anchor (doc 61 fallback): one small file per scope in a folder that should live apart from the sealed root.
/// It detects in-place edits and rollback on the same host, not a restore of both folders together.
/// </summary>
public sealed class FileSealAnchor : ISealAnchor
{
    private readonly string _directory;
    private readonly object _gate = new();

    public FileSealAnchor(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public ValueTask<SealAnchorPoint?> GetAsync(string scope, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(Read(PathFor(scope)));
        }
    }

    public ValueTask PublishAsync(string scope, SealAnchorPoint point, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var path = PathFor(scope);
            var current = Read(path);
            if (current is not null && current.Generation >= point.Generation)
            {
                return ValueTask.CompletedTask;
            }

            var bytes = new byte[8 + point.ManifestHash.Length];
            BinaryPrimitives.WriteInt64BigEndian(bytes, point.Generation);
            point.ManifestHash.CopyTo(bytes, 8);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ClearAsync(string scope, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            File.Delete(PathFor(scope));
        }

        return ValueTask.CompletedTask;
    }

    private string PathFor(string scope) =>
        Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(SealFormat.StrictUtf8.GetBytes(scope))) + ".anchor");

    private static SealAnchorPoint? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = File.ReadAllBytes(path);
        return bytes.Length != 8 + SHA256.HashSizeInBytes
            ? throw new InvalidDataException("Seal anchor file is malformed.")
            : new SealAnchorPoint(BinaryPrimitives.ReadInt64BigEndian(bytes), bytes[8..]);
    }
}
