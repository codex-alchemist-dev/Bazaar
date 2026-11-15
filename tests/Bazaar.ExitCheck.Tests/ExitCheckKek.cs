using System.Security.Cryptography;
using Bazaar.Server.Identity.Keys;

namespace Bazaar.ExitCheck.Tests;

/// <summary>
/// Test-only KEK for the exit-check host: AES-256-GCM under a random in-memory key. The GC body codec fails closed
/// without a KEK provider, so this harness supplies one. Production hosts register their own provider (doc 14).
/// </summary>
internal sealed class ExitCheckKek : IKekProvider, IDisposable
{
    private const int _nonceSize = 12;
    private const int _tagSize = 16;

    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public string KekId => "exitcheck-kek-1";

    public ValueTask<byte[]> WrapAsync(ReadOnlyMemory<byte> dek, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var nonce = RandomNumberGenerator.GetBytes(_nonceSize);
        var cipher = new byte[dek.Length];
        var tag = new byte[_tagSize];
        using var aes = new AesGcm(_key, _tagSize);
        aes.Encrypt(nonce, dek.Span, cipher, tag, associatedData.Span);

        // Layout: nonce | ciphertext | tag.
        var wrapped = new byte[nonce.Length + cipher.Length + tag.Length];
        nonce.CopyTo(wrapped, 0);
        cipher.CopyTo(wrapped, nonce.Length);
        tag.CopyTo(wrapped, nonce.Length + cipher.Length);
        return ValueTask.FromResult(wrapped);
    }

    public ValueTask<byte[]> UnwrapAsync(ReadOnlyMemory<byte> wrappedDek, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var wrapped = wrappedDek.Span;
        if (wrapped.Length < _nonceSize + _tagSize)
            throw new KekUnwrapException("wrapped key is too short");

        var nonce = wrapped[.._nonceSize];
        var cipher = wrapped[_nonceSize..^_tagSize];
        var tag = wrapped[^_tagSize..];
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(_key, _tagSize);
            aes.Decrypt(nonce, cipher, tag, plain, associatedData.Span);
        }
        catch (CryptographicException)
        {
            throw new KekUnwrapException("wrapped key does not authenticate");
        }

        return ValueTask.FromResult(plain);
    }

    public void Dispose() => Array.Clear(_key);
}
