namespace Bazaar.Client.TimeMachine;

/// <summary>
/// A scripted <see cref="IReplayClient"/> for tests and for heads that run without a server. Frames come from a function
/// of the seq, so a test controls exactly what each point of history shows. Records every call.
/// </summary>
public sealed class InMemoryReplayClient : IReplayClient, IReplayPointClient
{
    /// <summary>The points this client lists, in seq order. Empty by default.</summary>
    public List<ReplayPointView> Points { get; } = [];

    public List<long> PointRequests { get; } = [];

    public Task<ReplayPointsPage> GetPointsAsync(Guid gcId, long afterSeq, int limit, CancellationToken ct)
    {
        PointRequests.Add(afterSeq);
        if (Failure is { } failure)
        {
            throw failure;
        }

        var page = Points.Where(p => p.Seq > afterSeq).Take(limit).ToList();
        var more = Points.Count(p => p.Seq > afterSeq) > page.Count;
        return Task.FromResult(new ReplayPointsPage(page, more && page.Count > 0 ? page[^1].Seq : null));
    }

    private readonly Func<Guid, long, ReplayFrameView> _frames;
    private readonly Dictionary<(Guid Gc, long Seq, Guid Request), ReplayForkOutcome> _forks = [];

    /// <param name="headSeq">The newest seq. A frame request above it is clamped.</param>
    /// <param name="frames">Builds the frame for a GC and an effective seq; null builds an empty one.</param>
    public InMemoryReplayClient(long headSeq, Func<Guid, long, ReplayFrameView>? frames = null)
    {
        HeadSeq = headSeq;
        _frames = frames ?? DefaultFrame;
    }

    public long HeadSeq { get; }

    /// <summary>When set, every call throws it. Lets a test show the error path.</summary>
    public ReplayClientException? Failure { get; set; }

    public List<(Guid GcId, long Seq)> FrameRequests { get; } = [];

    public List<ReplayForkRequest> ForkRequests { get; } = [];

    public Task<ReplayFrameView> GetFrameAsync(Guid gcId, long seq, CancellationToken ct)
    {
        FrameRequests.Add((gcId, seq));
        if (Failure is { } failure)
        {
            throw failure;
        }

        var effective = Math.Clamp(seq, 0, HeadSeq);
        var frame = _frames(gcId, effective) with { EffectiveSeq = effective, HeadSeq = HeadSeq, Clamped = effective < seq };
        return Task.FromResult(frame);
    }

    public Task<ReplayForkOutcome> ForkAsync(ReplayForkRequest request, CancellationToken ct)
    {
        ForkRequests.Add(request);
        if (Failure is { } failure)
        {
            throw failure;
        }

        var seq = Math.Clamp(request.SourceSeq, 0, HeadSeq);
        var key = (request.SourceGcId, seq, request.RequestId);
        if (_forks.TryGetValue(key, out var earlier))
        {
            return Task.FromResult(earlier with { Created = false });
        }

        var outcome = new ReplayForkOutcome(Guid.NewGuid(), seq, true);
        _forks[key] = outcome;
        return Task.FromResult(outcome);
    }

    private ReplayFrameView DefaultFrame(Guid gcId, long seq) =>
        new(gcId, seq, HeadSeq, false, [], []);
}
