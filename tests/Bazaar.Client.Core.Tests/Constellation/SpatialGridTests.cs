using Bazaar.Client.Core.Constellation;

namespace Bazaar.Client.Core.Tests.Constellation;

public class SpatialGridTests
{
    private const int Seed = 20261030;

    [Fact]
    public void QueryRadius_matches_brute_force_on_1000_seeded_points()
    {
        float[] xy = RandomPositions(1000, Seed, 1000f);
        var grid = new SpatialGrid(25f);
        grid.Rebuild(xy);

        var random = new Random(Seed + 1);
        var buffer = new int[1000];
        for (int q = 0; q < 200; q++)
        {
            float x = (float)(random.NextDouble() * 1000.0);
            float y = (float)(random.NextDouble() * 1000.0);
            float radius = (float)(random.NextDouble() * 120.0);

            int count = grid.QueryRadius(x, y, radius, buffer);

            int[] expected = BruteForceRadius(xy, x, y, radius);
            Assert.Equal(expected.Length, count);
            Assert.Equal(expected, buffer.AsSpan(0, count).ToArray().Order().ToArray());
        }
    }

    [Fact]
    public void QueryRadius_includes_a_node_exactly_on_the_boundary()
    {
        var grid = new SpatialGrid(10f);
        grid.Rebuild([0f, 0f, 3f, 4f]);
        var buffer = new int[4];

        int count = grid.QueryRadius(0f, 0f, 5f, buffer);

        Assert.Equal(2, count);
        Assert.Equal([0, 1], buffer.AsSpan(0, count).ToArray().Order().ToArray());
    }

    [Fact]
    public void QueryRadius_reports_total_when_the_buffer_is_too_small()
    {
        var grid = new SpatialGrid(10f);
        grid.Rebuild([0f, 0f, 1f, 0f, 2f, 0f]);
        var buffer = new int[2];

        int count = grid.QueryRadius(0f, 0f, 10f, buffer);

        Assert.Equal(3, count);
    }

    [Fact]
    public void NaN_positions_are_excluded_and_never_throw()
    {
        float nan = float.NaN;
        var grid = new SpatialGrid(10f);
        grid.Rebuild([1f, 1f, nan, 2f, 3f, nan, float.PositiveInfinity, 0f, 4f, 4f]);
        var buffer = new int[10];

        int count = grid.QueryRadius(0f, 0f, 1000f, buffer);

        Assert.Equal([0, 4], buffer.AsSpan(0, count).ToArray().Order().ToArray());
        Assert.Equal(5, grid.NodeCount);
        Assert.Equal(0, grid.NearestNode(0f, 0f, 1000f));
        Assert.Equal(4, grid.NearestNode(4f, 4f, 1f));
    }

    [Fact]
    public void NaN_query_point_or_radius_returns_nothing()
    {
        var grid = new SpatialGrid(10f);
        grid.Rebuild([0f, 0f, 1f, 1f]);
        var buffer = new int[2];

        Assert.Equal(0, grid.QueryRadius(float.NaN, 0f, 5f, buffer));
        Assert.Equal(0, grid.QueryRadius(0f, 0f, float.NaN, buffer));
        Assert.Equal(0, grid.QueryRadius(0f, 0f, -1f, buffer));
        Assert.Equal(-1, grid.NearestNode(float.NaN, 0f, 5f));
    }

    [Fact]
    public void All_nan_input_builds_an_empty_grid()
    {
        var grid = new SpatialGrid(10f);
        grid.Rebuild([float.NaN, float.NaN, float.NaN, float.NaN]);
        var buffer = new int[2];

        Assert.Equal(0, grid.QueryRadius(0f, 0f, 1000f, buffer));
        Assert.Equal(-1, grid.NearestNode(0f, 0f, 1000f));
        Assert.Equal(0, grid.Columns);
    }

    [Fact]
    public void NearestNode_returns_minus_one_when_out_of_max_distance()
    {
        var grid = new SpatialGrid(10f);
        grid.Rebuild([100f, 100f]);

        Assert.Equal(-1, grid.NearestNode(0f, 0f, 50f));
        Assert.Equal(0, grid.NearestNode(0f, 0f, 200f));
    }

