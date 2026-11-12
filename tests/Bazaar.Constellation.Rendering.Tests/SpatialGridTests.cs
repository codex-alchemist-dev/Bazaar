using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Rendering.Tests;

public class SpatialGridTests
{
    [Fact]
    public void Empty_grid_finds_nothing()
    {
        var grid = new SpatialGrid(10f);
        grid.Build(ReadOnlySpan<float>.Empty, 2);
        Assert.Equal(-1, grid.Nearest(0f, 0f, 50f));
    }

    [Fact]
    public void Finds_the_node_under_the_pointer_and_ignores_far_ones()
    {
        var grid = new SpatialGrid(10f);
        grid.Build(new float[] { 0f, 0f, 100f, 100f }, 2);
        Assert.Equal(0, grid.Nearest(1f, 1f, 5f));
        Assert.Equal(-1, grid.Nearest(50f, 50f, 5f));
    }

    [Fact]
    public void Matches_brute_force_on_a_seeded_scatter()
    {
        var random = new Random(14);
        const int count = 2000;
        var positions = new float[count * 2];
        for (int i = 0; i < positions.Length; i++)
        {
            positions[i] = (float)((random.NextDouble() * 400.0) - 200.0);
        }

        var grid = new SpatialGrid(16f);
        grid.Build(positions, 2);
        for (int probe = 0; probe < 200; probe++)
        {
            float x = (float)((random.NextDouble() * 440.0) - 220.0);
            float y = (float)((random.NextDouble() * 440.0) - 220.0);
            float radius = 25f;
            int expected = BruteForceNearest(positions, x, y, radius);
            Assert.Equal(expected, grid.Nearest(x, y, radius));
        }
    }

    [Fact]
    public void Ignores_z_for_3d_frames()
    {
        var grid = new SpatialGrid(10f);
        grid.Build(new float[] { 2f, 3f, 999f }, 3);
        Assert.Equal(0, grid.Nearest(2f, 3f, 1f));
    }

    private static int BruteForceNearest(float[] xy, float x, float y, float radius)
    {
        int best = -1;
        float bestSquared = radius * radius;
        for (int node = 0; node < xy.Length / 2; node++)
        {
            float dx = xy[node * 2] - x;
            float dy = xy[(node * 2) + 1] - y;
            float d = (dx * dx) + (dy * dy);
            if (d <= bestSquared && (best < 0 || d < bestSquared))
            {
                best = node;
                bestSquared = d;
            }
        }

        return best;
    }
}
