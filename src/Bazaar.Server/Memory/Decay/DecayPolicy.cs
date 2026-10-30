using Bazaar.Server.Memory.Trust;

namespace Bazaar.Server.Memory.Decay;

/// <summary>
/// Pure retrieval-weight decay (docs/design/08-memory-system.md, section 8). The weight falls exponentially with time
/// since last use: weight = 0.5^(elapsed / half-life). It never deletes anything, and it never leaves [0, 1].
/// Floors: failure notes never go below <see cref="FailureFloor"/>, and T3 (<see cref="MemoryTier.OwnerPromoted"/>)
/// notes never go below <see cref="OwnerPromotedFloor"/>. These floors are fixed, not settings.
/// </summary>
public sealed class DecayPolicy
{
    /// <summary>Lowest weight a failure note can reach.</summary>
    public const double FailureFloor = 0.2;

    /// <summary>Lowest weight a T3 owner-promoted note can reach.</summary>
    public const double OwnerPromotedFloor = 0.3;

    /// <summary>Default half-life (docs/design/08-memory-system.md, Settings: memory.decay.half_life_days = 90).</summary>
    public static readonly TimeSpan DefaultHalfLife = TimeSpan.FromDays(90);

    private readonly TimeSpan _defaultHalfLife;
    private readonly IReadOnlyDictionary<DecayKind, TimeSpan> _halfLifeByKind;

    /// <summary>Creates a policy with one half-life for every kind, optionally overridden per kind.</summary>
    /// <param name="defaultHalfLife">Half-life for kinds without an override. Null means <see cref="DefaultHalfLife"/>.</param>
    /// <param name="halfLifeByKind">Per-kind overrides. Null means none.</param>
    /// <exception cref="ArgumentOutOfRangeException">A half-life is zero or negative.</exception>
    public DecayPolicy(TimeSpan? defaultHalfLife = null, IReadOnlyDictionary<DecayKind, TimeSpan>? halfLifeByKind = null)
    {
        _defaultHalfLife = defaultHalfLife ?? DefaultHalfLife;
        RequirePositive(_defaultHalfLife, nameof(defaultHalfLife));

        var overrides = halfLifeByKind ?? new Dictionary<DecayKind, TimeSpan>();
        foreach (var (kind, halfLife) in overrides)
        {
            RequirePositive(halfLife, $"halfLifeByKind[{kind}]");
        }

        _halfLifeByKind = overrides;
    }

    /// <summary>
    /// Retrieval weight in [0, 1] for a note of this kind and tier, given the time since it was last used.
    /// Zero or negative elapsed time gives 1.
    /// </summary>
    public double Weight(DecayKind kind, MemoryTier tier, TimeSpan sinceLastUse)
    {
        if (sinceLastUse <= TimeSpan.Zero)
        {
            return 1.0;
        }

        var halfLife = _halfLifeByKind.TryGetValue(kind, out var overrideHalfLife) ? overrideHalfLife : _defaultHalfLife;
        var halfLives = sinceLastUse.TotalSeconds / halfLife.TotalSeconds;
        var decayed = Math.Pow(0.5, halfLives);

        return Math.Clamp(Math.Max(decayed, FloorFor(kind, tier)), 0.0, 1.0);
    }

    private static double FloorFor(DecayKind kind, MemoryTier tier)
    {
        var floor = 0.0;
        if (kind == DecayKind.Failure)
        {
            floor = Math.Max(floor, FailureFloor);
        }

        if (tier == MemoryTier.OwnerPromoted)
        {
            floor = Math.Max(floor, OwnerPromotedFloor);
        }

        return floor;
    }

    private static void RequirePositive(TimeSpan halfLife, string paramName)
    {
        if (halfLife <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(paramName, halfLife, "Half-life must be positive.");
        }
    }
}
