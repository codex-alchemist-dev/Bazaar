using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Bazaar.Client.OfflineSync;

/// <summary>
/// An anchor store backed by one small file, kept outside the queue directory (doc 56 open item 7). The file carries the record
/// count and acknowledged sequence plus an HMAC under the queue key, so an edit or a swap with another device's anchor is
/// detected and the queue stays closed. Each save writes a temporary file, flushes it to disk and renames it over the old one,
/// so a crash leaves either the old anchor or the new one, never a half-written file.
/// </summary>
/// <remarks>
/// A file is the cross-platform fallback. A platform store (DPAPI or the OS keychain next to the queue key) implements
/// <see cref="IOfflineQueueAnchorStore"/> the same way; point this one at a directory the queue's own directory does not share
/// with a backup or sync tool, or truncation by restoring an old copy of both files goes unseen.
/// </remarks>
public sealed class FileOfflineQueueAnchorStore : IOfflineQueueAnchorStore
{
    private const int _countBytes = 16;
    private const int _macBytes = 32;
    private const int _fileBytes = 4 + _countBytes + _macBytes;

    private static ReadOnlySpan<byte> Magic => "BQA1"u8;

    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly Guid _deviceId;
    private readonly IOfflineQueueKeyProvider _keyProvider;

    /// <summary>Creates a store at <paramref name="path"/>.</summary>
    /// <param name="path">The anchor file. Its directory is created when absent.</param>
    /// <param name="deviceId">The device the queue belongs to; bound into the MAC so an anchor cannot be moved between devices.</param>
    /// <param name="keyProvider">Supplies the queue key that authenticates the anchor.</param>
    public FileOfflineQueueAnchorStore(string path, Guid deviceId, IOfflineQueueKeyProvider keyProvider)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(keyProvider);
        _path = Path.GetFullPath(path);
        _deviceId = deviceId;
        _keyProvider = keyProvider;
    }

    /// <inheritdoc />
    /// <exception cref="OfflineQueueCorruptedException">The file has the wrong size or fails its MAC.</exception>
    public OfflineQueueAnchor? Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(_path);
            if (bytes.Length != _fileBytes || !bytes.AsSpan(0, 4).SequenceEqual(Magic))
            {
                throw new OfflineQueueCorruptedException("the queue anchor file is damaged");
            }

            var expected = Mac(bytes.AsSpan(0, 4 + _countBytes));
            if (!CryptographicOperations.FixedTimeEquals(expected, bytes.AsSpan(4 + _countBytes)))
            {
                throw new OfflineQueueCorruptedException("the queue anchor file failed authentication");
            }

            return new OfflineQueueAnchor(
                BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(4, 8)),
                BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(12, 8)));
        }
    }

    /// <inheritdoc />
    public void Save(OfflineQueueAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        var bytes = new byte[_fileBytes];
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(4, 8), anchor.RecordCount);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(12, 8), anchor.AckedSequence);
        Mac(bytes.AsSpan(0, 4 + _countBytes)).CopyTo(bytes.AsSpan(4 + _countBytes));

        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temp = _path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, _path, overwrite: true);
        }
    }

    private byte[] Mac(ReadOnlySpan<byte> data)
    {
        Span<byte> message = stackalloc byte[data.Length + 16];
        data.CopyTo(message);
        _deviceId.TryWriteBytes(message[data.Length..]);
        return HMACSHA256.HashData(_keyProvider.GetKey().Span, message);
    }
}
