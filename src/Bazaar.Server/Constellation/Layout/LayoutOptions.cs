// Required CodeQL Analyze check runs on this head.
using Bazaar.Server.Settings;

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

    /// <summary>
    /// Distance between neighbouring orbit rings in <see cref="SolarOrbitLayout"/>. Ring <c>n</c> sits at radius
    /// <c>n * OrbitSpacing</c>. Must be finite and positive.
    /// </summary>
    public double OrbitSpacing { get; init; } = 10;

    /// <summary>
    /// How a branch's orbit radius follows its last-activity age (<c>constellation.orbit_radius</c>). <c>Age</c> scales the
    /// radius between <see cref="MinOrbitRadius"/> and <see cref="MaxOrbitRadius"/>; <c>Equal</c> uses <see cref="MinOrbitRadius"/>.
    /// Applied by the body radius helper in <see cref="SolarOrbitLayout"/>.
    /// </summary>
    public ConstellationOrbitRadius OrbitRadiusMode { get; init; } = ConstellationOrbitRadius.Age;

    /// <summary>
    /// Radius of the innermost body orbit (youngest activity in <c>Age</c> mode, every body in <c>Equal</c> mode).
    /// Safe default 10, which matches <see cref="OrbitSpacing"/>. An operator may change it. Must be finite and positive.
    /// </summary>
    public double MinOrbitRadius { get; init; } = 10;

    /// <summary>
    /// Radius of the outermost body orbit (oldest activity in <c>Age</c> mode). Safe default 100; an operator may change it.
    /// Must be finite and at least <see cref="MinOrbitRadius"/>.
    /// </summary>
    public double MaxOrbitRadius { get; init; } = 100;
}
