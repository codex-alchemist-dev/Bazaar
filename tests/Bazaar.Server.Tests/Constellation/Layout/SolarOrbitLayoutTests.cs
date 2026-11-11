using Bazaar.Server.Constellation.Layout;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Tests.Constellation.Layout;

public class SolarOrbitLayoutTests
{
    private const float _tolerance = 1e-4f;

    [Fact]
    public void Same_seed_gives_the_same_positions()
    {
        var graph = Graph(6, (0, 1), (0, 2), (1, 3), (2, 4), (4, 5));
        var options = new LayoutOptions { Seed = 42 };

        var first = SolarOrbitLayout.Layout(graph, 0, options);
        var second = SolarOrbitLayout.Layout(graph, 0, options);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Different_seed_changes_the_ring_phases()
    {
        var graph = Graph(3, (0, 1), (0, 2));

        var first = SolarOrbitLayout.Layout(graph, 0, new LayoutOptions { Seed = 1 });
        var second = SolarOrbitLayout.Layout(graph, 0, new LayoutOptions { Seed = 2 });

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Nodes_sit_at_their_ring_radius_within_tolerance()
    {
        // Star: core 0 with four spokes, all at depth 1.
        var graph = Graph(5, (0, 1), (0, 2), (0, 3), (0, 4));
        var options = new LayoutOptions { OrbitSpacing = 10 };

        var positions = SolarOrbitLayout.Layout(graph, 0, options);

        for (var i = 1; i < 5; i++)
        {
            Assert.InRange(Radius(positions, i), 10 - _tolerance, 10 + _tolerance);
        }
    }

    [Fact]
    public void Deeper_nodes_sit_on_proportionally_larger_rings()
    {
        // Path 0-1-2-3: depth equals index, so radius is index * spacing.
        var graph = Graph(4, (0, 1), (1, 2), (2, 3));
        var options = new LayoutOptions { OrbitSpacing = 5 };

        var positions = SolarOrbitLayout.Layout(graph, 0, options);

        Assert.InRange(Radius(positions, 1), 5 - _tolerance, 5 + _tolerance);
        Assert.InRange(Radius(positions, 2), 10 - _tolerance, 10 + _tolerance);
        Assert.InRange(Radius(positions, 3), 15 - _tolerance, 15 + _tolerance);
    }

    [Fact]
    public void Ring_radius_orders_nodes_by_depth()
    {
        var graph = Graph(6, (0, 1), (1, 2), (0, 3), (3, 4), (4, 5));
        var depth = new[] { 0, 1, 2, 1, 2, 3 };

        var positions = SolarOrbitLayout.Layout(graph, 0);

        for (var a = 0; a < depth.Length; a++)
        {
            for (var b = 0; b < depth.Length; b++)
            {
                if (depth[a] < depth[b])
                {
                    Assert.True(Radius(positions, a) < Radius(positions, b));
                }
            }
        }
    }

    [Fact]
    public void Core_sits_at_the_origin()
    {
        var graph = Graph(3, (0, 1), (1, 2));

        var positions = SolarOrbitLayout.Layout(graph, 1);

        Assert.Equal(0f, positions[2]);
        Assert.Equal(0f, positions[3]);
    }

    [Fact]
    public void Unreachable_nodes_go_one_ring_beyond_the_outermost_ring()
    {
        // Node 3 is in another component.
        var graph = Graph(4, (0, 1), (1, 2));
        var options = new LayoutOptions { OrbitSpacing = 10 };

        var positions = SolarOrbitLayout.Layout(graph, 0, options);

        Assert.InRange(Radius(positions, 3), 30 - _tolerance, 30 + _tolerance);
    }

    [Fact]
    public void No_coordinate_is_NaN_or_infinite()
    {
        var graph = Graph(7, (0, 1), (0, 2), (2, 3), (3, 4), (5, 6));

        var positions = SolarOrbitLayout.Layout(graph, 0, new LayoutOptions { Seed = int.MinValue });

        Assert.Equal(14, positions.Length);
        foreach (var value in positions)
        {
            Assert.True(float.IsFinite(value));
        }
    }

    [Fact]
    public void Empty_graph_gives_no_positions()
    {
        var graph = Graph(0);

        var positions = SolarOrbitLayout.Layout(graph, 0);

        Assert.Empty(positions);
    }

    [Fact]
    public void Single_node_sits_at_the_centre()
    {
        var graph = Graph(1);

        var positions = SolarOrbitLayout.Layout(graph, 0);

        Assert.Equal([0f, 0f], positions);
    }

    [Fact]
    public void Core_index_outside_the_graph_is_rejected()
    {
        var graph = Graph(2, (0, 1));

        Assert.Throws<ArgumentOutOfRangeException>(() => SolarOrbitLayout.Layout(graph, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => SolarOrbitLayout.Layout(graph, -1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Non_positive_or_non_finite_spacing_is_rejected(double spacing)
    {
        var graph = Graph(2, (0, 1));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SolarOrbitLayout.Layout(graph, 0, new LayoutOptions { OrbitSpacing = spacing }));
    }

    [Fact]
    public void Body_radius_is_min_at_the_youngest_age_and_max_at_the_oldest()
    {
        var options = new LayoutOptions { OrbitRadiusMode = ConstellationOrbitRadius.Age, MinOrbitRadius = 10, MaxOrbitRadius = 100 };

        Assert.Equal(10, SolarOrbitLayout.BodyOrbitRadius(options, age: 5, minAge: 5, maxAge: 25));
        Assert.Equal(100, SolarOrbitLayout.BodyOrbitRadius(options, age: 25, minAge: 5, maxAge: 25));
    }

    [Fact]
    public void Body_radius_at_the_midpoint_age_is_the_midpoint_radius()
    {
        var options = new LayoutOptions { OrbitRadiusMode = ConstellationOrbitRadius.Age, MinOrbitRadius = 10, MaxOrbitRadius = 100 };

        Assert.Equal(55, SolarOrbitLayout.BodyOrbitRadius(options, age: 15, minAge: 5, maxAge: 25), 9);
    }

    [Fact]
    public void Body_radius_is_linear_in_age()
    {
        var options = new LayoutOptions { OrbitRadiusMode = ConstellationOrbitRadius.Age, MinOrbitRadius = 10, MaxOrbitRadius = 100 };

        // A quarter of the way through the age range gives a quarter of the radius range.
        Assert.Equal(32.5, SolarOrbitLayout.BodyOrbitRadius(options, age: 10, minAge: 0, maxAge: 40), 9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(40)]
    public void Body_radius_in_equal_mode_is_min_for_any_age(double age)
    {
        var options = new LayoutOptions { OrbitRadiusMode = ConstellationOrbitRadius.Equal, MinOrbitRadius = 10, MaxOrbitRadius = 100 };

        Assert.Equal(10, SolarOrbitLayout.BodyOrbitRadius(options, age, minAge: 0, maxAge: 40));
    }

    [Fact]
    public void Body_radius_is_min_when_all_ages_are_equal()
    {
        var options = new LayoutOptions { OrbitRadiusMode = ConstellationOrbitRadius.Age, MinOrbitRadius = 10, MaxOrbitRadius = 100 };

        Assert.Equal(10, SolarOrbitLayout.BodyOrbitRadius(options, age: 7, minAge: 7, maxAge: 7));
    }

    [Theory]
    [InlineData(-5, 10)]
    [InlineData(50, 100)]
    public void Body_radius_clamps_ages_outside_the_range(double age, double expected)
    {
        var options = new LayoutOptions { OrbitRadiusMode = ConstellationOrbitRadius.Age, MinOrbitRadius = 10, MaxOrbitRadius = 100 };

        Assert.Equal(expected, SolarOrbitLayout.BodyOrbitRadius(options, age, minAge: 0, maxAge: 40));
    }

    [Fact]
    public void Default_options_use_age_mode_with_radius_range_ten_to_one_hundred()
    {
        var options = new LayoutOptions();

        Assert.Equal(ConstellationOrbitRadius.Age, options.OrbitRadiusMode);
        Assert.Equal(10, options.MinOrbitRadius);
        Assert.Equal(100, options.MaxOrbitRadius);
        Assert.Equal(10, options.OrbitSpacing);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void Body_radius_rejects_a_non_positive_minimum(double min)
    {
        var options = new LayoutOptions { MinOrbitRadius = min };

        Assert.Throws<ArgumentOutOfRangeException>(() => SolarOrbitLayout.BodyOrbitRadius(options, 1, 0, 2));
    }

    [Fact]
    public void Body_radius_rejects_a_maximum_below_the_minimum()
    {
        var options = new LayoutOptions { MinOrbitRadius = 50, MaxOrbitRadius = 20 };

        Assert.Throws<ArgumentOutOfRangeException>(() => SolarOrbitLayout.BodyOrbitRadius(options, 1, 0, 2));
    }

    [Fact]
    public void Body_radius_rejects_non_finite_ages()
    {
        var options = new LayoutOptions();

        Assert.Throws<ArgumentOutOfRangeException>(() => SolarOrbitLayout.BodyOrbitRadius(options, double.NaN, 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => SolarOrbitLayout.BodyOrbitRadius(options, 1, 0, double.PositiveInfinity));
    }

    [Fact]
    public void Body_radius_rejects_a_maximum_age_below_the_minimum_age()
    {
        var options = new LayoutOptions();

        Assert.Throws<ArgumentOutOfRangeException>(() => SolarOrbitLayout.BodyOrbitRadius(options, 1, minAge: 5, maxAge: 2));
    }

    private static LayoutGraph Graph(int nodeCount, params (int From, int To)[] edges)
    {
        var ids = Enumerable.Range(0, nodeCount).Select(i => $"n{i}").ToArray();
        var layoutEdges = edges.Select(e => new LayoutEdge(e.From, e.To)).ToArray();
        return new LayoutGraph(ids, layoutEdges);
    }

    private static double Radius(float[] positions, int node)
    {
        var x = positions[node * 2];
        var y = positions[node * 2 + 1];
        return Math.Sqrt((x * x) + (y * y));
    }
}
