using Bazaar.Server.Gc.Body;

namespace Bazaar.Server.Interrupts;

/// <summary>
/// Seals interrupt reasons through the GC body codec, so each reason is encrypted under its author's per-person key (doc 01 WP6)
/// and erasing the author removes it. The module never holds a key and never logs a reason.
/// </summary>
public sealed class GcBodyInterruptReasonCipher(IGcBodyCodec codec) : IInterruptReasonCipher
{
    /// <inheritdoc />
    public async ValueTask<SealedReason> SealAsync(Guid authorId, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var body = await codec.SealAsync(authorId, reason, ct).ConfigureAwait(false);
        return new SealedReason(body.KeyId, body.Nonce.ToArray(), body.BodyCiphertext.ToArray());
    }

    /// <inheritdoc />
    public ValueTask<string?> OpenAsync(SealedReason value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return codec.OpenAsync(value.Ciphertext, value.KeyId, value.Nonce, ct);
    }
}
