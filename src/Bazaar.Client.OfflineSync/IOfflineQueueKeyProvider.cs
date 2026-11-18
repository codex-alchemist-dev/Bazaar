namespace Bazaar.Client.OfflineSync;

/// <summary>Supplies the 32-byte AES-256-GCM key for the offline queue (doc 52 section 6). Platform key storage implements it (DPAPI on Windows, a key file elsewhere).</summary>
public interface IOfflineQueueKeyProvider
{
    /// <summary>Returns the 32-byte queue key. The queue copies what it needs and keeps no reference to the buffer.</summary>
    ReadOnlyMemory<byte> GetKey();
}
