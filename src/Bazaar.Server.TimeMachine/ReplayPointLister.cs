using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Read;

namespace Bazaar.Server.TimeMachine;

/// <summary>
/// Default <see cref="IReplayPointLister"/>. Scans one page of the log through <see cref="IGcLogReader"/>, so grants and viewer
/// filters are the reader's. Writes nothing.
/// </summary>
/// <param name="reader">The GC log reader.</param>
/// <param name="access">Read grants.</param>
/// <param name="markers">Audit markers, or null when no audit module is installed.</param>
public sealed class ReplayPointLister(IGcLogReader reader, IGcReadAccess access, IReplayMarkerSource? markers = null) : IReplayPointLister
{
    private const int _scanLimit = 5_000;
    private const int _markerLimit = 1_000;

    /// <inheritdoc />
    public async Task<ReplayPointPage> ListAsync(Guid gcId, long afterSeq, int limit, TimeSpan? bucket, GcPrincipal viewer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        if (bucket is { } b && b <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(bucket), "A bucket must be longer than zero.");
        }

        limit = Math.Clamp(limit, 1, IReplayPointLister.MaxLimit);
        afterSeq = Math.Max(0, afterSeq);
        var grant = await access.GetGrantAsync(gcId, viewer, ct).ConfigureAwait(false);
        if (!grant.Allowed)
        {
            throw new GcAccessDeniedException(gcId);
        }

        var head = await reader.GetHeadSeqAsync(gcId, ct).ConfigureAwait(false);
        if (grant.ThroughSeq is { } through)
        {
            head = Math.Min(head, Math.Max(0, through));
        }

        var events = new List<GcEvent>();
        await foreach (var e in reader.ReadAsync(gcId, afterSeq, _scanLimit + 1, viewer, ct).ConfigureAwait(false))
        {
            events.Add(e);
        }

        // One event past the scan limit tells whether more follow; it is dropped from this page.
        var truncated = events.Count > _scanLimit;
        if (truncated)
        {
            events.RemoveAt(events.Count - 1);
        }

        var points = bucket is { } size ? Thin(events, size) : [.. events.Select(ToPoint)];

        long? next = null;
        if (points.Count > limit)
        {
            // Resume after the last point that fits, so the next page starts at the first point left over.
            points = points[..limit];
            next = points[^1].Seq;
        }
        else if (truncated)
        {
            next = events[^1].Seq;
        }

        if (markers is not null && viewer.Kind == PrincipalKind.Owner)
        {
            points = await MergeMarkersAsync(points, events, afterSeq, next, grant.ThroughSeq is not null, ct).ConfigureAwait(false);
        }

        return new ReplayPointPage(points, next, head);
    }

    private static List<ReplayPoint> Thin(List<GcEvent> events, TimeSpan size)
    {
        var points = new List<ReplayPoint>();
        for (var i = 0; i < events.Count; i++)
        {
            var isLast = i == events.Count - 1 || BucketOf(events[i + 1].CreatedAt, size) != BucketOf(events[i].CreatedAt, size);
            if (isLast)
            {
                points.Add(ToPoint(events[i]));
            }
        }

        return points;
    }

    private static long BucketOf(DateTimeOffset at, TimeSpan size) => at.UtcTicks / size.Ticks;

    private static ReplayPoint ToPoint(GcEvent e) => e.Kind switch
    {
        GcEventKinds.Message => new(e.Seq, e.CreatedAt, ReplayPointKinds.Message, "Message"),
        GcEventKinds.MessageEdit => new(e.Seq, e.CreatedAt, ReplayPointKinds.Edit, "Message edited"),
        GcEventKinds.Quarantine => new(e.Seq, e.CreatedAt, ReplayPointKinds.Quarantine, "Entry quarantined"),
        _ => new(e.Seq, e.CreatedAt, ReplayPointKinds.Event, e.Kind),
    };

    private async Task<List<ReplayPoint>> MergeMarkersAsync(
        List<ReplayPoint> points, List<GcEvent> events, long afterSeq, long? next, bool grantEnds, CancellationToken ct)
    {
        if (events.Count == 0)
        {
            return points;
        }

        // A page owns the markers from its first event up to, not including, the first event of the next page, so a marker is
        // listed once and never hints at activity past what this viewer's page shows. The first page also owns earlier markers.
        var from = afterSeq == 0 ? DateTimeOffset.MinValue : events[0].CreatedAt;
        var to = DateTimeOffset.MaxValue;
        if (next is { } cut)
        {
            var after = events.FirstOrDefault(e => e.Seq > cut);
            to = (after?.CreatedAt ?? events[^1].CreatedAt).AddTicks(-1);
        }
        else if (grantEnds)
        {
            to = events[^1].CreatedAt;
        }

        var found = await markers!.ListAsync(from, to, _markerLimit, ct).ConfigureAwait(false);
        var merged = new List<ReplayPoint>(points);
        foreach (var m in found)
        {
            merged.Add(new ReplayPoint(SeqAt(events, afterSeq, m.At), m.At, ReplayPointKinds.Audit, m.Label));
        }

        // Stable by time; at a tie the audit marker follows the event it sits beside.
        return [.. merged.Select((p, i) => (p, i)).OrderBy(x => x.p.At).ThenBy(x => x.p.Kind == ReplayPointKinds.Audit ? 1 : 0).ThenBy(x => x.i).Select(x => x.p)];
    }

    private static long SeqAt(List<GcEvent> events, long afterSeq, DateTimeOffset at)
    {
        var seq = afterSeq;
        foreach (var e in events)
        {
            if (e.CreatedAt > at)
            {
                break;
            }

            seq = e.Seq;
        }

        return seq;
    }
}
