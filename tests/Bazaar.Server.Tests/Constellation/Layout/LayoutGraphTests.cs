using Bazaar.Contracts.Graph;
using Bazaar.Server.Constellation.Layout;

namespace Bazaar.Server.Tests.Constellation.Layout;

public class LayoutGraphTests
{
    [Fact]
    public void From_snapshot_maps_ids_to_indices_and_drops_unknown_endpoints()
    {
        var snapshot = new GraphSnapshot(
            GraphVersion: 3,
            Nodes: [new GraphNode("a"), new GraphNode("b")],
            Edges: [new GraphEdge("a", "b"), new GraphEdge("b", "missing")]);

        var graph = LayoutGraph.FromSnapshot(snapshot);

        Assert.Equal(["a", "b"], graph.NodeIds);
        Assert.Equal([new LayoutEdge(0, 1)], graph.Edges);
    }

    [Fact]
    public void Duplicate_node_ids_are_rejected()
    {
        var snapshot = new GraphSnapshot(1, [new GraphNode("a"), new GraphNode("a")], []);

        Assert.Throws<ArgumentException>(() => LayoutGraph.FromSnapshot(snapshot));
    }

    [Fact]
    public void Edge_index_outside_the_node_range_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LayoutGraph(["a"], [new LayoutEdge(0, 1)]));
    }
}
