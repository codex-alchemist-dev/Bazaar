namespace Bazaar.Client.TimeMachine;

/// <summary>One point in time the viewer can jump to (doc 26 section 4, snapshot list).</summary>
/// <param name="Seq">Seq to render at.</param>
/// <param name="At">When the point happened.</param>
/// <param name="Kind">message, edit, quarantine, event, or audit (owner only).</param>
/// <param name="Label">A short label with no payload.</param>
public sealed record ReplayPointView(long Seq, DateTimeOffset At, string Kind, string Label)
{
    /// <summary>True for an audit marker; the view gives it the accent colour.</summary>
    public bool IsAudit => Kind == "audit";

    /// <summary>The line the list shows: time, label, then the seq to jump to.</summary>
    public string Line => $"{At.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC · {Label} · seq {Seq:N0}";
}

/// <summary>One page of points.</summary>
/// <param name="Points">Points in time order.</param>
/// <param name="Next">Pass as <c>afterSeq</c> for the next page, or null when complete.</param>
public sealed record ReplayPointsPage(IReadOnlyList<ReplayPointView> Points, long? Next);

/// <summary>
/// Optional capability of an <see cref="IReplayClient"/>: the snapshot list. Kept separate so an existing client keeps working;
/// the pane shows no list when its client does not implement this.
/// </summary>
public interface IReplayPointClient
{
    /// <summary>Lists points after <paramref name="afterSeq"/>, at most <paramref name="limit"/>.</summary>
    /// <exception cref="ReplayClientException">The viewer may not read the GC, or the server failed.</exception>
    Task<ReplayPointsPage> GetPointsAsync(Guid gcId, long afterSeq, int limit, CancellationToken ct);
}
