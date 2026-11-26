namespace Bazaar.Client.OfflineSync;

/// <summary>
/// The anchor kept outside the queue file (doc 56 section 6, open item 7). It lets open tell a torn tail from a file that lost
/// whole records. In production it lives in platform key storage next to the queue key.
/// </summary>
/// <param name="RecordCount">Writes ever appended to the queue, equal to the highest device sequence appended.</param>
/// <param name="AckedSequence">The highest device sequence the server has acknowledged.</param>
public sealed record OfflineQueueAnchor(long RecordCount, long AckedSequence);

/// <summary>Stores the queue anchor outside the queue file. Implementations must make <see cref="Save"/> durable before it returns.</summary>
public interface IOfflineQueueAnchorStore
{
    /// <summary>Loads the stored anchor, or null when none has been saved.</summary>
    OfflineQueueAnchor? Load();

    /// <summary>Saves the anchor. The queue calls this after each fsynced append and each acknowledgement.</summary>
    /// <param name="anchor">The anchor to save.</param>
    void Save(OfflineQueueAnchor anchor);
}

/// <summary>In-memory anchor store for tests and single-process use. Thread safe. It does not survive a process restart.</summary>
public sealed class InMemoryOfflineQueueAnchorStore : IOfflineQueueAnchorStore
{
    private readonly Lock _gate = new();
    private OfflineQueueAnchor? _anchor;

    /// <inheritdoc />
    public OfflineQueueAnchor? Load()
    {
        lock (_gate)
        {
            return _anchor;
        }
    }

    /// <inheritdoc />
    public void Save(OfflineQueueAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        lock (_gate)
        {
            _anchor = anchor;
        }
    }
}
