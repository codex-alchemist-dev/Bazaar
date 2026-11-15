using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Rendering;

/// <summary>
/// Maps Constellation cluster ids to generic node styles. The palette is fixed so the same cluster keeps
/// the same colour across graph versions.
/// </summary>
public static class ConstellationNodeStyles
{
    /// <summary>Cluster colours, cycled by cluster id.</summary>
    public static readonly IReadOnlyList<NodeStyle> Palette =
    [
        NodeStyle.Create(0.36f, 0.62f, 0.95f, 1f, 4f),
        NodeStyle.Create(0.95f, 0.55f, 0.27f, 1f, 4f),
        NodeStyle.Create(0.38f, 0.78f, 0.47f, 1f, 4f),
        NodeStyle.Create(0.86f, 0.36f, 0.56f, 1f, 4f),
        NodeStyle.Create(0.67f, 0.49f, 0.93f, 1f, 4f),
        NodeStyle.Create(0.96f, 0.82f, 0.32f, 1f, 4f),
        NodeStyle.Create(0.34f, 0.80f, 0.82f, 1f, 4f),
        NodeStyle.Create(0.60f, 0.60f, 0.60f, 1f, 4f),
    ];

    /// <summary>Returns the palette style for a cluster id. Negative ids map into the palette range.</summary>
    public static NodeStyle ForCluster(int clusterId)
    {
        int count = Palette.Count;
        return Palette[((clusterId % count) + count) % count];
    }

    /// <summary>
    /// Builds one style per scene node. Nodes get <paramref name="fallback"/> when the scene has no clusters;
    /// otherwise each node gets the style of its cluster.
    /// </summary>
    public static NodeStyleTable Build(ConstellationScene scene, NodeStyle fallback)
    {
        ArgumentNullException.ThrowIfNull(scene);

        var table = new NodeStyleTable(scene.NodeCount, fallback);
        if (scene.ClusterIds.IsEmpty)
        {
            return table;
        }

        ReadOnlySpan<int> clusters = scene.ClusterIds.Span;
        for (int node = 0; node < clusters.Length; node++)
        {
            table.Set(node, ForCluster(clusters[node]));
        }

        return table;
    }
}
