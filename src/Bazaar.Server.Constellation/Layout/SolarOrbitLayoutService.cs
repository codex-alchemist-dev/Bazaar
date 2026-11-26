using Bazaar.Server.Settings;

namespace Bazaar.Server.Constellation.Layout;

/// <summary>
/// Adapts the plain <see cref="IConstellationOrbitLayout"/> contract to <see cref="SolarOrbitLayout"/>. Callers outside the
/// server depend on the contract only; this class is the one place that maps <see cref="OrbitLayoutOptions"/> onto the
/// server's <see cref="LayoutOptions"/> and calls the deterministic layout. Output is identical to calling
/// <see cref="SolarOrbitLayout.Layout"/> with the mapped options.
/// </summary>
public sealed class SolarOrbitLayoutService : IConstellationOrbitLayout
{
    /// <inheritdoc />
    public float[] Layout(LayoutGraph graph, int coreIndex, OrbitLayoutOptions? options = null, double[]? activityAges = null)
    {
        ArgumentNullException.ThrowIfNull(graph);

        return SolarOrbitLayout.Layout(graph, coreIndex, ToLayoutOptions(options ?? new OrbitLayoutOptions()), activityAges);
    }

    /// <summary>Maps the contract values onto the server options. Every field the solar layout reads is copied; the rest keep their defaults.</summary>
    private static LayoutOptions ToLayoutOptions(OrbitLayoutOptions options) =>
        new()
        {
            Seed = options.Seed,
            OrbitSpacing = options.OrbitSpacing,
            OrbitRadiusMode = ToSettingMode(options.OrbitRadiusMode),
            MinOrbitRadius = options.MinOrbitRadius,
            MaxOrbitRadius = options.MaxOrbitRadius,
        };

    private static ConstellationOrbitRadius ToSettingMode(OrbitRadiusMode mode) => mode switch
    {
        OrbitRadiusMode.Age => ConstellationOrbitRadius.Age,
        OrbitRadiusMode.Equal => ConstellationOrbitRadius.Equal,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown orbit radius mode."),
    };
}
