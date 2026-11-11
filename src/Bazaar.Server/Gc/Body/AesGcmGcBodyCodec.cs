using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Identity.Keys;
using Npgsql;

namespace Bazaar.Server.Gc.Body;

/// <summary>
/// Production body codec (docs/design/01 WP6; docs/design/04, Question 2): AES-256-GCM under the author's per-person key.
/// The stored value is the ciphertext followed by the 16-byte tag, with a fresh 12-byte nonce per seal. The key id is the
/// associated data, so a body cannot be moved to another key's row and still open.
/// </summary>
/// <remarks>
/// Without a KEK provider the codec fails closed: sealing throws, so no body is ever stored in plaintext. Rows written by the
/// interim codec (key id <see cref="PlaintextGcBodyCodec.PlaintextKeyId"/>) are still read as plaintext until they are migrated.
/// </remarks>
public sealed class AesGcmGcBodyCodec(NpgsqlDataSource dataSource, IKekProvider? kek) : IGcBodyCodec
{
    private const int _nonceSize = 12;
    private const int _tagSize = 16;

    private readonly PersonKeyStore? _keys = kek is null ? null : new PersonKeyStore(dataSource, kek);

    /// <inheritdoc />
    public async ValueTask<MessageBody> SealAsync(Guid authorId, string text, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        if (authorId == Guid.Empty)
            throw new ArgumentException("A body needs an author with a personal key.", nameof(authorId));
        var keys = RequireKeys();

        var key = await keys.GetOrCreateActiveAsync(authorId, ct).ConfigureAwait(false);
        var plaintext = Encoding.UTF8.GetBytes(text);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(_nonceSize);
            var sealed_ = new byte[plaintext.Length + _tagSize];
            using (var aes = new AesGcm(key.DataKey, _tagSize))
            {
                aes.Encrypt(nonce, plaintext, sealed_.AsSpan(0, plaintext.Length), sealed_.AsSpan(plaintext.Length), key.KeyId.ToByteArray());
            }

            return new MessageBody(sealed_, key.KeyId, nonce);
        }
        finally
        {
            // Shorten how long the data key and plaintext sit in process memory.
            CryptographicOperations.ZeroMemory(key.DataKey);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <inheritdoc />
    public async ValueTask<string?> OpenAsync(ReadOnlyMemory<byte> ciphertext, Guid keyId, ReadOnlyMemory<byte> nonce, CancellationToken ct)
    {
        if (keyId == PlaintextGcBodyCodec.PlaintextKeyId)
            return Encoding.UTF8.GetString(ciphertext.Span);

        var keys = RequireKeys();
        var dataKey = await keys.UnwrapAsync(keyId, ct).ConfigureAwait(false);
        if (dataKey is null)
            return null;

        if (nonce.Length != _nonceSize || ciphertext.Length < _tagSize)
            throw new CryptographicException("The sealed body has an invalid nonce or length.");

        var bodyLength = ciphertext.Length - _tagSize;
        var plaintext = new byte[bodyLength];
        try
        {
            using var aes = new AesGcm(dataKey, _tagSize);
            aes.Decrypt(nonce.Span, ciphertext.Span[..bodyLength], ciphertext.Span[bodyLength..], plaintext, keyId.ToByteArray());
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private PersonKeyStore RequireKeys() =>
        _keys ?? throw new InvalidOperationException(
            "No key-encryption key provider is registered, so GC bodies cannot be sealed. Register an IKekProvider (docs/design/14).");
}
