namespace Bazaar.Server.Constellation.Layout;

/// <summary>
/// The values the solar orbit layout reads, as a plain contract (docs/design/19, Git Solar System). Defaults match the
/// server's <c>LayoutOptions</c> defaults for these fields, so an omitted value gives the same layout on both sides.
/// </summary>
public sealed record OrbitLayoutOptions
{
    /// <summary>Seed for the ring phases. Same seed, same graph, same result.</summary>
    public int Seed { get; init; } = 1;

    /// <summary>Distance between neighbouring orbit rings. Ring <c>n</c> sits at radius <c>n * OrbitSpacing</c>. Must be finite and positive.</summary>
    public double OrbitSpacing { get; init; } = 10;

    /// <summary>How a body's orbit radius follows its last-activity age. <see cref="OrbitRadiusMode.Age"/> scales the radius between
    /// <see cref="MinOrbitRadius"/> and <see cref="MaxOrbitRadius"/>; <see cref="OrbitRadiusMode.Equal"/> uses <see cref="MinOrbitRadius"/>.</summary>
    public OrbitRadiusMode OrbitRadiusMode { get; init; } = OrbitRadiusMode.Age;

    /// <summary>Radius of the innermost body orbit (youngest in <c>Age</c> mode, every body in <c>Equal</c> mode). Must be finite and positive.</summary>
    public double MinOrbitRadius { get; init; } = 10;

    /// <summary>Radius of the outermost body orbit (oldest in <c>Age</c> mode). Must be finite and at least <see cref="MinOrbitRadius"/>.</summary>
    public double MaxOrbitRadius { get; init; } = 100;
}
