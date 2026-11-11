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
    public static float[] Layout(LayoutGraph graph, int coreIndex, LayoutOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(graph);

        options ??= new LayoutOptions();
        ValidateOptions(options);

        var nodeCount = graph.NodeCount;
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
            var radius = ring * options.OrbitSpacing;

            positions[i * 2] = (float)(radius * Math.Cos(angle));
            positions[i * 2 + 1] = (float)(radius * Math.Sin(angle));
        }

        return positions;
    }

    private static void ValidateOptions(LayoutOptions options)
    {
        if (!(options.OrbitSpacing > 0) || !double.IsFinite(options.OrbitSpacing))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.OrbitSpacing, "Orbit spacing must be finite and greater than zero.");
        }
    }
}
