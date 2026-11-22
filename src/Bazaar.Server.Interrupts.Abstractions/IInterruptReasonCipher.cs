namespace Bazaar.Server.Interrupts;

/// <summary>A sealed reason as stored in <c>interrupt_frame</c>. <see cref="Ciphertext"/> is the cipher output followed by the tag.</summary>
/// <param name="KeyId">The key that sealed the value.</param>
/// <param name="Nonce">The cipher nonce, unique per seal.</param>
/// <param name="Ciphertext">The sealed bytes followed by the authentication tag.</param>
public sealed record SealedReason(Guid KeyId, byte[] Nonce, byte[] Ciphertext);

/// <summary>
/// Seals and opens interrupt reasons under a person's key (doc 24 section 5, "Interrupt reason sealing"). Async because the key
/// is read through the database.
/// </summary>
public interface IInterruptReasonCipher
{
    /// <summary>Seals <paramref name="reason"/> under <paramref name="authorId"/>'s current key, creating the key when needed.</summary>
    ValueTask<SealedReason> SealAsync(Guid authorId, string reason, CancellationToken ct = default);

    /// <summary>
    /// Opens a sealed reason. Returns <c>null</c> when the author's key was destroyed by erasure. Throws when the key is unknown
    /// or the value does not verify.
    /// </summary>
    ValueTask<string?> OpenAsync(SealedReason value, CancellationToken ct = default);
}
