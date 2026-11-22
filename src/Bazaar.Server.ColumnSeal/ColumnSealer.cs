using System.Security.Cryptography;

namespace Bazaar.Server.ColumnSeal;

/// <summary>
/// AES-256-GCM sealing under a scope DEK (doc 63 section 9). A fresh random 12-byte nonce is drawn for every seal; the stored
/// ciphertext is the encrypted bytes followed by the 16-byte tag. The associated data is built by <see cref="ColumnSealNames"/>.
/// </summary>
/// <remarks>
/// Class B (personal identifiers under the person's key) is not sealed here. The seam for it is a second
/// <see cref="IColumnDekStore"/> whose scope is the person's key, to be added with the class B work package.
/// </remarks>
public sealed class ColumnSealer(IColumnDekStore keys) : IColumnSealer
{
    private const int _nonceSize = 12;
    private const int _tagSize = 16;

    /// <inheritdoc />
    public async ValueTask<SealedColumnValue> SealAsync(ReadOnlyMemory<byte> plaintext, ColumnSealContext context, CancellationToken cancellationToken)
    {
        ColumnSealNames.ValidateContext(context);
        using var dek = await keys.GetOrCreateActiveAsync(context.Scope, cancellationToken).ConfigureAwait(false);
        if (dek.Retired || dek.Scope != context.Scope)
            throw new ColumnSealException(ColumnSealFailure.KeyUnavailable, "No data key is available for the scope.");

        var nonce = RandomNumberGenerator.GetBytes(_nonceSize);
        var output = new byte[plaintext.Length + _tagSize];
        using (var aes = new AesGcm(dek.Key, _tagSize))
        {
            aes.Encrypt(nonce, plaintext.Span, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length), ColumnSealNames.BuildAad(context, dek.DekId));
        }

        return new SealedColumnValue(output, nonce, dek.DekId);
    }

    /// <inheritdoc />
    public async ValueTask<byte[]> OpenAsync(SealedColumnValue sealedValue, ColumnSealContext context, CancellationToken cancellationToken)
    {
        ColumnSealNames.ValidateContext(context);
        if (sealedValue.Nonce.Length != _nonceSize || sealedValue.Ciphertext.Length < _tagSize)
            throw CannotOpen();

        using var dek = await keys.GetAsync(sealedValue.KeyId, cancellationToken).ConfigureAwait(false)
            ?? throw new ColumnSealException(ColumnSealFailure.KeyUnavailable, "The data key for this value is not available.");
        if (dek.Scope != context.Scope)
            throw CannotOpen();

        var cipher = sealedValue.Ciphertext.Span;
        var bodyLength = cipher.Length - _tagSize;
        var plaintext = new byte[bodyLength];
        try
        {
            using var aes = new AesGcm(dek.Key, _tagSize);
            aes.Decrypt(sealedValue.Nonce.Span, cipher[..bodyLength], cipher[bodyLength..], plaintext, ColumnSealNames.BuildAad(context, dek.DekId));
            return plaintext;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw CannotOpen();
        }
    }

    private static ColumnSealException CannotOpen() =>
        new(ColumnSealFailure.CannotOpen, "The sealed value cannot be opened for this row and column.");
}
