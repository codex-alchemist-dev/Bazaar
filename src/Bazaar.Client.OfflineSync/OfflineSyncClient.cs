using Bazaar.OfflineSync;

namespace Bazaar.Client.OfflineSync;

/// <summary>
/// The composed client offline stack (doc 56 section 6): the encrypted queue, the replay driver and the connection state, behind
/// one object. Built by <see cref="OfflineSyncClientFactory.Create"/>. Not thread safe for overlapping runs: a second
/// <see cref="SyncAsync"/> while one is in progress throws.
/// </summary>
public sealed class OfflineSyncClient : IDisposable
{
    private readonly EncryptedOfflineQueue _queue;
    private readonly OfflineReplayDriver _driver;
    private int _running;
    private bool _disposed;

    internal OfflineSyncClient(EncryptedOfflineQueue queue, OfflineReplayDriver driver, ConnectionStateTracker state)
    {
        _queue = queue;
        _driver = driver;
        State = state;
    }

    /// <summary>The connection state. Heads read <see cref="ConnectionStateTracker.Current"/> or subscribe to <see cref="ConnectionStateTracker.StateChanged"/>.</summary>
    public ConnectionStateTracker State { get; }

    /// <summary>The device whose writes this client queues.</summary>
    public Guid DeviceId => _queue.DeviceId;

    /// <summary>
    /// Appends a write to the local queue. The append is fsynced before the result is returned, so an accepted write survives a crash.
    /// A full queue returns <see cref="EnqueueStatus.Full"/> with the reason to show the person.
    /// </summary>
    /// <param name="write">The write. Its device sequence must be the queue's next sequence (see <see cref="EncryptedOfflineQueue.NextSequence"/>).</param>
    /// <param name="cancellationToken">Checked before the append starts.</param>
    public Task<EnqueueResult> EnqueueAsync(OfflineWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        // The append fsyncs, so it runs off the caller's thread.
        return Task.Run(() => _queue.Enqueue(write), CancellationToken.None);
    }

    /// <summary>Replays the queue to the server once, until it drains, the transport fails, or progress stops.</summary>
    /// <param name="cancellationToken">Cancels the run. Unacknowledged writes stay queued.</param>
    /// <exception cref="InvalidOperationException">Another run is already in progress.</exception>
    public async Task<OfflineReplayResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("An offline sync run is already in progress.");
        }

        try
        {
            return await _driver.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>
    /// Clears the Attention raised by a torn-tail repair once a person has seen it. The next <see cref="SyncAsync"/> no longer
    /// counts the lost write. The damaged file's copy stays on disk.
    /// </summary>
    public void AcknowledgeRepair()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _queue.AcknowledgeRepair();
    }

    /// <summary>Closes the queue file. Writes already queued stay on disk for the next open.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.Dispose();
    }
}
