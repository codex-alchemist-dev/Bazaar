namespace Bazaar.Kodo.Anchoring;

/// <summary>
/// An external place that keeps anchors outside the journal operator's control (doc 46 section 6.5). Concrete sinks
/// (paired devices, timestamp authority, object-locked store, transparency log) are modules and live elsewhere.
/// Implementations may throw or time out; <see cref="AnchorPublisher"/> contains every failure.
/// </summary>
public interface IAnchorSink
{
    /// <summary>Stable identifier, used in <see cref="AnchorRecord.SinkId"/> and in status output.</summary>
    string Id { get; }

    /// <summary>Publishes one anchor. Returns true when the sink durably accepted it.</summary>
    ValueTask<bool> PublishAsync(AnchorRecord anchor, CancellationToken cancellationToken);
}
