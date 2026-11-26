using System.Security.Cryptography;

namespace Bazaar.Client.OfflineSync;

/// <summary>A key provider that holds the key in memory. For tests and development only: the key is lost on restart.</summary>
public sealed class InMemoryKeyProvider : IOfflineQueueKeyProvider
{
    /// <summary>The key length the queue requires (AES-256).</summary>
    public const int KeyBytes = 32;

    private readonly byte[] _key;

    /// <summary>Creates a provider with a random key.</summary>
    public InMemoryKeyProvider()
        : this(RandomNumberGenerator.GetBytes(KeyBytes))
    {
    }

    /// <summary>Creates a provider with a fixed key.</summary>
    /// <param name="key">Exactly 32 bytes. The provider copies it.</param>
    public InMemoryKeyProvider(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeyBytes)
        {
            throw new ArgumentException($"The key must be {KeyBytes} bytes.", nameof(key));
        }

        _key = key.ToArray();
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> GetKey() => _key;
}
