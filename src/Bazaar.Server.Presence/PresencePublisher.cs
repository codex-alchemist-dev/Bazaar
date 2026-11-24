using Bazaar.Contracts.Wire;
using Bazaar.Server.Presence.Abstractions;
using Bazaar.Server.Wire.Connection;
using Bazaar.Server.Wire.Sync;
using Bazaar.Server.WorkLife;

namespace Bazaar.Server.Presence;

/// <summary>
/// Sends <c>presence.update</c> deltas to the connections subscribed to the <c>presence</c> stream and answers the snapshot
/// (docs/design/23, "Sequence numbers on deltas"). Every delta carries the agent id and the row's revision; the snapshot carries
/// each row's revision. Delivery is best effort: a full outbound queue drops the delta, and the client recovers with the
/// snapshot, so order and loss never affect correctness (doc 02, doc 23). With no wire directory registered (a host without a
/// socket) deltas go nowhere and the snapshot still works.
/// </summary>
public sealed class PresencePublisher(
    IPresenceStore store,
    IWireConnectionDirectory? connections,
    SyncOptions sync,
    TimeProvider time) : IPresencePublisher
{
    /// <inheritdoc />
    public ValueTask PublishDeltaAsync(PresenceRow row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (connections is null)
        {
            return ValueTask.CompletedTask;
        }

        var wire = ToWire(row);
        var payload = new PresenceUpdate(PresenceKinds.Agent, wire.AgentId, wire.Revision, wire);
        var now = time.GetUtcNow();
        foreach (var connection in connections.Connections)
        {
            if (connection.Subscriptions is not { } subscriptions || !subscriptions.Streams.Contains(WireStreams.Presence))
            {
                continue;
            }

            var frame = new Envelope(
                sync.ProtocolMajor,
                WireTypes.PresenceUpdate,
                EnvelopeKind.Event,
                null,
                null,
                WireStreams.Presence,
                null,
                now,
                connection.Codec.EncodePayload(payload));
            connection.TryEnqueueEvent(frame);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<PresenceRow>> GetSnapshotAsync(CancellationToken ct) => store.GetAllAsync(ct);

    /// <summary>Maps a row to its wire shape.</summary>
    public static WirePresenceRow ToWire(PresenceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new WirePresenceRow(
            row.AgentId,
            row.Revision,
            row.Availability.ToString(),
            OverlayNames(row.Overlay),
            row.Presence,
            row.ActivityKind.ToString(),
            row.GcId,
            row.TurnId,
            row.Label,
            row.Since,
            row.UpdatedAt);
    }

    private static string[] OverlayNames(PresenceOverlay overlay) =>
        [.. Enum.GetValues<PresenceOverlay>().Where(o => o != PresenceOverlay.None && overlay.HasFlag(o)).Select(o => o.ToString())];
}
