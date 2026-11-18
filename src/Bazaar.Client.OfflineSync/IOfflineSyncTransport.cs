using Bazaar.OfflineSync;

namespace Bazaar.Client.OfflineSync;

/// <summary>Sends one device batch to the server and returns the per-write outcomes (doc 52 section 4). A transport failure is reported by throwing.</summary>
public interface IOfflineSyncTransport
{
    /// <summary>Sends <paramref name="batch"/> and returns the server's result.</summary>
    Task<OfflineBatchResult> SendAsync(OfflineWriteBatch batch, CancellationToken cancellationToken);
}
