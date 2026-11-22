namespace Bazaar.Server.Gateway.Selection;

/// <summary>Priors and constants of the scorer.</summary>
public sealed record SelectorOptions
{
    /// <summary>Beta prior successes. With <see cref="PriorFailures"/> it sets how fast a new endpoint is trusted.</summary>
    public double PriorSuccesses { get; init; } = 2;

    public double PriorFailures { get; init; } = 1;

    /// <summary>Latency assumed for an endpoint with no samples, so it is neither best nor worst.</summary>
    public TimeSpan UnknownLatency { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Concurrency assumed when an endpoint states none; each in-flight call past it multiplies effective latency.</summary>
    public int DefaultSoftConcurrency { get; init; } = 8;
}

/// <summary>
/// The selector (doc 92). Hard filters with reasons, tiers, a weighted score of normalised factors, then a strategy that
/// turns scores into an order. Pure: no I/O, no clock beyond the injected one, and no endpoint ever appears in the answer
/// that the caller did not pass in.
/// </summary>
public sealed class EndpointSelector : IEndpointSelector
{
    private readonly IEndpointStatsTracker _stats;
    private readonly IExternalSignalSource _signals;
    private readonly ISelectionRandom _random;
    private readonly TimeProvider _time;
    private readonly SelectorOptions _options;
    private readonly Dictionary<string, SelectionProfile> _profiles;
    private readonly string _defaultProfile;

    public EndpointSelector(
        IEndpointStatsTracker stats,
        IExternalSignalSource? signals = null,
        IEnumerable<SelectionProfile>? profiles = null,
        string defaultProfile = SelectionProfiles.Balanced,
        ISelectionRandom? random = null,
        TimeProvider? time = null,
        SelectorOptions? options = null)
    {
        _stats = stats ?? throw new ArgumentNullException(nameof(stats));
        _signals = signals ?? NoSignals.Instance;
        _random = random ?? SystemSelectionRandom.Instance;
        _time = time ?? TimeProvider.System;
        _options = options ?? new SelectorOptions();
        _profiles = new Dictionary<string, SelectionProfile>(StringComparer.Ordinal);
        foreach (var p in SelectionProfiles.Defaults.Concat(profiles ?? []))
        {
            _profiles[p.Name] = p;
        }

        _defaultProfile = _profiles.ContainsKey(defaultProfile)
            ? defaultProfile
            : throw new ArgumentException($"Unknown default profile '{defaultProfile}'.", nameof(defaultProfile));
    }

    public IReadOnlyCollection<string> ProfileNames => _profiles.Keys;

    public SelectionDecision Rank(IReadOnlyList<Endpoint> allowed, SelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        ArgumentNullException.ThrowIfNull(request);
        var profile = _profiles.GetValueOrDefault(request.Profile ?? _defaultProfile) ?? _profiles[_defaultProfile];
        var now = _time.GetUtcNow();

        var survivors = new List<Candidate>();
        var rejected = new List<Rejection>();
        var seen = new HashSet<EndpointKey>();
        foreach (var endpoint in allowed)
        {
            if (!seen.Add(endpoint.Key))
            {
                continue;
            }

            if (Reject(endpoint, request, now) is { } reason)
            {
                rejected.Add(new Rejection(endpoint, reason));
                continue;
            }

            survivors.Add(new Candidate(endpoint, endpoint.Signals ?? _signals.GetSignals(endpoint.Key), _stats.Snapshot(endpoint.Key)));
        }

        if (survivors.Count == 0)
        {
            return new SelectionDecision([], rejected, null, profile.Name, $"{profile.Name}/none/r{rejected.Count}");
        }

        Score(survivors, request, profile);
        var explored = false;
        var ordered = Order(survivors, profile, out explored);
        var ranking = ordered.Select(c => new RankedEndpoint(c.Endpoint, c.Breakdown!)).ToList();
        var hedge = BuildHedge(ordered, request, profile);
        var strategy = profile.Strategy switch
        {
            SelectionStrategy.Best => "best",
            SelectionStrategy.PowerOfTwo => "p2c",
            SelectionStrategy.Thompson => "ts",
            _ => "wr",
        };
        return new SelectionDecision(ranking, rejected, hedge, profile.Name, $"{profile.Name}/{strategy}/n{ranking.Count}{(explored ? "/x" : string.Empty)}");
    }

    private RejectionReason? Reject(Endpoint e, SelectionRequest request, DateTimeOffset now)
    {
        if (request.Excluded?.Contains(e.Key) == true)
        {
            return RejectionReason.Excluded;
        }

        if ((e.Capabilities & request.Required) != request.Required)
        {
            return RejectionReason.MissingCapability;
        }

        if (e.ContextWindowTokens is { } window && (long)request.EstimatedInputTokens + request.ExpectedOutputTokens > window)
        {
            return RejectionReason.ContextTooSmall;
        }

        var signals = e.Signals ?? _signals.GetSignals(e.Key);
        if (signals.BreakerOpen)
        {
            return RejectionReason.BreakerOpen;
        }

        return signals.QuotaBlockedUntil is { } until && until > now ? RejectionReason.QuotaBlocked : null;
    }

