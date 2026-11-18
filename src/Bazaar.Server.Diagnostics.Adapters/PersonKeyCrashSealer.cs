using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Gc.Body;

namespace Bazaar.Server.Diagnostics.Adapters;

/// <summary>Where the company key lives. Shared devices, which have no person, are sealed under it (doc 43 section 11.3).</summary>
/// <param name="CompanyKeyOwnerId">The reserved person id the company key is stored under in <c>person_key</c>. No real person has it.</param>
public sealed record CrashSealingOptions(Guid CompanyKeyOwnerId)
{
    /// <summary>The shipped reserved id.</summary>
    public static CrashSealingOptions Default { get; } = new(Guid.Parse("00000000-0000-7000-8000-000000000043"));
}

/// <summary>
/// Seals report bodies and screenshots with AES-256-GCM under the owning person's data key, or the company key for a shared
/// device. The associated data binds the payload kind and the record id, so a blob cannot be moved to another record or passed
/// off as another kind. Destroying the person's key makes <see cref="OpenAsync"/> throw.
/// </summary>
public sealed class PersonKeyCrashSealer(PersonKeyStore keys, ICrashInstallDirectory directory, CrashSealingOptions options) : ICrashPayloadSealer
{
    private const int _nonceSize = 12;
    private const int _tagSize = 16;

    /// <inheritdoc />
    public async Task<SealedPayload> SealAsync(Guid installId, SealedPayloadKind kind, Guid recordId, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default)
    {
        var owner = await directory.GetOwnerAsync(installId, ct).ConfigureAwait(false) ?? options.CompanyKeyOwnerId;
        var key = await keys.GetOrCreateActiveAsync(owner, ct).ConfigureAwait(false);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(_nonceSize);
            var cipher = new byte[plaintext.Length];
            var tag = new byte[_tagSize];
            using var aes = new AesGcm(key.DataKey, _tagSize);
            aes.Encrypt(nonce, plaintext.Span, cipher, tag, AssociatedData(kind, recordId));
            return new SealedPayload(key.KeyId.ToString("D"), nonce, cipher, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key.DataKey);
        }
    }

    /// <inheritdoc />
    public async Task<byte[]> OpenAsync(Guid installId, SealedPayloadKind kind, Guid recordId, SealedPayload payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!Guid.TryParseExact(payload.KeyId, "D", out var keyId))
        {
            throw new CryptographicException("The payload names no valid key.");
        }

        var dataKey = await keys.UnwrapAsync(keyId, ct).ConfigureAwait(false)
            ?? throw new CryptographicException("The key that sealed this payload was destroyed.");
        try
        {
            var plain = new byte[payload.Ciphertext.Length];
            using var aes = new AesGcm(dataKey, _tagSize);
            aes.Decrypt(payload.Nonce, payload.Ciphertext, payload.Tag, plain, AssociatedData(kind, recordId));
            return plain;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    private static byte[] AssociatedData(SealedPayloadKind kind, Guid recordId) =>
        Encoding.UTF8.GetBytes($"bazaar.crash/1|{kind}|{recordId:D}");
}
