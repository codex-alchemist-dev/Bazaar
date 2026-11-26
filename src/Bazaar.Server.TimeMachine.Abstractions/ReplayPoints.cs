using Bazaar.Contracts.Gc;

namespace Bazaar.Server.TimeMachine;

/// <summary>Kinds of <see cref="ReplayPoint"/>. Anything the GC log holds that is not listed here is reported as <see cref="Event"/>.</summary>
public static class ReplayPointKinds
{
    /// <summary>A message was posted.</summary>
    public const string Message = "message";

    /// <summary>A message was edited.</summary>
    public const string Edit = "edit";

    /// <summary>An entry was quarantined.</summary>
    public const string Quarantine = "quarantine";

    /// <summary>Any other GC log event.</summary>
    public const string Event = "event";

    /// <summary>An audit-log entry, shown to the owner only (docs/design/26, section 4).</summary>
    public const string Audit = "audit";
}

/// <summary>A point in a GC's history that a frame can be rendered at.</summary>
/// <param name="Seq">The seq to render at: the last log event at or before the point (0 before the first event).</param>
/// <param name="At">When the point happened.</param>
/// <param name="Kind">A <see cref="ReplayPointKinds"/> value.</param>
/// <param name="Label">A short label. Never a payload: for audit points it is the audit entry's kind only.</param>
public sealed record ReplayPoint(long Seq, DateTimeOffset At, string Kind, string Label);

/// <summary>One page of points.</summary>
/// <param name="Points">Points in time order.</param>
/// <param name="NextAfterSeq">Pass this as <c>afterSeq</c> for the next page, or null when the list is complete.</param>
/// <param name="Head">The viewer's visible head seq, never the true head.</param>
public sealed record ReplayPointPage(IReadOnlyList<ReplayPoint> Points, long? NextAfterSeq, long Head);

/// <summary>An entry another module marks on the timeline. The audit module registers one source; Time Machine never reads audit storage itself.</summary>
/// <param name="At">When it happened.</param>
/// <param name="Label">A short label with no payload, such as the audit entry's kind.</param>
public sealed record ReplayMarker(DateTimeOffset At, string Label);

/// <summary>
/// Supplies audit markers (docs/design/15, audit log). Optional: with none registered the list holds GC points only. Time Machine
/// asks only for the owner, because the audit log is owner-scope.
/// </summary>
public interface IReplayMarkerSource
{
    /// <summary>Markers with <paramref name="since"/> &lt;= At &lt;= <paramref name="until"/>, in time order, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<ReplayMarker>> ListAsync(DateTimeOffset since, DateTimeOffset until, int limit, CancellationToken ct);
}

/// <summary>
/// Lists the points in time a GC can be replayed at, built from the GC log and, for the owner, audit markers
/// (docs/design/26, section 4). Read-only; the viewer filter of doc 01 applies.
/// </summary>
public interface IReplayPointLister
{
    /// <summary>Largest <c>limit</c> accepted; larger values are clamped.</summary>
    public const int MaxLimit = 500;

    /// <summary>
    /// Lists points after <paramref name="afterSeq"/>. With a <paramref name="bucket"/>, points are thinned to the last event of each
    /// time bucket (aligned to the Unix epoch); without one, every log event is a point. Throws <c>GcAccessDeniedException</c> when
    /// the viewer may not read the GC.
    /// </summary>
    Task<ReplayPointPage> ListAsync(Guid gcId, long afterSeq, int limit, TimeSpan? bucket, GcPrincipal viewer, CancellationToken ct);
}