    [Fact]
    public void NearestNode_matches_brute_force()
    {
        float[] xy = RandomPositions(1000, Seed + 2, 1000f);
        var grid = new SpatialGrid(40f);
        grid.Rebuild(xy);
        var random = new Random(Seed + 3);

        for (int q = 0; q < 200; q++)
        {
            float x = (float)(random.NextDouble() * 1000.0);
            float y = (float)(random.NextDouble() * 1000.0);
            float maxDistance = (float)(random.NextDouble() * 150.0);

            int expected = BruteForceNearest(xy, x, y, maxDistance);
            Assert.Equal(expected, grid.NearestNode(x, y, maxDistance));
        }
    }

    [Fact]
    public void NearestNode_breaks_ties_by_lower_index()
    {
        var grid = new SpatialGrid(10f);
        grid.Rebuild([5f, 0f, -5f, 0f]);

        Assert.Equal(0, grid.NearestNode(0f, 0f, 10f));
    }

    [Fact]
    public void Rebuild_replaces_the_previous_contents_and_reuses_buffers()
    {
        var grid = new SpatialGrid(10f);
        grid.Rebuild([0f, 0f, 1f, 1f, 2f, 2f]);
        grid.Rebuild([500f, 500f]);
        var buffer = new int[3];

        Assert.Equal(0, grid.QueryRadius(0f, 0f, 5f, buffer));
        Assert.Equal(-1, grid.NearestNode(0f, 0f, 5f));
        Assert.Equal(1, grid.QueryRadius(500f, 500f, 1f, buffer));
        Assert.Equal(0, buffer[0]);
    }

    [Fact]
    public void Rebuild_rejects_an_odd_length_span()
    {
        var grid = new SpatialGrid(10f);

        Assert.Throws<ArgumentException>(() => grid.Rebuild([1f, 2f, 3f]));
    }

    [Fact]
    public void Constructor_rejects_non_positive_or_non_finite_cell_size()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpatialGrid(0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpatialGrid(-1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpatialGrid(float.NaN));
    }

    [Fact]
    public void Sparse_layout_grows_the_cell_size_within_budget()
    {
        // Two nodes 1e6 apart with a 1-unit cell would need 1e12 cells without the budget.
        var grid = new SpatialGrid(1f);
        grid.Rebuild([0f, 0f, 1_000_000f, 1_000_000f]);

        Assert.True(grid.Columns * grid.Rows <= 1024);
        Assert.True(grid.CellSize > 1f);
        Assert.Equal(1, grid.NearestNode(999_999f, 999_999f, 10f));
    }

    private static float[] RandomPositions(int count, int seed, float extent)
    {
        var random = new Random(seed);
        var xy = new float[count * 2];
        for (int i = 0; i < xy.Length; i++)
        {
            xy[i] = (float)(random.NextDouble() * extent);
        }

        return xy;
    }

    private static int[] BruteForceRadius(float[] xy, float x, float y, float radius)
    {
        var hits = new List<int>();
        for (int i = 0; i < xy.Length / 2; i++)
        {
            double dx = xy[2 * i] - (double)x;
            double dy = xy[(2 * i) + 1] - (double)y;
            if ((dx * dx) + (dy * dy) <= (double)radius * radius)
            {
                hits.Add(i);
            }
        }

        return hits.ToArray();
    }

    private static int BruteForceNearest(float[] xy, float x, float y, float maxDistance)
    {
        int best = -1;
        double bestD2 = double.PositiveInfinity;
        double max2 = (double)maxDistance * maxDistance;
        for (int i = 0; i < xy.Length / 2; i++)
        {
            double dx = xy[2 * i] - (double)x;
            double dy = xy[(2 * i) + 1] - (double)y;
            double d2 = (dx * dx) + (dy * dy);
            if (d2 <= max2 && d2 < bestD2)
            {
                best = i;
                bestD2 = d2;
            }
        }

        return best;
    }
}
