namespace Bazaar.Server.Gateway.Selection;

/// <summary>How scores turn into an order.</summary>
public enum SelectionStrategy
{
    /// <summary>Highest score first.</summary>
    Best,

    /// <summary>Draw two by weight, keep the better, repeat. Spreads load without a herd.</summary>
    PowerOfTwo,

    /// <summary>Sample each endpoint's success from its Beta posterior, so exploration shrinks as evidence grows.</summary>
    Thompson,

    /// <summary>Draw by score as weight.</summary>
    WeightedRandom,
}

/// <summary>Relative importance of each factor. Weights are normalised, so only their ratios matter.</summary>
public sealed record ScoreWeights
{
    public double Latency { get; init; } = 1;

    public double Reliability { get; init; } = 1;

    public double Headroom { get; init; } = 0.5;

    public double Cost { get; init; } = 0.5;

    public double Identity { get; init; } = 0.25;

    public double Affinity { get; init; } = 0.5;

    public double Quality { get; init; } = 0.5;
}

/// <summary>A named set of weights and a strategy. Operators may add more.</summary>
public sealed record SelectionProfile(string Name, ScoreWeights Weights, SelectionStrategy Strategy)
{
    /// <summary>Chance, 0 to 1, that the first and second choice swap, so a recovered endpoint is noticed.</summary>
    public double ExploreFraction { get; init; } = 0.02;

    /// <summary>When false (default) the best non-empty tier wins and lower tiers only back it up. When true tiers cost score instead.</summary>
    public bool SoftTiers { get; init; }

    /// <summary>Score taken off per tier step when <see cref="SoftTiers"/> is on, 0 to 1.</summary>
    public double TierPenalty { get; init; } = 0.15;

    /// <summary>A hedge starts the second endpoint after this multiple of the first one's p95 latency.</summary>
    public double HedgeP95Factor { get; init; } = 1.0;

    /// <summary>Maps to <c>native</c> before <c>pooled</c> (doc 05 <c>gateway.claude.order</c>); false reverses it.</summary>
    public bool NativeFirst { get; init; } = true;
}

/// <summary>The shipped profiles.</summary>
public static class SelectionProfiles
{
    public const string Balanced = "balanced";
    public const string Latency = "latency";
    public const string Reliability = "reliability";
    public const string Cost = "cost";
    public const string Quality = "quality";

    public static SelectionProfile BalancedProfile { get; } = new(Balanced, new ScoreWeights(), SelectionStrategy.PowerOfTwo);

    public static IReadOnlyList<SelectionProfile> Defaults { get; } =
    [
        BalancedProfile,
        new(Latency, new ScoreWeights { Latency = 3, Reliability = 1, Headroom = 0.5, Cost = 0.1, Identity = 0.1, Affinity = 0.5, Quality = 0.2 }, SelectionStrategy.Best),
        new(Reliability, new ScoreWeights { Latency = 0.4, Reliability = 3, Headroom = 1, Cost = 0.1, Identity = 0.25, Affinity = 0.3, Quality = 0.3 }, SelectionStrategy.Thompson),
        new(Cost, new ScoreWeights { Latency = 0.3, Reliability = 1, Headroom = 0.5, Cost = 3, Identity = 0.25, Affinity = 0.5, Quality = 0.3 }, SelectionStrategy.PowerOfTwo),
        new(Quality, new ScoreWeights { Latency = 0.3, Reliability = 1, Headroom = 0.3, Cost = 0.1, Identity = 0.25, Affinity = 0.5, Quality = 3 }, SelectionStrategy.Best),
    ];
}
