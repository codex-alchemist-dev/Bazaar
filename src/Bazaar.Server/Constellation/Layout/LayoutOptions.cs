namespace Bazaar.Server.Constellation.Layout;

/// <summary>
/// Tuning for <see cref="ForceLayout"/>. Defaults follow the usual ForceAtlas2 values.
/// The seed fixes the initial positions, so the same graph and options always give the same layout.
/// </summary>
public sealed record LayoutOptions
{
    /// <summary>Seed for the initial random positions. Same seed, same graph, same result.</summary>
    public int Seed { get; init; } = 1;

    /// <summary>2 keeps every node at z = 0; 3 lays nodes out in space (doc 19 3D mode).</summary>
    public int Dimensions { get; init; } = 2;

    /// <summary>Repulsion strength (ForceAtlas2 scaling ratio). Must be positive.</summary>
    public double ScalingRatio { get; init; } = 10;

    /// <summary>Pull toward the origin, per unit of node mass. Must be positive.</summary>
    public double Gravity { get; init; } = 1;

    /// <summary>Spring strength along each edge. Must be positive.</summary>
    public double EdgeStrength { get; init; } = 1;

    /// <summary>Barnes-Hut opening angle. 0 is exact (same as brute force); larger is faster and less exact. Must not be negative.</summary>
    public double Theta { get; init; } = 0.8;

    /// <summary>Adaptive speed tolerance (ForceAtlas2 jitter tolerance). Must be positive.</summary>
    public double JitterTolerance { get; init; } = 1;

    /// <summary>Largest distance one node may move in one tick. Must be positive.</summary>
    public double MaxDisplacement { get; init; } = 10;

    /// <summary>Half-width of the cube that random initial positions fall in, per sqrt(node count). Must be positive.</summary>
    public double InitialSpread { get; init; } = 10;
}
