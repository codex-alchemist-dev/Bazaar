using Bazaar.Server.Constellation.Layout;

namespace Bazaar.Server.Tests.Constellation.Layout;

public class ReverseGravityTests
{
    [Fact]
    public void Path_graph_gives_hop_distances_from_the_focus()
    {
        var edges = new[] { (0, 1), (1, 2), (2, 3) };

        Assert.Equal([0, 1, 2, 3], ReverseGravity.HopDistances(4, edges, 0));
        Assert.Equal([1, 0, 1, 2], ReverseGravity.HopDistances(4, edges, 1));
    }

    [Fact]
    public void Edges_are_undirected()
    {
        var edges = new[] { (1, 0), (2, 1) };

        Assert.Equal([2, 1, 0], ReverseGravity.HopDistances(3, edges, 2));
    }

    [Fact]
    public void Disconnected_node_gets_minus_one()
    {
        var edges = new[] { (0, 1), (1, 2) };

        Assert.Equal([0, 1, 2, -1], ReverseGravity.HopDistances(4, edges, 0));
    }

    [Fact]
    public void Focus_is_at_radius_zero()
    {
        var hops = ReverseGravity.HopDistances(3, new[] { (0, 1), (1, 2) }, 1);
        var radii = ReverseGravity.RingTargetRadii(hops, 3, 10f);

        Assert.Equal(0f, radii[1]);
    }

    [Fact]
    public void Ring_radii_increase_with_hop_until_the_ring_depth()
    {
        int[] hops = [0, 1, 2, 3, 4, 5, 6];

        var radii = ReverseGravity.RingTargetRadii(hops, 3, 10f);

        Assert.Equal([0f, 10f, 20f, 30f, 30f, 30f, 30f], radii);
        for (var i = 1; i < 4; i++)
        {
            Assert.True(radii[i] > radii[i - 1]);
        }
    }

    [Fact]
    public void Unreachable_nodes_go_one_ring_beyond_the_outermost_ring()
    {
        int[] hops = [0, 1, -1];

        var radii = ReverseGravity.RingTargetRadii(hops, 2, 5f);

        Assert.Equal([0f, 5f, 15f], radii);
        Assert.Equal(15f, ReverseGravity.RingRadius(ReverseGravity.Unreachable, 2, 5f));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void Ring_depth_outside_one_to_six_is_rejected(int ringDepth)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReverseGravity.RingTargetRadii([0, 1], ringDepth, 10f));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReverseGravity.RingRadius(1, ringDepth, 10f));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void Ring_depth_bounds_are_accepted(int ringDepth)
    {
        var radii = ReverseGravity.RingTargetRadii([0, 1, 2, 3, 4, 5, 6], ringDepth, 1f);

        Assert.All(radii, r => Assert.True(float.IsFinite(r)));
    }

    [Fact]
    public void Invalid_hop_node_index_and_radius_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReverseGravity.RingTargetRadii([0, -2], 3, 10f));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReverseGravity.RingTargetRadii([0], 3, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReverseGravity.RingTargetRadii([0], 3, float.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReverseGravity.HopDistances(3, [(0, 3)], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReverseGravity.HopDistances(3, [(0, 1)], 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReverseGravity.HopDistances(-1, [], 0));
    }

    [Fact]
    public void No_output_is_nan_or_infinite()
    {
        var hops = ReverseGravity.HopDistances(6, [(0, 1), (1, 2), (3, 4)], 0);
        var radii = ReverseGravity.RingTargetRadii(hops, 6, 1000f);

        Assert.All(radii, r => Assert.True(float.IsFinite(r)));

        var positions = new float[12];
        Array.Fill(positions, float.NaN);
        positions[0] = 1f;
        positions[1] = 2f;
        ReverseGravity.WarmStart(positions, [(0, 1), (1, 2), (3, 4)], 0);

        Assert.All(positions, p => Assert.True(float.IsFinite(p)));
    }

    [Fact]
    public void Warm_start_keeps_placed_positions_and_seeds_a_new_node_at_its_neighbours_mean()
    {
        // Node 0 at (0,0), node 1 at (10,4), node 2 unplaced and joined to both.
        var positions = new float[] { 0f, 0f, 10f, 4f, float.NaN, float.NaN };

        ReverseGravity.WarmStart(positions, [(0, 2), (1, 2)], 0);

        Assert.Equal([0f, 0f, 10f, 4f, 5f, 2f], positions);
    }

    [Fact]
    public void Warm_start_places_a_node_with_no_placed_neighbour_at_the_focus()
    {
        // Focus 0 placed at (3,-2). Node 2 is only connected to unplaced node 1, so it takes the focus position.
        var positions = new float[] { 3f, -2f, float.NaN, float.NaN, float.NaN, float.NaN };

        ReverseGravity.WarmStart(positions, [(1, 2)], 0);

        Assert.Equal([3f, -2f, 3f, -2f, 3f, -2f], positions);
    }

    [Fact]
    public void Warm_start_seeds_an_unplaced_focus_at_the_origin()
    {
        var positions = new float[] { float.NaN, float.NaN, 4f, 6f };

        ReverseGravity.WarmStart(positions, [(0, 1)], 0);

        Assert.Equal([0f, 0f, 4f, 6f], positions);
    }

    [Fact]
    public void Warm_start_supports_three_dimensions()
    {
        var positions = new float[] { 0f, 0f, 0f, 2f, 4f, 6f, float.NaN, float.NaN, float.NaN };

        ReverseGravity.WarmStart(positions, [(0, 2), (1, 2)], 0, dimensions: 3);

        Assert.Equal([0f, 0f, 0f, 2f, 4f, 6f, 1f, 2f, 3f], positions);
    }

    [Fact]
    public void Warm_start_rejects_bad_input()
    {
        var odd = new float[] { 0f, 0f, 1f };
        Assert.Throws<ArgumentException>(() => ReverseGravity.WarmStart(odd, [], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReverseGravity.WarmStart(new float[4], [], 0, dimensions: 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReverseGravity.WarmStart(new float[4], [], 5));
        Assert.Throws<ArgumentException>(() => ReverseGravity.WarmStart(new float[] { 1f, float.NaN }, [], 0));
        Assert.Throws<ArgumentException>(() => ReverseGravity.WarmStart(new float[] { float.PositiveInfinity, 0f }, [], 0));
    }

    [Fact]
    public void Same_input_gives_the_same_output()
    {
        var edges = new[] { (0, 1), (1, 2), (2, 3), (0, 3), (4, 5) };

        var first = ReverseGravity.RingTargetRadii(ReverseGravity.HopDistances(6, edges, 2), 3, 12.5f);
        var second = ReverseGravity.RingTargetRadii(ReverseGravity.HopDistances(6, edges, 2), 3, 12.5f);
        Assert.Equal(first, second);

        var positionsA = new float[] { 0f, 0f, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, 8f, 9f, float.NaN, float.NaN };
        var positionsB = (float[])positionsA.Clone();
        ReverseGravity.WarmStart(positionsA, edges, 0);
        ReverseGravity.WarmStart(positionsB, edges, 0);
        Assert.Equal(positionsA, positionsB);
    }
}
