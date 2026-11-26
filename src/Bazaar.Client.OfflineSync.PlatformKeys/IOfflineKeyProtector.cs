namespace Bazaar.Client.OfflineSync.PlatformKeys;

/// <summary>Wraps and unwraps the queue key with whatever the platform offers (DPAPI on Windows, nothing but file permissions elsewhere).</summary>
public interface IOfflineKeyProtector
{
    /// <summary>Protects <paramref name="plaintext"/> for storage on disk.</summary>
    byte[] Protect(ReadOnlySpan<byte> plaintext);

    /// <summary>Reverses <see cref="Protect"/>. Throws when the blob is damaged or belongs to another user or machine.</summary>
    byte[] Unprotect(ReadOnlySpan<byte> protectedBytes);
}
