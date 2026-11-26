namespace Bazaar.Client.OfflineSync;

/// <summary>
/// Composes the client offline stack without a DI container (doc 56 section 6). The queue opens with
/// <see cref="OfflineQueueRepairMode.QuarantineTornTail"/>, so a torn final record is quarantined and reported as an Attention
/// count on the next <see cref="OfflineSyncClient.SyncAsync"/> instead of blocking the client.
/// </summary>
public static class OfflineSyncClientFactory
{
    /// <summary>Opens the queue in <paramref name="queueDirectory"/> and wires the transport, state and replay driver to it.</summary>
    /// <param name="http">The client to the server. Its base address is the server root; the batch route is appended.</param>
    /// <param name="credential">Returns the device bearer token, or null when none is held.</param>
    /// <param name="queueDirectory">The directory that holds the queue file. Created when absent.</param>
    /// <param name="keys">Supplies the 32-byte queue key.</param>
    /// <param name="anchors">Stores the queue anchor outside the queue file. Torn-tail repair needs it.</param>
    /// <param name="deviceId">The device whose writes the queue holds.</param>
    /// <exception cref="OfflineQueueCorruptedException">The queue failed verification in a way repair does not cover.</exception>
    public static OfflineSyncClient Create(
        HttpClient http,
        Func<string?> credential,
        string queueDirectory,
        IOfflineQueueKeyProvider keys,
        IOfflineQueueAnchorStore anchors,
        Guid deviceId)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrEmpty(queueDirectory);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(anchors);

        var queue = new EncryptedOfflineQueue(queueDirectory, deviceId, keys, limits: null, OfflineQueueRepairMode.QuarantineTornTail, anchors);
        try
        {
            var transport = new HttpOfflineSyncTransport(http, credential);
            var state = new ConnectionStateTracker();
            var driver = new OfflineReplayDriver(queue, transport, state);
            return new OfflineSyncClient(queue, driver, state);
        }
        catch
        {
            queue.Dispose();
            throw;
        }
    }
}
