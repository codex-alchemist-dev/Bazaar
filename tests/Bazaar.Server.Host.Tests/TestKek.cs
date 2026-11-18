using System.Linq;
using System.Security.Cryptography;
using Bazaar.Server.Identity.Keys;

namespace Bazaar.Server.Host.Tests;

/// <summary>An AES-GCM <see cref="IKekProvider"/> with a random key, for tests. Wrap is nonce + ciphertext + tag, bound to the associated data.</summary>
internal sealed class TestKek : IKekProvider
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public string KekId => "test-kek";

    public ValueTask<byte[]> WrapAsync(ReadOnlyMemory<byte> dek, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken = default)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var cipher = new byte[dek.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(_key, AesGcm.TagByteSizes.MaxSize))
        {
            aes.Encrypt(nonce, dek.Span, cipher, tag, associatedData.Span);
        }

        return ValueTask.FromResult(nonce.Concat(cipher).Concat(tag).ToArray());
    }

    public ValueTask<byte[]> UnwrapAsync(ReadOnlyMemory<byte> wrappedDek, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken = default)
    {
        var wrapped = wrappedDek.ToArray();
        var plain = new byte[wrapped.Length - NonceBytes - TagBytes];
        using var aes = new AesGcm(_key, AesGcm.TagByteSizes.MaxSize);
        aes.Decrypt(wrapped.AsSpan(0, NonceBytes), wrapped.AsSpan(NonceBytes, plain.Length), wrapped.AsSpan(^TagBytes), plain, associatedData.Span);
        return ValueTask.FromResult(plain);
    }
}
