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
/// TODO(component): the production implementation is the doc 04 per-person key helper (KeyStore over person_key, wrapped
/// under the bootstrap key from doc 14). The host wires it; this store never holds key material. No production cipher
/// lives in this component. Tests use an in-memory fake.
/// </remarks>
public interface ICouncilReasoningCipher
{
    /// <summary>Seals <paramref name="plaintext"/> under the voter's current per-person key.</summary>
    ReasoningCiphertext Encrypt(string voterId, string plaintext);

    /// <summary>Opens a sealed reasoning text for the voter. Throws when the key is destroyed (erasure) or the seal is invalid.</summary>
    string Decrypt(string voterId, ReasoningCiphertext sealedText);
}
