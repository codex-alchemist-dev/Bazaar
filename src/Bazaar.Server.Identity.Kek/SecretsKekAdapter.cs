using System.Security.Cryptography;

namespace Bazaar.Server.Identity.Kek;

/// <summary>
/// Presents the one host KEK (<see cref="IKekProvider"/>, DPAPI or a key file) through the key-id-aware contract the secret store
/// uses (<see cref="Bazaar.Secrets.IKekProvider"/>). This keeps a single custody path: the secret store and the Soul DEK service
/// wrap under the same key, and switching providers is one re-wrap. A wrap made under another KEK id is refused the same way the
/// store's reference provider refuses it, with a <see cref="CryptographicException"/>.
/// </summary>
public sealed class SecretsKekAdapter(IKekProvider inner) : Bazaar.Secrets.IKekProvider
{
    /// <inheritdoc />
    public string CurrentKeyId => inner.KekId;

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<byte>> WrapAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> associatedData, CancellationToken ct) =>
        await inner.WrapAsync(key, associatedData, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<byte>> UnwrapAsync(string keyId, ReadOnlyMemory<byte> wrapped, ReadOnlyMemory<byte> associatedData, CancellationToken ct)
    {
        if (!string.Equals(keyId, inner.KekId, StringComparison.Ordinal))
            throw new CryptographicException("The wrapped key was sealed under a KEK this provider does not hold.");

        try
        {
            return await inner.UnwrapAsync(wrapped, associatedData, ct).ConfigureAwait(false);
        }
        catch (KekUnwrapException ex)
        {
            throw new CryptographicException(ex.Message, ex);
        }
    }
}
