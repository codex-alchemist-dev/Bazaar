namespace Bazaar.Server.Presence.Abstractions;

/// <summary>Sends presence to clients: deltas as they happen, and the snapshot a client starts from or recovers with (docs/design/23).</summary>
public interface IPresencePublisher
{
    /// <summary>Pushes one row as a <c>presence.update</c> delta to the connections subscribed to <c>presence</c>. Best effort: a client that misses it recovers with the snapshot.</summary>
    ValueTask PublishDeltaAsync(PresenceRow row, CancellationToken ct);

    /// <summary>Every agent's current row, each with its revision.</summary>
    ValueTask<IReadOnlyList<PresenceRow>> GetSnapshotAsync(CancellationToken ct);
}
