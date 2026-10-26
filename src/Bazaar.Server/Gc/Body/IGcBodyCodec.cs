using Bazaar.Server.Gc.Write;

namespace Bazaar.Server.Gc.Body;

/// <summary>
/// Seals message bodies before they reach the log and opens them when the wire reads them. The log stores only what
/// <see cref="Seal"/> returns (docs/design/01, WP6 personal fields), so the writer never sees plaintext.
/// </summary>
public interface IGcBodyCodec
{
    /// <summary>Seals the text of a new message version.</summary>
    MessageBody Seal(string text);

    /// <summary>Opens a stored body. Throws when the codec does not hold <paramref name="keyId"/>.</summary>
    string Open(ReadOnlyMemory<byte> ciphertext, Guid keyId, ReadOnlyMemory<byte> nonce);
}
