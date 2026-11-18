namespace Bazaar.Push;

/// <summary>
/// The wake kinds the server may send (doc 02, Android; doc 29). <see cref="Unknown"/> covers any value this build does not know.
/// </summary>
public enum WakeKind
{
    Unknown,
    Mention,
    DirectMessage,
    OwnerDecision,
    Quota,
}

/// <summary>
/// The result of one wake, after the handler has fetched over the authenticated connection.
/// </summary>
public enum WakeOutcome
{
    /// <summary>The fetch ran and found nothing new. No notification is shown.</summary>
    NothingNew,

    /// <summary>The fetch found new events and a local notification was shown.</summary>
    Notified,

    /// <summary>The fetch or the notification failed. The cursor is unchanged, so the next wake retries.</summary>
    Failed,
}

/// <summary>
/// Fetches what a wake points at over the authenticated connection. The kind is the only input: no GC id, no
/// sequence number and no text (doc 02, <c>WakeHint</c>; C41). Implementations hold the socket and the cursors.
/// </summary>
public interface IWakeFetcher
{
    /// <summary>Opens a short-lived socket, runs <c>hello</c> and <c>subscribe</c> from the cursors, and closes it.</summary>
    Task<WakeFetchResult> FetchAsync(WakeKind kind, CancellationToken ct);
}

/// <summary>The number of events the fetch applied. Zero means the wake was late or repeated.</summary>
public sealed record WakeFetchResult(int NewEventCount);

/// <summary>
/// Shows the local notification for a wake that found new events. The lock screen text is fixed by the head (doc 29),
/// so nothing from the fetched content reaches this interface.
/// </summary>
public interface ILocalNotifier
{
    Task ShowAsync(WakeKind kind, CancellationToken ct);
}

/// <summary>Receives a bare wake from a push provider and runs the fetch.</summary>
public interface IBareWakeHandler
{
    /// <summary>
    /// Handles one wake. <paramref name="rawKind"/> is the kind token the provider adapter extracted from its message, or
    /// null when the message had none. Any other field of the provider message must be dropped by the adapter.
    /// </summary>
    Task<WakeOutcome> HandleAsync(string? rawKind, CancellationToken ct);
}
