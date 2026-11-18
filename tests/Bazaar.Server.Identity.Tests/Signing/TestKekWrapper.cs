using System.Linq;
using System.Security.Cryptography;
using Bazaar.Server.Identity.Signing;

namespace Bazaar.Server.Identity.Tests.Signing;

/// <summary>
/// An AES-GCM stand-in for the host KEK. Wrap is nonce + ciphertext + tag, bound to the associated data (the dek_id bytes). Set
/// <see cref="FailUnwrap"/> to simulate a KEK that is unavailable.
/// </summary>
internal sealed class TestKekWrapper(byte[] kek, string kekId = "test-kek") : ISigningKeyWrapper
{
    private const int _nonceBytes = 12;
    private const int _tagBytes = 16;

    public bool FailUnwrap { get; set; }

    public string KekId { get; } = kekId;

    public ValueTask<byte[]> WrapAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken = default)
    {
        var nonce = RandomNumberGenerator.GetBytes(_nonceBytes);
        var cipher = new byte[key.Length];
        var tag = new byte[_tagBytes];
        using (var aes = new AesGcm(kek, AesGcm.TagByteSizes.MaxSize))
        {
            aes.Encrypt(nonce, key.Span, cipher, tag, associatedData.Span);
        }

        return ValueTask.FromResult(nonce.Concat(cipher).Concat(tag).ToArray());
    }

    public ValueTask<byte[]> UnwrapAsync(ReadOnlyMemory<byte> wrappedKey, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken = default)
    {
        if (FailUnwrap) return ValueTask.FromException<byte[]>(new CryptographicException("The KEK is unavailable."));

        var wrapped = wrappedKey.ToArray();
        var nonce = wrapped[.._nonceBytes];
        var cipher = wrapped[_nonceBytes..^_tagBytes];
        var tag = wrapped[^_tagBytes..];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(kek, AesGcm.TagByteSizes.MaxSize);
        aes.Decrypt(nonce, cipher, tag, plain, associatedData.Span);
        return ValueTask.FromResult(plain);
    }
}
