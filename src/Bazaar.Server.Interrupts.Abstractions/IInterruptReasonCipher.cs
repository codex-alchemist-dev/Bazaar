using System.Security.Cryptography;

namespace Bazaar.Server.Interrupts;

/// <summary>A sealed reason as stored in <c>interrupt_frame</c>. <see cref="Ciphertext"/> is the cipher output followed by the tag.</summary>
/// <param name="KeyId">The key that sealed the value.</param>
/// <param name="Nonce">The 12-byte cipher nonce, unique per seal.</param>
/// <param name="Ciphertext">The sealed bytes followed by the 16-byte authentication tag.</param>
public sealed record SealedReason(Guid KeyId, byte[] Nonce, byte[] Ciphertext);

/// <summary>Seals and opens interrupt reasons. The associated data binds each value to its row.</summary>
public interface IInterruptReasonCipher
{
    /// <summary>The key id written with every value this cipher seals.</summary>
    Guid KeyId { get; }

    /// <summary>Seals <paramref name="reason"/>. The same <paramref name="associatedData"/> must be given to <see cref="Open"/>.</summary>
    SealedReason Seal(string reason, ReadOnlySpan<byte> associatedData);

    /// <summary>Opens a sealed reason. Throws <see cref="CryptographicException"/> when the key, the data or the value is wrong.</summary>
    string Open(SealedReason value, ReadOnlySpan<byte> associatedData);
}
