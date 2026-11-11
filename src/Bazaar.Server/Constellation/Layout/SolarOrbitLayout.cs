// Analyzed-source touch: keeps CodeQL Analyze reporting on this head (comment only).
using Bazaar.Server.Settings;

namespace Bazaar.Server.Constellation.Layout;

/// <summary>
/// Deterministic orbit layout for the Git Solar System (docs/design/19, Git Solar System). The core (for example the
/// default branch) sits at the origin. Every other node lies on a concentric ring whose index is its hop depth from
/// the core. Within a ring, nodes are spaced evenly by index order, and each ring starts at a phase drawn from the
/// seed, so the same graph, core and seed always give the same positions. No I/O, no randomness outside the seed.
/// Output is always two-dimensional; <see cref="LayoutOptions.Dimensions"/> is ignored.
/// </summary>
public static class SolarOrbitLayout
{
    /// <summary>
    /// Lays out <paramref name="graph"/> around <paramref name="coreIndex"/>. Returns positions packed as x,y per node
    /// index (the same packing as a layout frame). Nodes unreachable from the core go on one ring beyond the outermost
    /// reachable ring. An empty graph returns an empty array.
    /// </summary>
    /// <remarks>
    /// Without <paramref name="activityAges"/>, each node sits at radius <c>ring * OrbitSpacing</c>, where the ring is its hop depth.
    /// With <paramref name="activityAges"/>, the ring slot (angle) is unchanged, but each non-core node sits at its own radius:
    /// <list type="bullet">
    /// <item><description><c>Age</c> mode: <see cref="BodyOrbitRadius"/> over the ages of the non-core nodes, so the youngest is at
    /// <see cref="LayoutOptions.MinOrbitRadius"/> and the oldest at <see cref="LayoutOptions.MaxOrbitRadius"/>.</description></item>
    /// <item><description><c>Equal</c> mode: every non-core node is at <see cref="LayoutOptions.MinOrbitRadius"/>.</description></item>
    /// </list>
    /// The core stays at the origin in both modes, and its age is ignored. Non-finite non-core ages are rejected by
    /// <see cref="BodyOrbitRadius"/>. The output packing and determinism do not change.
    /// </remarks>
    /// <param name="activityAges">Last-activity age per node index, or null to keep the hop-depth ring radii. When given, its
    /// length must equal <see cref="LayoutGraph.NodeCount"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="activityAges"/> is given and its length is not the node count.</exception>
    public static float[] Layout(LayoutGraph graph, int coreIndex, LayoutOptions? options = null, double[]? activityAges = null)
    {
        ArgumentNullException.ThrowIfNull(graph);

        options ??= new LayoutOptions();
        ValidateOptions(options);

        var nodeCount = graph.NodeCount;
        if (activityAges is not null && activityAges.Length != nodeCount)
        {
            throw new ArgumentException("Activity ages must have one entry per node.", nameof(activityAges));
        }

        if (nodeCount == 0)
        {
            return [];
        }

        ArgumentOutOfRangeException.ThrowIfNegative(coreIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(coreIndex, nodeCount);

        var edges = new (int From, int To)[graph.Edges.Count];
        for (var i = 0; i < edges.Length; i++)
        {
            edges[i] = (graph.Edges[i].Source, graph.Edges[i].Target);
        }

        var depth = ReverseGravity.HopDistances(nodeCount, edges, coreIndex);

        var outermost = 0;
        foreach (var hop in depth)
        {
            outermost = Math.Max(outermost, hop);
        }

        var unreachableRing = outermost + 1;
        var ringOf = new int[nodeCount];
        var ringSize = new int[unreachableRing + 1];
        for (var i = 0; i < nodeCount; i++)
        {
            ringOf[i] = depth[i] == ReverseGravity.Unreachable ? unreachableRing : depth[i];
            ringSize[ringOf[i]]++;
        }

        // Age range over the non-core nodes only. With no non-core nodes the range is never read.
        var minAge = double.PositiveInfinity;
        var maxAge = double.NegativeInfinity;
        if (activityAges is not null)
        {
            for (var i = 0; i < nodeCount; i++)
            {
                if (i != coreIndex)
                {
                    minAge = Math.Min(minAge, activityAges[i]);
                    maxAge = Math.Max(maxAge, activityAges[i]);
                }
            }
        }

        var ringPhase = new double[ringSize.Length];
        for (var ring = 1; ring < ringSize.Length; ring++)
        {
            var rng = new SeededRandom(unchecked(options.Seed + ring));
            ringPhase[ring] = rng.NextDouble() * Math.Tau;
        }

        var positions = new float[nodeCount * 2];
        var ringSeen = new int[ringSize.Length];
        for (var i = 0; i < nodeCount; i++)
        {
            var ring = ringOf[i];
            if (ring == 0)
            {
                // The core is the only node at hop 0, so it stays at the origin.
                continue;
            }

            var slot = ringSeen[ring]++;
            var angle = ringPhase[ring] + Math.Tau * slot / ringSize[ring];
            var radius = activityAges is null
                ? ring * options.OrbitSpacing
                : BodyOrbitRadius(options, activityAges[i], minAge, maxAge);

            positions[i * 2] = (float)(radius * Math.Cos(angle));
            positions[i * 2 + 1] = (float)(radius * Math.Sin(angle));
        }

        return positions;
    }

    /// <summary>
    /// Orbit radius for one branch body from its last-activity age (docs/design/19, Settings). Pure: no graph, no randomness.
    /// In <c>Equal</c> mode the radius is <see cref="LayoutOptions.MinOrbitRadius"/> for any age. In <c>Age</c> mode it is
    /// linear from <see cref="LayoutOptions.MinOrbitRadius"/> at <paramref name="minAge"/> to
    /// <see cref="LayoutOptions.MaxOrbitRadius"/> at <paramref name="maxAge"/>. Ages outside that range are clamped, and
    /// when the two ages are equal the radius is <see cref="LayoutOptions.MinOrbitRadius"/>. Older bodies get outer orbits.
    /// Ages may use any single consistent unit.
    /// </summary>
    /// <remarks>
    /// <see cref="Layout"/> calls this helper for each non-core node when it is given activity ages.
    /// </remarks>
    public static double BodyOrbitRadius(LayoutOptions options, double age, double minAge, double maxAge)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOrbitRadii(options);

        if (!double.IsFinite(age))
        {
            throw new ArgumentOutOfRangeException(nameof(age), age, "Age must be finite.");
        }

        if (!double.IsFinite(minAge) || !double.IsFinite(maxAge) || maxAge < minAge)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAge), maxAge, "Minimum and maximum ages must be finite, with the maximum not below the minimum.");
        }

        if (options.OrbitRadiusMode == ConstellationOrbitRadius.Equal || maxAge == minAge)
        {
            return options.MinOrbitRadius;
        }

        var ratio = Math.Clamp((age - minAge) / (maxAge - minAge), 0, 1);
        return options.MinOrbitRadius + ((options.MaxOrbitRadius - options.MinOrbitRadius) * ratio);
    }

    private static void ValidateOrbitRadii(LayoutOptions options)
    {
        if (!(options.MinOrbitRadius > 0) || !double.IsFinite(options.MinOrbitRadius))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MinOrbitRadius, "Minimum orbit radius must be finite and greater than zero.");
        }

        if (!double.IsFinite(options.MaxOrbitRadius) || options.MaxOrbitRadius < options.MinOrbitRadius)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxOrbitRadius, "Maximum orbit radius must be finite and at least the minimum orbit radius.");
        }
    }

    private static void ValidateOptions(LayoutOptions options)
    {
        if (!(options.OrbitSpacing > 0) || !double.IsFinite(options.OrbitSpacing))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.OrbitSpacing, "Orbit spacing must be finite and greater than zero.");
        }
    }
}
