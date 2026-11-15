using System.Security.Cryptography;

namespace Bazaar.Secrets;

/// <summary>
/// A KEK held in process memory, supplied by host configuration (a file or environment reference resolved at
/// bootstrap). It is the reference provider behind <see cref="IKekProvider"/>. DPAPI and OpenBao providers
/// plug into the same interface when their custody questions are settled (docs 14 and 33).
/// </summary>
public sealed class SoftwareKekProvider : IKekProvider, IDisposable
{
    private const int KeyBytes = 32;
    private readonly byte[] _key;

    public SoftwareKekProvider(string keyId, ReadOnlySpan<byte> key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        if (key.Length != KeyBytes)
        {
            throw new ArgumentException("A KEK must be exactly 32 bytes.", nameof(key));
        }

        CurrentKeyId = keyId;
        _key = key.ToArray();
    }

    public string CurrentKeyId { get; }

    public Task<ReadOnlyMemory<byte>> WrapAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> associatedData, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var blob = SecretCrypto.Seal(_key, key.Span, associatedData.Span);
        return Task.FromResult<ReadOnlyMemory<byte>>(SecretCrypto.Pack(blob));
    }

    public Task<ReadOnlyMemory<byte>> UnwrapAsync(string keyId, ReadOnlyMemory<byte> wrapped, ReadOnlyMemory<byte> associatedData, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!string.Equals(keyId, CurrentKeyId, StringComparison.Ordinal))
        {
            throw new CryptographicException("The wrapped key was sealed under a KEK this provider does not hold.");
        }

        var span = wrapped.Span;
        if (span.Length < SecretCrypto.NonceBytes + SecretCrypto.TagBytes)
        {
            throw new CryptographicException("The wrapped key is too short to be valid.");
        }

        var nonce = span[..SecretCrypto.NonceBytes];
        var body = span[SecretCrypto.NonceBytes..^SecretCrypto.TagBytes];
        var tag = span[^SecretCrypto.TagBytes..];
        var key = SecretCrypto.Open(_key, nonce, body, tag, associatedData.Span);
        return Task.FromResult<ReadOnlyMemory<byte>>(key);
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_key);
}
