using System.Security.Cryptography;
using Bazaar.Client.OfflineSync;

namespace Bazaar.Client.OfflineSync.PlatformKeys;

/// <summary>
/// A persistent <see cref="IOfflineQueueKeyProvider"/>: the 32-byte queue key lives in one file, wrapped by an <see cref="IOfflineKeyProtector"/>.
/// The first call creates the key; later calls, and later runs, read it back. A damaged or foreign file fails closed
/// (<see cref="OfflineQueueCorruptedException"/>) and is never replaced, because a new key would make the queue unreadable and hide the loss.
/// </summary>
public sealed class ProtectedFileKeyProvider : IOfflineQueueKeyProvider
{
    private const string _magic = "BQK1";

    private readonly string _path;
    private readonly IOfflineKeyProtector _protector;
    private readonly Lock _gate = new();
    private byte[]? _key;

    /// <summary>Creates the provider.</summary>
    /// <param name="path">The key file. Its directory is created when missing.</param>
    /// <param name="protector">The platform wrapper.</param>
    public ProtectedFileKeyProvider(string path, IOfflineKeyProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protector);
        _path = path;
        _protector = protector;
    }

    /// <summary>Picks the platform wrapper: DPAPI on Windows, the key file alone elsewhere.</summary>
    public static ProtectedFileKeyProvider ForPlatform(string path) =>
        new(path, OperatingSystem.IsWindows() ? new DpapiOfflineKeyProtector() : new KeyFileOfflineKeyProtector());

    /// <inheritdoc />
    public ReadOnlyMemory<byte> GetKey()
    {
        lock (_gate)
        {
            return _key ??= File.Exists(_path) ? Read() : Create();
        }
    }

    private byte[] Read()
    {
        byte[] blob = File.ReadAllBytes(_path);
        if (blob.Length < _magic.Length || !blob.AsSpan(0, _magic.Length).SequenceEqual("BQK1"u8))
        {
            throw new OfflineQueueCorruptedException("The queue key file is damaged.");
        }

        byte[] key;
        try
        {
            key = _protector.Unprotect(blob.AsSpan(_magic.Length));
        }
        catch (CryptographicException ex)
        {
            throw new OfflineQueueCorruptedException("The queue key file cannot be opened by this user or machine.", ex);
        }

        if (key.Length != InMemoryKeyProvider.KeyBytes)
        {
            throw new OfflineQueueCorruptedException("The queue key file holds a key of the wrong length.");
        }

        return key;
    }

    private byte[] Create()
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        byte[] key = RandomNumberGenerator.GetBytes(InMemoryKeyProvider.KeyBytes);
        byte[] wrapped = _protector.Protect(key);
        byte[] blob = [.. "BQK1"u8, .. wrapped];

        string temp = _path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temp, options))
        {
            stream.Write(blob);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, _path, overwrite: false);
        return key;
    }
}
