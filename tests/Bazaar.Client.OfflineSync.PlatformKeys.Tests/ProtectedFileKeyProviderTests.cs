using Bazaar.Client.OfflineSync;
using Bazaar.Client.OfflineSync.PlatformKeys;

namespace Bazaar.Client.OfflineSync.PlatformKeys.Tests;

public sealed class ProtectedFileKeyProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bqk-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string KeyPath => Path.Combine(_dir, "sub", "queue.key");

    [Fact]
    public void FirstCallCreatesA32ByteKeyAndLaterRunsReadItBack()
    {
        var first = new ProtectedFileKeyProvider(KeyPath, new KeyFileOfflineKeyProtector()).GetKey().ToArray();
        var second = new ProtectedFileKeyProvider(KeyPath, new KeyFileOfflineKeyProtector()).GetKey().ToArray();

        Assert.Equal(32, first.Length);
        Assert.Equal(first, second);
    }

    [Fact]
    public void RepeatedCallsOnOneInstanceReturnTheSameKey()
    {
        var provider = new ProtectedFileKeyProvider(KeyPath, new KeyFileOfflineKeyProtector());
        Assert.Equal(provider.GetKey().ToArray(), provider.GetKey().ToArray());
    }

    [Fact]
    public void TheKeyIsWrappedByTheProtector()
    {
        var provider = new ProtectedFileKeyProvider(KeyPath, new XorProtector());
        var key = provider.GetKey().ToArray();

        var onDisk = File.ReadAllBytes(KeyPath);
        Assert.NotEqual(key, onDisk[4..]);
        Assert.Equal(key, new ProtectedFileKeyProvider(KeyPath, new XorProtector()).GetKey().ToArray());
    }

    [Fact]
    public void ADamagedFileFailsClosedAndIsNotReplaced()
    {
        _ = new ProtectedFileKeyProvider(KeyPath, new KeyFileOfflineKeyProtector()).GetKey();
        File.WriteAllBytes(KeyPath, [1, 2, 3]);

        Assert.Throws<OfflineQueueCorruptedException>(() => new ProtectedFileKeyProvider(KeyPath, new KeyFileOfflineKeyProtector()).GetKey());
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(KeyPath));
    }

    [Fact]
    public void AKeyOfTheWrongLengthFailsClosed()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        File.WriteAllBytes(KeyPath, [.. "BQK1"u8, 1, 2, 3]);

        Assert.Throws<OfflineQueueCorruptedException>(() => new ProtectedFileKeyProvider(KeyPath, new KeyFileOfflineKeyProtector()).GetKey());
    }

    [Fact]
    public void AProtectorThatCannotOpenTheBlobFailsClosed()
    {
        _ = new ProtectedFileKeyProvider(KeyPath, new XorProtector()).GetKey();

        Assert.Throws<OfflineQueueCorruptedException>(() => new ProtectedFileKeyProvider(KeyPath, new ThrowingProtector()).GetKey());
    }

    [Fact]
    public void TheKeyFileIsOwnerOnlyOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        _ = new ProtectedFileKeyProvider(KeyPath, new KeyFileOfflineKeyProtector()).GetKey();

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyPath));
        Assert.False(File.Exists(KeyPath + ".tmp"));
    }

    [Fact]
    public void DpapiRoundTripsOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var protector = new DpapiOfflineKeyProtector();
        byte[] secret = [9, 8, 7, 6];
        var blob = protector.Protect(secret);

        Assert.NotEqual(secret, blob);
        Assert.Equal(secret, protector.Unprotect(blob));
    }

    [Fact]
    public void TheKeyWorksWithTheEncryptedQueue()
    {
        var provider = ProtectedFileKeyProvider.ForPlatform(KeyPath);
        var deviceId = Guid.NewGuid();
        string queueDir = Path.Combine(_dir, "queue");

        using (var queue = new EncryptedOfflineQueue(queueDir, deviceId, provider))
        {
            Assert.NotNull(queue);
        }

        using var reopened = new EncryptedOfflineQueue(queueDir, deviceId, ProtectedFileKeyProvider.ForPlatform(KeyPath));
        Assert.NotNull(reopened);
    }

    private sealed class XorProtector : IOfflineKeyProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => Xor(plaintext);

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes) => Xor(protectedBytes);

        private static byte[] Xor(ReadOnlySpan<byte> data)
        {
            var result = data.ToArray();
            for (int i = 0; i < result.Length; i++)
            {
                result[i] ^= 0x5A;
            }

            return result;
        }
    }

    private sealed class ThrowingProtector : IOfflineKeyProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => throw new System.Security.Cryptography.CryptographicException();

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes) => throw new System.Security.Cryptography.CryptographicException();
    }
}
