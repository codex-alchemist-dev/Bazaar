using System.Text;

namespace Bazaar.Server.Gc.Body;

/// <summary>
/// INTERIM body codec: stores UTF-8 text unencrypted under <see cref="PlaintextKeyId"/>.
/// DECRYPTION SEAM: replace this with the per-person key codec (docs/design/04, Question 2; doc 14) before any real
/// personal data is written. Until then a GC body is readable by anyone who can read the database.
/// </summary>
public sealed class PlaintextGcBodyCodec : IGcBodyCodec
{
    /// <summary>The key id stored with plaintext bodies. The all-zero UUID never names a real person key.</summary>
    public static readonly Guid PlaintextKeyId = Guid.Empty;

    /// <inheritdoc />
    public Write.MessageBody Seal(string text) =>
        new(Encoding.UTF8.GetBytes(text), PlaintextKeyId);

    /// <inheritdoc />
    public string Open(ReadOnlyMemory<byte> ciphertext, Guid keyId, ReadOnlyMemory<byte> nonce)
    {
        if (keyId != PlaintextKeyId)
            throw new InvalidOperationException("The body is sealed under a key the plaintext codec does not hold.");
        return Encoding.UTF8.GetString(ciphertext.Span);
    }
}
