using Bazaar.Server.Constellation.Clustering;

namespace Bazaar.Server.Tests.Constellation.Clustering;

public class LouvainClusteringTests
{
    /// <summary>Nodes "{prefix}00".."{prefix}{size-1}" joined pairwise, each with weight 1.</summary>
    private static (List<string> Nodes, List<WeightedEdge> Edges) Clique(string prefix, int size)
    {
        var nodes = Enumerable.Range(0, size).Select(i => $"{prefix}{i:D2}").ToList();
        var edges = new List<WeightedEdge>();
        for (var i = 0; i < size; i++)
        {
            for (var j = i + 1; j < size; j++)
            {
                edges.Add(new WeightedEdge(nodes[i], nodes[j], 1.0));
            }
        }

        return (nodes, edges);
    }

    private static (List<string> Nodes, List<WeightedEdge> Edges) TwoCliques()
    {
        var a = Clique("a", 5);
        var b = Clique("b", 5);
        return (a.Nodes.Concat(b.Nodes).ToList(), a.Edges.Concat(b.Edges).ToList());
    }

    [Fact]
    public void Two_disjoint_cliques_split_into_two_communities()
    {
        var (nodes, edges) = TwoCliques();

        var result = LouvainClustering.Detect(nodes, edges);

        var a = result.CommunityByNode["a00"];
        var b = result.CommunityByNode["b00"];
        Assert.NotEqual(a, b);
        Assert.All(Enumerable.Range(0, 5), i => Assert.Equal(a, result.CommunityByNode[$"a{i:D2}"]));
        Assert.All(Enumerable.Range(0, 5), i => Assert.Equal(b, result.CommunityByNode[$"b{i:D2}"]));
    }

    [Fact]
    public void Two_cliques_joined_by_one_edge_yield_two_communities()
    {
        var (nodes, edges) = TwoCliques();
        edges.Add(new WeightedEdge("a00", "b00", 1.0));

        var result = LouvainClustering.Detect(nodes, edges);

        Assert.Equal(2, result.CommunityByNode.Values.Distinct().Count());
        Assert.NotEqual(result.CommunityByNode["a00"], result.CommunityByNode["b00"]);
    }

    [Fact]
    public void Single_clique_stays_one_community()
    {
        var (nodes, edges) = Clique("k", 6);

        var result = LouvainClustering.Detect(nodes, edges);

        Assert.Single(result.CommunityByNode.Values.Distinct());
    }

    [Fact]
    public void Single_community_modularity_is_zero()
    {
        var (nodes, edges) = Clique("k", 6);

        var result = LouvainClustering.Detect(nodes, edges);

        Assert.Equal(0.0, result.Modularity, 10);
    }

    [Fact]
    public void Empty_graph_returns_empty_map()
    {
        var result = LouvainClustering.Detect([], []);

        Assert.Empty(result.CommunityByNode);
        Assert.Equal(0.0, result.Modularity);
    }

    [Fact]
    public void Nodes_without_edges_each_get_their_own_community()
    {
        var result = LouvainClustering.Detect(["x", "y", "z"], []);

        Assert.Equal(3, result.CommunityByNode.Count);
        Assert.Equal(3, result.CommunityByNode.Values.Distinct().Count());
    }

    [Fact]
    public void Self_loops_and_zero_weight_edges_do_not_throw()
    {
        var edges = new[]
        {
            new WeightedEdge("a", "a", 2.0),
            new WeightedEdge("a", "b", 0.0),
            new WeightedEdge("b", "c", 1.0),
            new WeightedEdge("c", "c", 0.0),
        };

        var result = LouvainClustering.Detect(["a", "b", "c"], edges);

        Assert.Equal(3, result.CommunityByNode.Count);
        Assert.True(double.IsFinite(result.Modularity));
    }

    [Fact]
    public void Running_twice_on_the_same_input_gives_the_same_map()
    {
        var (nodes, edges) = TwoCliques();
        edges.Add(new WeightedEdge("a03", "b02", 0.5));

        var first = LouvainClustering.Detect(nodes, edges);
        var second = LouvainClustering.Detect(nodes, edges);

        Assert.Equal(first.CommunityByNode, second.CommunityByNode);
        Assert.Equal(first.Modularity, second.Modularity);
    }

    [Fact]
    public void Edge_order_does_not_change_the_map()
    {
        var (nodes, edges) = TwoCliques();
        edges.Add(new WeightedEdge("a00", "b00", 1.0));

        var forward = LouvainClustering.Detect(nodes, edges);
        var reversed = LouvainClustering.Detect(nodes.AsEnumerable().Reverse(), edges.AsEnumerable().Reverse());

        Assert.Equal(forward.CommunityByNode, reversed.CommunityByNode);
    }

    [Fact]
    public void Modularity_of_the_two_clique_result_beats_the_single_community_result()
    {
        var (nodes, edges) = TwoCliques();
        var split = LouvainClustering.Detect(nodes, edges);
        var single = nodes.ToDictionary(n => n, _ => 0, StringComparer.Ordinal);

        var singleScore = LouvainClustering.Modularity(nodes, edges, single);

        Assert.True(split.Modularity > singleScore, $"split {split.Modularity} should beat single {singleScore}");
        Assert.Equal(split.Modularity, LouvainClustering.Modularity(nodes, edges, split.CommunityByNode), 10);
    }

    [Fact]
    public void Sparse_graph_of_1000_nodes_finishes_and_maps_every_node()
    {
        const int count = 1000;
        var nodes = Enumerable.Range(0, count).Select(i => $"n{i:D4}").ToList();
        var edges = new List<WeightedEdge>();
        for (var i = 0; i < count; i++)
        {
            // A ring plus a chord every 7 steps: sparse, with local structure.
            edges.Add(new WeightedEdge(nodes[i], nodes[(i + 1) % count], 1.0));
            edges.Add(new WeightedEdge(nodes[i], nodes[(i + 7) % count], 1.0));
        }

        var result = LouvainClustering.Detect(nodes, edges);

        Assert.Equal(count, result.CommunityByNode.Count);
        Assert.InRange(result.CommunityByNode.Values.Distinct().Count(), 1, count - 1);
    }

    [Fact]
    public void Edge_to_an_unknown_node_throws_with_a_clear_message()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            LouvainClustering.Detect(["a", "b"], [new WeightedEdge("a", "ghost", 1.0)]));

        Assert.Contains("ghost", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not in the node set", ex.Message, StringComparison.Ordinal);
    }
}
