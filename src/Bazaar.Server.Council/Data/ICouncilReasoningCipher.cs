namespace Bazaar.Server.Council.Data;

/// <summary>
/// The stored form of one per-person encrypted field (doc 04): the key that sealed it, the ciphertext and the nonce.
/// Mirrors the <c>gc_message_version</c> shape (<c>key_id</c>, <c>body_ct</c>, <c>body_nonce</c>).
/// </summary>
/// <param name="KeyId">The per-person key id the ciphertext was sealed under.</param>
/// <param name="Ciphertext">The sealed reasoning text.</param>
/// <param name="Nonce">The nonce used for the seal.</param>
public sealed record ReasoningCiphertext(Guid KeyId, byte[] Ciphertext, byte[] Nonce);

/// <summary>
/// Seals and opens ballot reasoning under the voter's per-person key (doc 04, per-person encrypted fields; doc 10).
/// </summary>
/// <remarks>
/// The production implementation is the host's cipher over the per-person key store (doc 04, doc 14); this component never
/// holds key material and ships no production cipher. Tests use an in-memory fake. Both calls are async because the key store
/// reads Postgres and unwraps under the host KEK.
/// </remarks>
public interface ICouncilReasoningCipher
{
    /// <summary>Seals <paramref name="plaintext"/> under the voter's current per-person key.</summary>
    ValueTask<ReasoningCiphertext> SealAsync(string voterId, string plaintext, CancellationToken ct);

    /// <summary>Opens a sealed reasoning text for the voter. Throws when the key is destroyed (erasure) or the seal is invalid.</summary>
    ValueTask<string> OpenAsync(string voterId, ReasoningCiphertext sealedText, CancellationToken ct);
}
