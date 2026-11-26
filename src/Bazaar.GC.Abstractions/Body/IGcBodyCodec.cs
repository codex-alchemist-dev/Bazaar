using Bazaar.Server.Gc.Write;

namespace Bazaar.Server.Gc.Body;

/// <summary>
/// Seals message bodies before they reach the log and opens them when they are read (docs/design/01 WP6; docs/design/04,
/// Question 2). Each body is encrypted under its author's per-person key, so erasing the author removes that author's text from
/// every GC. The log stores only what <see cref="SealAsync"/> returns; the writer never sees plaintext.
/// </summary>
/// <remarks>
/// Async because the key lives in <c>person_key</c>, which is read through the database. Opening a body whose key was destroyed
/// returns <c>null</c>; readers show <see cref="ErasedText"/> in its place. The tombstone and the log structure stay intact.
/// </remarks>
public interface IGcBodyCodec
{
    /// <summary>Seals the text of a new message version under the author's current key, creating the key when needed.</summary>
    ValueTask<MessageBody> SealAsync(Guid authorId, string text, CancellationToken ct);

    /// <summary>
    /// Opens a stored body. Returns <c>null</c> when the key was destroyed by erasure. Throws when the key is unknown or the
    /// ciphertext does not verify.
    /// </summary>
    ValueTask<string?> OpenAsync(ReadOnlyMemory<byte> ciphertext, Guid keyId, ReadOnlyMemory<byte> nonce, CancellationToken ct);
}

/// <summary>Text shown in place of a body whose key was destroyed (docs/design/01, Personal data and erasure).</summary>
public static class GcBodyText
{
    public const string ErasedText = "[erased]";
}
