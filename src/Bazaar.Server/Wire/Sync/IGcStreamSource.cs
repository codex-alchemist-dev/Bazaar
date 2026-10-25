using Bazaar.Contracts.Wire;

namespace Bazaar.Server.Wire.Sync;

/// <summary>The latest counters of one GC: <c>n</c> for log entries, <c>ev</c> for message versions.</summary>
/// <param name="LatestN">Highest assigned <c>n</c>; 0 for an empty GC.</param>
/// <param name="LatestEv">Highest assigned <c>ev</c>; 0 for an empty GC.</param>
public readonly record struct GcHead(long LatestN, long LatestEv);

/// <summary>
/// Read side of the GC log that the subscription engine and dispatcher need (doc 01 <c>IGcLog.ReadAsync</c>,
/// doc 04 replay). G3 implements it over Postgres; tests use an in-memory fake. Access checks are not its job:
/// the caller has already passed <see cref="IGcAccessPolicy"/>.
/// </summary>
public interface IGcStreamSource
{
    /// <summary>The GC's counters, or null when the GC does not exist.</summary>
    ValueTask<GcHead?> GetHeadAsync(Guid gcId, CancellationToken ct);

    /// <summary>
    /// Message entries a client lacks, ascending by <c>Ev</c>: those with <c>N &gt; after</c> or
    /// <c>Ev &gt; editAfter</c>, and <c>Ev &gt; afterEv</c> (the paging cursor). Each message appears once, as its
    /// current version. Returns at most <paramref name="limit"/> items; fewer than that means the end of the log
    /// as of the read.
    /// </summary>
    ValueTask<IReadOnlyList<WireGcMessage>> ReadAsync(
        Guid gcId, long after, long editAfter, long afterEv, int limit, CancellationToken ct);

    /// <summary>The last <paramref name="limit"/> message entries, oldest first, as current versions.</summary>
    ValueTask<IReadOnlyList<WireGcMessage>> ReadTailAsync(Guid gcId, int limit, CancellationToken ct);

    /// <summary>Unread entries for the viewer, for <c>resync</c>.</summary>
    ValueTask<int> CountUnreadAsync(Guid gcId, Guid viewerId, CancellationToken ct);
}

/// <summary>Who may read a GC stream (membership, DM rules). Denied and missing must look the same to the client.</summary>
public interface IGcAccessPolicy
{
    /// <summary>True when the principal may read the GC.</summary>
    ValueTask<bool> CanReadAsync(Guid principalId, Guid gcId, CancellationToken ct);
}

/// <summary>One notification from the post-commit <c>NOTIFY</c> (doc 04): the GC and its new <c>n</c>.</summary>
/// <param name="GcId">The GC; <see cref="Guid.Empty"/> for a reconnect marker.</param>
/// <param name="Seq">The <c>n</c> appended; 0 for a reconnect marker.</param>
/// <param name="Reconnected">True when the listener lost its connection and re-established it, so any notification may have been missed.</param>
public readonly record struct GcNotification(Guid GcId, long Seq, bool Reconnected = false)
{
    /// <summary>The marker the listener emits after a reconnect.</summary>
    public static GcNotification ReconnectMarker => new(Guid.Empty, 0, true);
}

/// <summary>The listener side (G3 <c>GcNotifyListener</c>): a stream of notifications, in commit order per GC.</summary>
public interface IGcNotificationSource
{
    /// <summary>Notifications until cancelled. Must emit <see cref="GcNotification.ReconnectMarker"/> after a reconnect.</summary>
    IAsyncEnumerable<GcNotification> ReadAllAsync(CancellationToken ct);
}
