using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Council.Data;
using Bazaar.Server.Gc.Body;

namespace Bazaar.Server.Host.CouncilAdapters;

/// <summary>
/// Production <see cref="ICouncilReasoningCipher"/> (doc 04, per-person encrypted fields): AES-256-GCM under the voter's per-person
/// key from <see cref="PersonKeyStore"/>, with a fresh 12-byte nonce per seal and the key id as associated data, as
/// <c>AesGcmGcBodyCodec</c> does. The stored ciphertext is the encrypted text followed by the 16-byte tag.
/// </summary>
/// <remarks>
/// The voter id is a person id in its <c>D</c> text form (the agent or owner id the membership adapter returns). An id that is
/// not a GUID has no per-person key, and the code has no mapping from other text to a person id, so it is refused with an
/// <see cref="ArgumentException"/> rather than sealed under an invented key.
/// </remarks>
public sealed class PersonKeyCouncilReasoningCipher(PersonKeyStore keys) : ICouncilReasoningCipher
{
    private const int _nonceSize = 12;
    private const int _tagSize = 16;

    /// <inheritdoc />
    public async ValueTask<ReasoningCiphertext> SealAsync(string voterId, string plaintext, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var personId = ParsePersonId(voterId);

        var key = await keys.GetOrCreateActiveAsync(personId, ct).ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(_nonceSize);
            var sealedBytes = new byte[bytes.Length + _tagSize];
            using (var aes = new AesGcm(key.DataKey, _tagSize))
            {
                aes.Encrypt(nonce, bytes, sealedBytes.AsSpan(0, bytes.Length), sealedBytes.AsSpan(bytes.Length), key.KeyId.ToByteArray());
            }

            return new ReasoningCiphertext(key.KeyId, sealedBytes, nonce);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key.DataKey);
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <inheritdoc />
    public async ValueTask<string> OpenAsync(string voterId, ReasoningCiphertext sealedText, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sealedText);
        _ = ParsePersonId(voterId);

        if (sealedText.Nonce.Length != _nonceSize || sealedText.Ciphertext.Length < _tagSize)
        {
            throw new CryptographicException("The sealed ballot reasoning has an invalid nonce or length.");
        }

        var dataKey = await keys.UnwrapAsync(sealedText.KeyId, ct).ConfigureAwait(false)
            ?? throw new CryptographicException($"The key {sealedText.KeyId:D} that sealed this ballot reasoning was destroyed by erasure.");

        var length = sealedText.Ciphertext.Length - _tagSize;
        var plaintext = new byte[length];
        try
        {
            using var aes = new AesGcm(dataKey, _tagSize);
            aes.Decrypt(sealedText.Nonce, sealedText.Ciphertext.AsSpan(0, length), sealedText.Ciphertext.AsSpan(length), plaintext, sealedText.KeyId.ToByteArray());
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static Guid ParsePersonId(string voterId)
    {
        if (!Guid.TryParse(voterId, CultureInfo.InvariantCulture, out var personId) || personId == Guid.Empty)
        {
            throw new ArgumentException(
                $"Council voter id '{voterId}' is not a person id (a GUID), so there is no per-person key to seal its reasoning under.",
                nameof(voterId));
        }

        return personId;
    }
}
