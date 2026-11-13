namespace Bazaar.Components.ManagedPostgres.Secrets;

/// <summary>
/// Supplies the 256-bit key that <see cref="AesGcmManagedPasswordStore"/> encrypts with. The key source is the OS keyring
/// or a host-held key; no implementation is registered by this component yet, so the encrypted store cannot be used until one is.
/// </summary>
public interface IManagedStoreKeyProvider
{
    /// <summary>Returns a 32-byte key. Implementations must not log or return a key they cannot vouch for.</summary>
    Task<byte[]> GetKeyAsync(CancellationToken cancellationToken);
}
