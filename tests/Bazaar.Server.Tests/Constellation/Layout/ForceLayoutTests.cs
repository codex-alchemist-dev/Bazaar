using Bazaar.Server.Constellation.Layout;

namespace Bazaar.Server.Tests.Constellation.Layout;

public class ForceLayoutTests
{
    private static LayoutGraph Graph(int nodes, IEnumerable<(int A, int B)> edges)
    {
        var ids = Enumerable.Range(0, nodes).Select(i => $"n{i}").ToArray();
        return new LayoutGraph(ids, edges.Select(e => new LayoutEdge(e.A, e.B)).ToArray());
    }

    private static LayoutGraph RandomGraph(int nodes, int edges, int seed)
    {
        var random = new Random(seed);
        var pairs = new List<(int, int)>(edges);
        for (var k = 0; k < edges; k++)
        {
            pairs.Add((random.Next(nodes), random.Next(nodes)));
        }

        return Graph(nodes, pairs);
    }

    private static double Distance(ForceLayout layout, int a, int b)
    {
        return System.Numerics.Vector3.Distance(layout.GetPosition(a), layout.GetPosition(b));
    }

    [Fact]
    public void Same_seed_and_graph_give_identical_positions()
    {
        var graph = RandomGraph(60, 90, seed: 3);
        var first = new ForceLayout(graph, new LayoutOptions { Seed = 42 });
        var second = new ForceLayout(graph, new LayoutOptions { Seed = 42 });

        first.Step(200);
        second.Step(200);

        Assert.Equal(first.GetPositions(), second.GetPositions());
    }

    [Fact]
    public void Different_seed_gives_different_start_positions()
    {
        var graph = Graph(5, []);

        var a = new ForceLayout(graph, new LayoutOptions { Seed = 1 }).GetPositions();
        var b = new ForceLayout(graph, new LayoutOptions { Seed = 2 }).GetPositions();

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Two_unconnected_nodes_separate_to_at_least_the_target_distance()
    {
        const double targetDistance = 1.0;
        var layout = new ForceLayout(Graph(2, []));

        layout.Step(500);

        Assert.True(Distance(layout, 0, 1) >= targetDistance, $"Separation {Distance(layout, 0, 1)} is below {targetDistance}.");
    }

    [Fact]
    public void Empty_graph_does_not_throw()
    {
        var layout = new ForceLayout(Graph(0, []));

        layout.Step(10);

        Assert.Empty(layout.GetPositions());
        Assert.Equal(0, layout.LastMaxDisplacement);
    }

    [Fact]
    public void Single_node_does_not_throw_and_stays_finite()
    {
        var layout = new ForceLayout(Graph(1, []));

        layout.Step(100);

        Assert.All(layout.GetPositions(), value => Assert.True(float.IsFinite(value)));
    }

    [Fact]
    public void Two_dimensional_layout_keeps_z_at_zero()
    {
        var layout = new ForceLayout(RandomGraph(30, 40, seed: 5), new LayoutOptions { Dimensions = 2 });

        layout.Step(300);

        var positions = layout.GetPositions();
        for (var i = 2; i < positions.Length; i += 3)
        {
            Assert.Equal(0f, positions[i]);
        }
    }

    [Fact]
    public void Three_dimensional_layout_uses_z()
    {
        var layout = new ForceLayout(RandomGraph(30, 40, seed: 5), new LayoutOptions { Dimensions = 3, Seed = 9 });

        layout.Step(300);

        var positions = layout.GetPositions();
        var zValues = Enumerable.Range(0, positions.Length / 3).Select(i => positions[(3 * i) + 2]);
        Assert.Contains(zValues, z => z != 0f);
    }

    [Fact]
    public void Warm_start_settles_in_fewer_ticks_than_cold_start()
    {
        // Adaptive-speed ForceAtlas2 keeps small bumps in the largest per-tick move, so "settled" uses a practical tolerance.
        const double settleTolerance = 1e-2;
        var graph = RandomGraph(30, 45, seed: 8);
        var cold = new ForceLayout(graph, new LayoutOptions { Seed = 4 });
        var coldTicks = cold.RunUntilSettled(settleTolerance, 10_000);

        var warm = new ForceLayout(graph, new LayoutOptions { Seed = 4 }, cold.GetPositions());
        var warmTicks = warm.RunUntilSettled(settleTolerance, 10_000);

        Assert.True(warmTicks < coldTicks, $"Warm start took {warmTicks} ticks; cold start took {coldTicks}.");
    }

    [Fact]
    public void Warm_start_with_wrong_length_is_rejected()
    {
        var graph = Graph(3, []);

        Assert.Throws<ArgumentException>(() => new ForceLayout(graph, null, new float[4]));
    }

    [Fact]
    public void Warm_start_with_nan_entries_uses_seeded_start_for_them()
    {
        var graph = Graph(2, []);
        var warm = new float[] { float.NaN, float.NaN, 0, 1, 2, 0 };

        var layout = new ForceLayout(graph, new LayoutOptions { Seed = 6 }, warm);

        var positions = layout.GetPositions();
        Assert.All(positions, value => Assert.True(float.IsFinite(value)));
        Assert.Equal(warm[3], positions[3]);
    }

    [Fact]
    public void No_non_finite_positions_after_1000_ticks_on_500_nodes()
    {
        var layout = new ForceLayout(RandomGraph(500, 1500, seed: 21));

        layout.Step(1000);

        Assert.All(layout.GetPositions(), value => Assert.True(float.IsFinite(value)));
        Assert.True(double.IsFinite(layout.LastMaxDisplacement));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void Invalid_dimensions_are_rejected(int dimensions)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ForceLayout(Graph(2, []), new LayoutOptions { Dimensions = dimensions }));
    }
}
