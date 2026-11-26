namespace Bazaar.Client.OfflineSync.PlatformKeys;

/// <summary>No wrapping: the key sits in the key file, which <see cref="ProtectedFileKeyProvider"/> restricts to its owner (mode 0600 on Unix). For platforms without a user key store (doc 56 section 6, same rule as the KEK).</summary>
public sealed class KeyFileOfflineKeyProtector : IOfflineKeyProtector
{
    /// <inheritdoc />
    public byte[] Protect(ReadOnlySpan<byte> plaintext) => plaintext.ToArray();

    /// <inheritdoc />
    public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes) => protectedBytes.ToArray();
}
