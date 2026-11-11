using System.Text;

namespace Bazaar.Client.Core.Pairing;

/// <summary>
/// Device-side storage for the refresh credential (doc 32, section 5). The access token is never stored: it lives in memory
/// only. The server keeps a hash of the refresh credential, never the value.
/// </summary>
public interface IRefreshCredentialStore
{
    /// <summary>The stored refresh credential, or <c>null</c> when the device is not paired or the stored value can no longer be read.</summary>
    Task<string?> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Replaces the stored refresh credential. Rotation (each refresh) and a new pairing both call this.</summary>
    Task SaveAsync(string refreshCredential, CancellationToken cancellationToken = default);

    /// <summary>Removes the stored refresh credential, for sign-out and revocation.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Seals and opens bytes under a device-bound key. On Android this is AES-256-GCM under a non-exportable Keystore key.
/// </summary>
public interface ICredentialSealer
{
    /// <summary>Encrypts and authenticates <paramref name="plaintext"/>. The result is opaque to the caller.</summary>
    byte[] Seal(byte[] plaintext);

    /// <summary>
    /// Reverses <see cref="Seal"/>. Returns <c>null</c> when the sealed bytes are not authentic or the key is gone (Keystore
    /// loss, device reset). A <c>null</c> result means the credential is unrecoverable and the device must pair again.
    /// </summary>
    byte[]? Open(byte[] sealedBytes);
}

/// <summary>Persists the sealed bytes. On Android this is a file in app-private storage.</summary>
public interface ISealedBlobStore
{
    Task<byte[]?> ReadAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(byte[] blob, CancellationToken cancellationToken = default);

    Task DeleteAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The refresh credential sealed by <see cref="ICredentialSealer"/> and kept in <see cref="ISealedBlobStore"/>. Plaintext
/// never reaches the blob store. If the sealed value cannot be opened, the stored blob is deleted and the credential reads as
/// absent, so a lost Keystore key forces re-pairing (doc 32, section 3 lifecycle; doc 29).
/// </summary>
public sealed class SealedRefreshCredentialStore(ICredentialSealer sealer, ISealedBlobStore blobs) : IRefreshCredentialStore
{
    private readonly ICredentialSealer _sealer = sealer ?? throw new ArgumentNullException(nameof(sealer));
    private readonly ISealedBlobStore _blobs = blobs ?? throw new ArgumentNullException(nameof(blobs));

    public async Task<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var blob = await _blobs.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (blob is null)
        {
            return null;
        }

        var plaintext = _sealer.Open(blob);
        if (plaintext is null)
        {
            await _blobs.DeleteAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        return Encoding.UTF8.GetString(plaintext);
    }

    public Task SaveAsync(string refreshCredential, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshCredential);
        var sealedBlob = _sealer.Seal(Encoding.UTF8.GetBytes(refreshCredential));
        return _blobs.WriteAsync(sealedBlob, cancellationToken);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default) => _blobs.DeleteAsync(cancellationToken);
}
