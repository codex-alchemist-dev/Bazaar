using System.Security.Cryptography;
using Bazaar.Server.Identity.Keys;

namespace Bazaar.Server.Tests.Identity;

/// <summary>Test-only KEK: AES-256-GCM under a random in-memory key. Never used outside tests.</summary>
public sealed class FakeKekProvider : IKekProvider, IDisposable
{
    private const int _nonceSize = 12;
    private const int _tagSize = 16;

    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public FakeKekProvider(string kekId = "fake-kek-1") => KekId = kekId;

    public string KekId { get; }

    public ValueTask<byte[]> WrapAsync(ReadOnlyMemory<byte> dek, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var nonce = RandomNumberGenerator.GetBytes(_nonceSize);
        var cipher = new byte[dek.Length];
        var tag = new byte[_tagSize];
        using var aes = new AesGcm(_key, _tagSize);
        aes.Encrypt(nonce, dek.Span, cipher, tag, associatedData.Span);

        // Layout: nonce | ciphertext | tag.
        return ValueTask.FromResult(Concat(nonce, cipher, tag));
    }

    public ValueTask<byte[]> UnwrapAsync(ReadOnlyMemory<byte> wrappedDek, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var wrapped = wrappedDek.Span;
        if (wrapped.Length < _nonceSize + _tagSize)
        {
            throw new KekUnwrapException("wrapped key is too short");
        }

        var nonce = wrapped[.._nonceSize];
        var cipher = wrapped[_nonceSize..^_tagSize];
        var tag = wrapped[^_tagSize..];
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(_key, _tagSize);
            aes.Decrypt(nonce, cipher, tag, plain, associatedData.Span);
        }
        catch (CryptographicException exception)
        {
            throw new KekUnwrapException("wrapped key failed authentication", exception);
        }

        return ValueTask.FromResult(plain);
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_key);

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}
