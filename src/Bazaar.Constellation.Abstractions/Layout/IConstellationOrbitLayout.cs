namespace Bazaar.Server.Constellation.Layout;

/// <summary>
/// Deterministic orbit layout for the Git Solar System (docs/design/19). Graph in, packed positions out: x,y per node index,
/// the same packing as a layout frame. Implementations must be deterministic for the same inputs.
/// </summary>
public interface IConstellationOrbitLayout
{
    /// <summary>
    /// Lays out <paramref name="graph"/> around <paramref name="coreIndex"/>, which sits at the origin. Nodes unreachable from the
    /// core go on one ring beyond the outermost reachable ring. An empty graph returns an empty array.
    /// </summary>
    /// <param name="graph">The graph to lay out. Node order is the packed position order.</param>
    /// <param name="coreIndex">Index of the core node.</param>
    /// <param name="options">Layout values, or null for the defaults.</param>
    /// <param name="activityAges">Last-activity age per node index, or null to keep the hop-depth ring radii. When given, its
    /// length must equal the node count. The core's age is ignored.</param>
    /// <returns>Positions packed as x,y per node index.</returns>
    /// <exception cref="ArgumentException"><paramref name="activityAges"/> is given and its length is not the node count.</exception>
    float[] Layout(LayoutGraph graph, int coreIndex, OrbitLayoutOptions? options = null, double[]? activityAges = null);
}