    private void Score(List<Candidate> list, SelectionRequest request, SelectionProfile profile)
    {
        // Latency: effective = peak-EWMA latency times (1 + in-flight past the soft cap fraction); best is 1, others are best / theirs.
        var known = list.Where(c => c.Stats.PeakEwmaLatency is not null).Select(c => c.Stats.PeakEwmaLatency!.Value.TotalMilliseconds).Order().ToList();
        var fallbackMs = known.Count > 0 ? known[known.Count / 2] : _options.UnknownLatency.TotalMilliseconds;
        foreach (var c in list)
        {
            var baseMs = Math.Max(1, c.Stats.PeakEwmaLatency?.TotalMilliseconds ?? fallbackMs);
            var cap = Math.Max(1, c.Endpoint.SoftConcurrency ?? _options.DefaultSoftConcurrency);
            c.EffectiveLatencyMs = baseMs * (1.0 + ((double)c.Stats.InFlight / cap));
        }

        var bestLatency = list.Min(c => c.EffectiveLatencyMs);

        // Cost: expected price of this call; cheapest is 1; unknown price is neutral.
        double? cheapest = null;
        foreach (var c in list)
        {
            c.ExpectedCost = c.Endpoint.Price is { } p
                ? ((request.EstimatedInputTokens * p.InputPerMTok) + (request.ExpectedOutputTokens * p.OutputPerMTok)) / 1_000_000.0
                : null;
            if (c.ExpectedCost is { } cost && (cheapest is null || cost < cheapest))
            {
                cheapest = cost;
            }
        }

        var affinityHolder = request.AffinityKey is null ? null : _stats.AffinityHolder(request.AffinityKey);
        var minTier = list.Min(c => c.Endpoint.Tier);
        var w = profile.Weights;
        var total = w.Latency + w.Reliability + w.Headroom + w.Cost + w.Identity + w.Affinity + w.Quality;
        if (total <= 0)
        {
            total = 1;
        }

        foreach (var c in list)
        {
            var latency = bestLatency / c.EffectiveLatencyMs;
            var reliability = Reliability(c.Stats.Successes, c.Stats.Failures);
            var headroom = Math.Clamp(c.Signals.QuotaHeadroom ?? 1.0, 0, 1);
            var cost = c.ExpectedCost is { } x ? (cheapest is { } best && x > 0 ? Math.Clamp(best / x, 0, 1) : 1.0) : 0.5;
            var identity = IdentityScore(c.Endpoint.Identity, profile.NativeFirst);
            var affinity = affinityHolder is { } h && h.Equals(c.Endpoint.Key) ? 1.0 : 0.0;
            var quality = Math.Clamp(c.Endpoint.QualityByTask?.GetValueOrDefault(request.TaskClass, c.Endpoint.Quality) ?? c.Endpoint.Quality, 0, 1);
            var penalty = profile.SoftTiers ? Math.Clamp(profile.TierPenalty, 0, 1) * (c.Endpoint.Tier - minTier) : 0;
            c.Parts = (latency, reliability, headroom, cost, identity, affinity, quality, penalty);
            c.Breakdown = Build(c.Parts, w, total, reliability);
        }
    }

    private static ScoreBreakdown Build((double Latency, double Reliability, double Headroom, double Cost, double Identity, double Affinity, double Quality, double Penalty) p, ScoreWeights w, double total, double reliability)
    {
        var sum = ((w.Latency * p.Latency) + (w.Reliability * reliability) + (w.Headroom * p.Headroom) + (w.Cost * p.Cost)
            + (w.Identity * p.Identity) + (w.Affinity * p.Affinity) + (w.Quality * p.Quality)) / total;
        return new ScoreBreakdown(p.Latency, reliability, p.Headroom, p.Cost, p.Identity, p.Affinity, p.Quality, p.Penalty, sum - p.Penalty);
    }

    private double Reliability(double successes, double failures) =>
        (_options.PriorSuccesses + successes) / (_options.PriorSuccesses + _options.PriorFailures + successes + failures);

    private static double IdentityScore(string identity, bool nativeFirst) => identity switch
    {
        "native" => nativeFirst ? 1.0 : 0.0,
        "pooled" => nativeFirst ? 0.0 : 1.0,
        _ => 0.5,
    };

    private List<Candidate> Order(List<Candidate> survivors, SelectionProfile profile, out bool explored)
    {
        explored = false;
        var result = new List<Candidate>(survivors.Count);
        IEnumerable<IGrouping<int, Candidate>> groups = profile.SoftTiers
            ? new IGrouping<int, Candidate>[] { new SingleGroup(survivors) }
            : survivors.GroupBy(c => c.Endpoint.Tier).OrderBy(g => g.Key);
        var first = true;
        foreach (var group in groups)
        {
            var ordered = OrderGroup(group.ToList(), profile);
            if (first && ordered.Count >= 2 && profile.ExploreFraction > 0 && _random.NextDouble() < profile.ExploreFraction)
            {
                (ordered[0], ordered[1]) = (ordered[1], ordered[0]);
                explored = true;
            }

            first = false;
            result.AddRange(ordered);
        }

        return result;
    }

