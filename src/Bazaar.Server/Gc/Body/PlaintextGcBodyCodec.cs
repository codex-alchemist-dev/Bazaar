using System.Text;

namespace Bazaar.Server.Gc.Body;

/// <summary>
/// INTERIM codec for tests and for reading rows written before per-person keys existed: stores UTF-8 text unencrypted under
/// <see cref="PlaintextKeyId"/>. Production wiring uses <see cref="AesGcmGcBodyCodec"/>, which still reads these rows.
/// </summary>
public sealed class PlaintextGcBodyCodec : IGcBodyCodec
{
    /// <summary>The key id stored with plaintext bodies. The all-zero UUID never names a real person key.</summary>
    public static readonly Guid PlaintextKeyId = Guid.Empty;

    /// <inheritdoc />
    public ValueTask<Write.MessageBody> SealAsync(Guid authorId, string text, CancellationToken ct) =>
        ValueTask.FromResult(new Write.MessageBody(Encoding.UTF8.GetBytes(text), PlaintextKeyId));

    /// <inheritdoc />
    public ValueTask<string?> OpenAsync(ReadOnlyMemory<byte> ciphertext, Guid keyId, ReadOnlyMemory<byte> nonce, CancellationToken ct)
    {
        if (keyId != PlaintextKeyId)
            throw new InvalidOperationException("The body is sealed under a key the plaintext codec does not hold.");
        return ValueTask.FromResult<string?>(Encoding.UTF8.GetString(ciphertext.Span));
    }
}
