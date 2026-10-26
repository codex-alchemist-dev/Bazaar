using System.Runtime.CompilerServices;
using Bazaar.Server.Wire.Sync;

namespace Bazaar.Server.Gc.Read;

/// <summary>
/// Feeds the dispatcher from the G3 listener (doc 04 <c>LISTEN</c>). Each wake carries the GCs and seqs that committed since the
/// last one; a reconnect arrives as <see cref="GcNotification.ReconnectMarker"/>, so the dispatcher re-reads everything.
/// </summary>
public sealed class GcNotificationSource(GcNotifyListener listener, TimeSpan pollInterval) : IGcNotificationSource
{
    /// <summary>Creates the source with the default 30 s poll, which only bounds how long a stop request waits.</summary>
    public GcNotificationSource(GcNotifyListener listener) : this(listener, TimeSpan.FromSeconds(30))
    {
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<GcNotification> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var subscription = listener.Subscribe();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var wake = await subscription.WaitAsync(pollInterval, listener.Time, ct).ConfigureAwait(false);
            if (wake is null)
                continue;

            if (wake.Resync)
                yield return GcNotification.ReconnectMarker;

            foreach (var head in wake.Heads)
                yield return new GcNotification(head.Key, head.Value);
        }
    }
}
