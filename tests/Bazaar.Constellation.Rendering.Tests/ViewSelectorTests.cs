using Bazaar.Constellation.Rendering.Core;
using Xunit;

namespace Bazaar.Constellation.Rendering.Tests;

public sealed class ViewSelectorTests
{
    private static readonly TileBounds _world2D = new(-1_000_000f, -1_000_000f, 0f, 1_000_000f, 1_000_000f, 0f);
    private static readonly TileBounds _world3D = new(-1_000_000f, -1_000_000f, -1_000_000f, 1_000_000f, 1_000_000f, 1_000_000f);

    private static OrthoView Ortho(float zoom, float cx = 0, float cy = 0)
    {
        var cam = new Camera2D();
        cam.CenterOn(cx, cy);
        cam.ZoomAt(0, 0, 1920, 1080, 1f);
        // Zoom is clamped to [0.05, 64]; the world here is huge so use a fixed tiny zoom via repeated ZoomAt.
        float target = Math.Clamp(zoom, Camera2D.MinZoom, Camera2D.MaxZoom);
        cam.ZoomAt(960, 540, 1920, 1080, target);
        return new OrthoView(cam, 1920, 1080);
    }

    [Theory]
    [InlineData(1_000_000L)]
    [InlineData(2_000_000_000L)]
    [InlineData(1_000_000_000_000L)]
    public void TwoD_SelectionIsBoundedAndIndependentOfGraphSize(long nodes)
    {
        var tree = new ImplicitTileTree(_world2D, 2, nodes);
        var opts = new ViewSelectorOptions(MaxInstances: 2_000_000, MaxTiles: 4096);
        ViewSelection sel = ViewSelector.Select(tree, Ortho(0.05f), opts);
        Assert.InRange(sel.TotalInstances, 1, opts.MaxInstances);
        Assert.InRange(sel.Tiles.Count, 1, opts.MaxTiles);
        Assert.InRange(sel.TilesVisited, 1, 40_000);
        Assert.Equal(sel.TotalInstances, sel.Tiles.Sum(t => t.Instances));
    }

    [Fact]
    public void TwoD_ZoomedIn_OnlyVisibleTilesAreChosen()
    {
        var tree = new ImplicitTileTree(_world2D, 2, 2_000_000_000L);
        ViewSelection sel = ViewSelector.Select(tree, Ortho(8f, 500_000, -250_000));
        var view = Ortho(8f, 500_000, -250_000);
        Assert.NotEmpty(sel.Tiles);
        Assert.All(sel.Tiles, t => Assert.True(view.Intersects(tree.GetInfo(t.Key).Bounds)));
        Assert.True(sel.TotalInstances <= 2_000_000);
    }

    [Fact]
    public void TileOutsideTheView_IsNotSelected()
    {
        var tree = new ImplicitTileTree(_world2D, 2, 1_000_000L);
        ViewSelection sel = ViewSelector.Select(tree, Ortho(1f, 5_000_000, 5_000_000));
        Assert.Empty(sel.Tiles);
        Assert.Equal(0, sel.TotalInstances);
    }

    [Fact]
    public void TighterInstanceCap_NeverExceeded()
    {
        var tree = new ImplicitTileTree(_world2D, 2, 2_000_000_000L);
        var opts = new ViewSelectorOptions(MaxInstances: 300, MaxTiles: 64);
        ViewSelection sel = ViewSelector.Select(tree, Ortho(0.05f), opts);
        Assert.True(sel.TotalInstances <= 300);
        Assert.True(sel.Tiles.Count <= 64);
    }

    [Fact]
    public void MoreZoom_RefinesTowardsLeaves()
    {
        var tree = new ImplicitTileTree(_world2D, 2, 1_000_000L, leafCapacity: 1000);
        int coarse = ViewSelector.Select(tree, Ortho(0.05f)).Tiles.Max(t => t.Key.Level);
        int fine = ViewSelector.Select(tree, Ortho(64f)).Tiles.Max(t => t.Key.Level);
        Assert.True(fine >= coarse);
    }

    [Theory]
    [InlineData(2_000_000_000L)]
    [InlineData(1_000_000_000_000L)]
    public void ThreeD_SelectionIsBoundedAndSeesTheFrustumOnly(long nodes)
    {
        var tree = new ImplicitTileTree(_world3D, 3, nodes);
        var cam = new PerspectiveCamera3D(0, 0, 3_000_000, 0, 0, 0, Far: 1.0e8f);
        var view = new PerspectiveView(cam, 1920, 1080);
        var opts = new ViewSelectorOptions(MaxInstances: 2_000_000, MaxTiles: 4096);
        ViewSelection sel = ViewSelector.Select(tree, view, opts);
        Assert.InRange(sel.TotalInstances, 1, opts.MaxInstances);
        Assert.InRange(sel.Tiles.Count, 1, opts.MaxTiles);
        Assert.All(sel.Tiles, t => Assert.True(view.Intersects(tree.GetInfo(t.Key).Bounds)));
    }

    [Fact]
    public void ThreeD_BoxBehindTheCamera_IsCulled()
    {
        var cam = new PerspectiveCamera3D(0, 0, 0, 0, 0, -1, Far: 1000f);
        var view = new PerspectiveView(cam, 1920, 1080);
        Assert.False(view.Intersects(new TileBounds(-1, -1, 10, 1, 1, 12)));
        Assert.True(view.Intersects(new TileBounds(-1, -1, -12, 1, 1, -10)));
        Assert.False(view.Intersects(new TileBounds(-1, -1, -2000, 1, 1, -1500)));
        Assert.False(view.Intersects(new TileBounds(5000, -1, -12, 5002, 1, -10)));
    }

    [Fact]
    public void ThreeD_NearerBoxesProjectLarger()
    {
        var cam = new PerspectiveCamera3D(0, 0, 0, 0, 0, -1, Far: 1000f);
        var view = new PerspectiveView(cam, 1920, 1080);
        float near = view.ProjectedPixels(new TileBounds(-1, -1, -11, 1, 1, -9));
        float far = view.ProjectedPixels(new TileBounds(-1, -1, -101, 1, 1, -99));
        Assert.True(near > far * 5f);
    }

    [Fact]
    public void ImplicitTree_LeafCountsStayWithinCapacity()
    {
        var tree = new ImplicitTileTree(_world3D, 3, 2_000_000_000L, leafCapacity: 4096);
        TileInfo leaf = tree.GetInfo(new TileKey(tree.LeafLevel, 0, 0, 0));
        Assert.True(leaf.IsLeaf);
        Assert.True(leaf.NodeCount <= 4096);
        Span<TileKey> kids = stackalloc TileKey[8];
        Assert.Equal(8, tree.GetChildren(TileKey.Root, kids));
        Assert.Equal(0, tree.GetChildren(new TileKey(tree.LeafLevel, 0, 0, 0), kids));
    }
}
