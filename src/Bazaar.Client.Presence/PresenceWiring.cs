using Bazaar.Client.Abstractions.Wire;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Presence;

/// <summary>Connects a <see cref="PresenceReceiver"/> to a <see cref="WireClient"/> (docs/design/23, "Event sources and subscription").</summary>
public static class PresenceWiring
{
    /// <summary>
    /// Creates a receiver that fetches snapshots over <paramref name="client"/>, feeds it the client's agent deltas, fetches a snapshot
    /// after every connect, and subscribes to the <c>presence</c> stream. Dispose the result to detach.
    /// </summary>
    public static PresenceReceiver Attach(WireClient client, PresenceReceiverOptions? options = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        var receiver = new PresenceReceiver(
            ct => client.RequestAsync<PresenceSnapshotRequest, PresenceSnapshot>(WireTypes.PresenceSnapshot, new PresenceSnapshotRequest(), ct),
            options,
            time);
        client.PresenceUpdateReceived += receiver.Apply;
        client.Connected += () => _ = Task.Run(() => SafeRefreshAsync(receiver));
        _ = client.SubscribeAsync(WireStreams.Presence);
        return receiver;
    }

    private static async Task SafeRefreshAsync(PresenceReceiver receiver)
    {
        try
        {
            await receiver.RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A refused or failed snapshot leaves the held rows as they are; the next gap or reconnect asks again.
        }
    }
}
