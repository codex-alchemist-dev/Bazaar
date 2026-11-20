using System.Security.Cryptography;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.MailInbound;
using Bazaar.Server.MailOutbound;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// The per-person key lookup the mail body adapters need (docs/design/22, "Mail runtime"; docs/design/04, <c>person_key</c>). A seam over
/// <see cref="PersonKeyStore"/> so the adapters can be tested without a database.
/// </summary>
public interface IMailPersonKeys
{
    /// <summary>The person's live key id and data key, created on first use.</summary>
    ValueTask<PersonKeyStore.PersonKey> GetOrCreateActiveAsync(Guid personId, CancellationToken ct);

    /// <summary>The data key for a stored key id, or <c>null</c> when erasure destroyed it. Throws for an unknown key id.</summary>
    ValueTask<byte[]?> UnwrapAsync(Guid keyId, CancellationToken ct);
}

/// <summary>Production lookup: delegates to the shared <see cref="PersonKeyStore"/>.</summary>
public sealed class PersonKeyStoreMailKeys(PersonKeyStore store) : IMailPersonKeys
{
    public ValueTask<PersonKeyStore.PersonKey> GetOrCreateActiveAsync(Guid personId, CancellationToken ct) => store.GetOrCreateActiveAsync(personId, ct);

    public ValueTask<byte[]?> UnwrapAsync(Guid keyId, CancellationToken ct) => store.UnwrapAsync(keyId, ct);
}

/// <summary>
/// A stored mail body cannot be opened because the person's key was destroyed (erasure, docs/design/04 and 14). This is the expected end state of
/// erasure, so callers can tell it from a tampered or corrupt body, which throws a <see cref="CryptographicException"/>.
/// </summary>
public sealed class MailBodyErasedException(Guid keyId)
    : InvalidOperationException($"The key {keyId} for this mail body was destroyed by erasure; the body is unrecoverable.")
{
    public Guid KeyId { get; } = keyId;
}

/// <summary>
/// AES-256-GCM under the person's key, laid out as the GC body codec does (<see cref="AesGcmGcBodyCodec"/>): ciphertext then the 16-byte tag,
/// a fresh 12-byte nonce per seal, the key id as associated data. The inbound side also binds the person id.
/// </summary>
internal static class MailBodyCrypto
{
    private const int _nonceSize = 12;
    private const int _tagSize = 16;

    public static async ValueTask<(Guid KeyId, byte[] Ciphertext, byte[] Nonce)> SealAsync(
        IMailPersonKeys keys, Guid personId, ReadOnlyMemory<byte> plaintext, Func<Guid, byte[]> aad, CancellationToken ct)
    {
        if (personId == Guid.Empty)
            throw new ArgumentException("A mail body needs an owner with a personal key.", nameof(personId));

        var key = await keys.GetOrCreateActiveAsync(personId, ct).ConfigureAwait(false);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(_nonceSize);
            var sealedBytes = new byte[plaintext.Length + _tagSize];
            using var aes = new AesGcm(key.DataKey, _tagSize);
            aes.Encrypt(nonce, plaintext.Span, sealedBytes.AsSpan(0, plaintext.Length), sealedBytes.AsSpan(plaintext.Length), aad(key.KeyId));
            return (key.KeyId, sealedBytes, nonce);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key.DataKey);
        }
    }

    public static async ValueTask<byte[]> OpenAsync(
        IMailPersonKeys keys, Guid keyId, byte[] ciphertext, byte[] nonce, Func<Guid, byte[]> aad, CancellationToken ct)
    {
        // Fail closed: a destroyed key never yields plaintext, and a malformed body is rejected before any key work.
        if (nonce.Length != _nonceSize || ciphertext.Length < _tagSize)
            throw new CryptographicException("The sealed mail body has an invalid nonce or length.");

        var dataKey = await keys.UnwrapAsync(keyId, ct).ConfigureAwait(false) ?? throw new MailBodyErasedException(keyId);
        var length = ciphertext.Length - _tagSize;
        var plaintext = new byte[length];
        try
        {
            using var aes = new AesGcm(dataKey, _tagSize);
            aes.Decrypt(nonce, ciphertext.AsSpan(0, length), ciphertext.AsSpan(length), plaintext, aad(keyId));
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }
}

/// <summary>
/// Outbound body protection over the per-person key store. The agent id is the agent's principal id, which is also its <c>person_key</c> owner
/// for GC bodies; an id that is not a GUID is refused rather than mapped to some other person.
/// </summary>
public sealed class PersonKeyMailBodyProtector(IMailPersonKeys keys) : IMailBodyProtector
{
    private static byte[] Aad(Guid keyId) => keyId.ToByteArray();

    public async ValueTask<ProtectedBody> ProtectAsync(string agentId, ReadOnlyMemory<byte> plaintext, CancellationToken ct)
    {
        if (!Guid.TryParse(agentId, out var personId))
            throw new ArgumentException("The agent id is not a principal id, so it has no personal key.", nameof(agentId));

        var (keyId, ciphertext, nonce) = await MailBodyCrypto.SealAsync(keys, personId, plaintext, Aad, ct).ConfigureAwait(false);
        return new ProtectedBody(keyId, ciphertext, nonce);
    }

    public ValueTask<byte[]> UnprotectAsync(ProtectedBody body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        return MailBodyCrypto.OpenAsync(keys, body.KeyId, body.Ciphertext, body.Nonce, Aad, ct);
    }
}

/// <summary>Inbound body sealing over the per-person key store. The associated data is the key id and the person id, so a body cannot be opened as another person's.</summary>
public sealed class PersonKeyMailBodySealer(IMailPersonKeys keys) : IMailBodySealer
{
    private static byte[] Aad(Guid keyId, Guid personId)
    {
        var aad = new byte[32];
        keyId.TryWriteBytes(aad.AsSpan(0, 16));
        personId.TryWriteBytes(aad.AsSpan(16, 16));
        return aad;
    }

    public async ValueTask<SealedBody> SealAsync(Guid personId, ReadOnlyMemory<byte> raw, CancellationToken ct)
    {
        var (keyId, ciphertext, nonce) = await MailBodyCrypto.SealAsync(keys, personId, raw, id => Aad(id, personId), ct).ConfigureAwait(false);
        return new SealedBody(keyId, ciphertext, nonce);
    }

    public ValueTask<byte[]> OpenAsync(Guid personId, SealedBody sealedBody, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sealedBody);
        return MailBodyCrypto.OpenAsync(keys, sealedBody.KeyId, sealedBody.Ciphertext, sealedBody.Nonce, id => Aad(id, personId), ct);
    }
}
