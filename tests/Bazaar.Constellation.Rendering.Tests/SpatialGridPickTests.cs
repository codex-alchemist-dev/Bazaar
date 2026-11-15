using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Rendering.Tests;

public class SpatialGridPickTests
{
    [Fact]
    public void SpatialGrid_satisfies_IPickIndex()
    {
        var grid = new SpatialGrid(10f);
        Assert.IsAssignableFrom<IPickIndex>(grid);
    }

    [Fact]
    public void Pick_through_the_interface_matches_the_concrete_query()
    {
        var grid = new SpatialGrid(10f);
        grid.Rebuild(new float[] { 0f, 0f, 100f, 100f });

        Assert.Equal(grid.Nearest(1f, 1f, 5f), PickThrough(grid, 1f, 1f, 5f));
        Assert.Equal(0, PickThrough(grid, 1f, 1f, 5f));
        Assert.Equal(-1, PickThrough(grid, 50f, 50f, 5f));
    }

    [Fact]
    public void Pick_with_an_invalid_radius_finds_nothing()
    {
        var grid = new SpatialGrid(10f);
        grid.Rebuild(new float[] { 0f, 0f });

        Assert.Equal(-1, PickThrough(grid, 0f, 0f, -1f));
        Assert.Equal(-1, PickThrough(grid, 0f, 0f, float.NaN));
    }

    private static int PickThrough<TIndex>(TIndex index, float x, float y, float radius)
        where TIndex : IPickIndex => index.Nearest(x, y, radius);
}
