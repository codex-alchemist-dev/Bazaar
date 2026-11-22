namespace Bazaar.Server.Gateway.Selection;

/// <summary>
/// Keeps the last decisions in a bounded ring and reads endpoint health from the tracker, for the admin panel and the client pane.
/// Records the trace and scores only: no prompt, no key, no message text.
/// </summary>
public sealed class SelectionInsights(EndpointStatsTracker tracker, TimeProvider? time = null, int capacity = 200) : ISelectionInsights
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly Queue<DecisionRecord> _ring = new();
    private readonly Lock _gate = new();

    public void Record(string routeId, SelectionDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var record = new DecisionRecord(
            _time.GetUtcNow(),
            routeId,
            decision.Profile,
            decision.TraceCode,
            [.. decision.Ranking.Select(r => new RankedView(r.Endpoint.Key, r.Endpoint.Identity, Math.Round(r.Score.Total, 4)))],
            [.. decision.Rejected.Select(r => new RejectedView(r.Endpoint.Key, r.Reason))]);
        lock (_gate)
        {
            _ring.Enqueue(record);
            while (_ring.Count > _capacity)
            {
                _ring.Dequeue();
            }
        }
    }

    /// <summary>The newest decisions first.</summary>
    public IReadOnlyList<DecisionRecord> Recent(int count = 50)
    {
        lock (_gate)
        {
            return [.. _ring.Reverse().Take(Math.Max(0, count))];
        }
    }

    public IReadOnlyList<EndpointHealthView> Endpoints() => tracker.Health();
}