    private List<Candidate> OrderGroup(List<Candidate> group, SelectionProfile profile)
    {
        switch (profile.Strategy)
        {
            case SelectionStrategy.Best:
                return [.. group.OrderByDescending(c => c.Breakdown!.Total).ThenBy(c => c.Endpoint.Key.ToString(), StringComparer.Ordinal)];

            case SelectionStrategy.Thompson:
            {
                foreach (var c in group)
                {
                    var alpha = _options.PriorSuccesses + c.Stats.Successes;
                    var beta = _options.PriorFailures + c.Stats.Failures;
                    var sampled = _random.NextBeta(Math.Max(1, alpha), Math.Max(1, beta));
                    c.Sampled = Build(c.Parts, profile.Weights, TotalWeight(profile.Weights), sampled).Total;
                }

                return [.. group.OrderByDescending(c => c.Sampled).ThenBy(c => c.Endpoint.Key.ToString(), StringComparer.Ordinal)];
            }

            case SelectionStrategy.WeightedRandom:
            {
                var pool = new List<Candidate>(group);
                var output = new List<Candidate>(group.Count);
                while (pool.Count > 0)
                {
                    output.Add(Take(pool, DrawIndex(pool)));
                }

                return output;
            }

            default:
            {
                var pool = new List<Candidate>(group);
                var output = new List<Candidate>(group.Count);
                while (pool.Count > 0)
                {
                    if (pool.Count == 1)
                    {
                        output.Add(Take(pool, 0));
                        continue;
                    }

                    var a = DrawIndex(pool);
                    var b = DrawIndexExcluding(pool, a);
                    var winner = pool[a].Breakdown!.Total >= pool[b].Breakdown!.Total ? a : b;
                    output.Add(Take(pool, winner));
                }

                return output;
            }
        }
    }

    private static double TotalWeight(ScoreWeights w)
    {
        var t = w.Latency + w.Reliability + w.Headroom + w.Cost + w.Identity + w.Affinity + w.Quality;
        return t <= 0 ? 1 : t;
    }

    private static Candidate Take(List<Candidate> pool, int index)
    {
        var item = pool[index];
        pool.RemoveAt(index);
        return item;
    }

    private int DrawIndex(List<Candidate> pool) => Draw(pool, -1);

    private int DrawIndexExcluding(List<Candidate> pool, int excluded) => Draw(pool, excluded);

    private int Draw(List<Candidate> pool, int excluded)
    {
        var total = 0.0;
        for (var i = 0; i < pool.Count; i++)
        {
            if (i != excluded)
            {
                total += Weight(pool[i]);
            }
        }

        var pick = _random.NextDouble() * total;
        var last = -1;
        for (var i = 0; i < pool.Count; i++)
        {
            if (i == excluded)
            {
                continue;
            }

            last = i;
            pick -= Weight(pool[i]);
            if (pick < 0)
            {
                return i;
            }
        }

        return last;
    }

    // A small floor keeps a zero-score endpoint reachable as a last resort.
    private static double Weight(Candidate c) => Math.Max(c.Breakdown!.Total, 0) * Math.Max(c.Endpoint.Weight, 0.0001) + 0.001;

    private static HedgePlan? BuildHedge(List<Candidate> ordered, SelectionRequest request, SelectionProfile profile)
    {
        if (!request.AllowHedge || ordered.Count < 2 || ordered[0].Stats.P95Latency is not { } p95)
        {
            return null;
        }

        return new HedgePlan(ordered[0].Endpoint, ordered[1].Endpoint, p95 * Math.Max(profile.HedgeP95Factor, 0.1));
    }

    private sealed class Candidate(Endpoint endpoint, ExternalSignals signals, EndpointStats stats)
    {
        public Endpoint Endpoint { get; } = endpoint;
        public ExternalSignals Signals { get; } = signals;
        public EndpointStats Stats { get; } = stats;
        public double EffectiveLatencyMs;
        public double? ExpectedCost;
        public (double Latency, double Reliability, double Headroom, double Cost, double Identity, double Affinity, double Quality, double Penalty) Parts;
        public ScoreBreakdown? Breakdown;
        public double Sampled;
    }

    private sealed class SingleGroup(List<Candidate> items) : List<Candidate>(items), IGrouping<int, Candidate>
    {
        public int Key => 0;
    }

    private sealed class NoSignals : IExternalSignalSource
    {
        public static NoSignals Instance { get; } = new();

        public ExternalSignals GetSignals(EndpointKey key) => ExternalSignals.None;
    }
}
