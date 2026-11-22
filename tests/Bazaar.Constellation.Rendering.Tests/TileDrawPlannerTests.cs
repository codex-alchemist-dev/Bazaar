using Bazaar.Constellation.Rendering.Core;
using Xunit;

namespace Bazaar.Constellation.Rendering.Tests;

public sealed class TileDrawPlannerTests
{
    private static readonly TileBounds _world2D = new(-1_000_000f, -1_000_000f, 0f, 1_000_000f, 1_000_000f, 0f);
    private static readonly TileBounds _world3D = new(-1_000_000f, -1_000_000f, -1_000_000f, 1_000_000f, 1_000_000f, 1_000_000f);

    private static OrthoView Ortho(float zoom, float cx = 0, float cy = 0)
    {
        var cam = new Camera2D();
        cam.CenterOn(cx, cy);
        cam.ZoomAt(960, 540, 1920, 1080, Math.Clamp(zoom, Camera2D.MinZoom, Camera2D.MaxZoom));
        return new OrthoView(cam, 1920, 1080);
    }

    private static PerspectiveView Persp(float distance) =>
        new(new PerspectiveCamera3D(0, 0, distance, 0, 0, 0, Far: 1.0e8f), 1920, 1080);

    [Theory]
    [InlineData(1_000_000L, 2)]
    [InlineData(2_000_000_000L, 2)]
    [InlineData(1_000_000_000_000L, 2)]
    [InlineData(1_000_000L, 3)]
    [InlineData(2_000_000_000L, 3)]
    [InlineData(1_000_000_000_000L, 3)]
    public void BudgetIsNeverExceeded_AtAnyScaleAndDetail(long nodes, int dims)
    {
        var tree = new ImplicitTileTree(dims == 2 ? _world2D : _world3D, dims, nodes);
        var list = new List<TileDraw>();
        foreach (float detail in new[] { 0.05f, 1f, 2f })
        {
            foreach (ViewVolume view in dims == 2
                ? new ViewVolume[] { Ortho(0.05f), Ortho(1f), Ortho(32f, 1000, 1000) }
                : new ViewVolume[] { Persp(3_000_000), Persp(200_000), Persp(20_000) })
            {
                ViewSelectorOptions opts = ScaledOptions(RenderTiers.Mid, detail);
                ViewSelection sel = ViewSelector.Select(tree, view, opts);
                int edges = (int)(RenderTiers.Mid.MaxEdges * detail);
                TileDrawPlan plan = TileDrawPlanner.Plan(sel, tree, view, opts.MaxInstances, edges, list);
                Assert.True(plan.NodeInstances <= opts.MaxInstances);
                Assert.True(plan.Edges <= edges);
                Assert.Equal(plan.NodeInstances, list.Where(d => d.Mode == TileDrawMode.Nodes).Sum(d => d.NodeCount));
                Assert.Equal(plan.Edges, list.Sum(d => d.Edges));
                Assert.All(list, d => Assert.True(d.NodeCount <= d.Available));
                Assert.Equal(sel.Tiles.Count, plan.NodeTiles + plan.SplatTiles);
            }
        }
    }

    [Fact]
    public void ControllerOverload_UsesCurrentCaps()
    {
        var tree = new ImplicitTileTree(_world2D, 2, 2_000_000_000L);
        var budget = new FrameBudgetController(RenderTiers.Mid, 3.3);
        for (int i = 0; i < 30; i++)
        {
            budget.Update(8.0);
        }

        ViewVolume view = Ortho(8f);
        ViewSelection sel = ViewSelector.Select(tree, view, budget.Current());
        var list = new List<TileDraw>();
        TileDrawPlan plan = TileDrawPlanner.Plan(sel, tree, view, budget, list);
        Assert.True(plan.NodeInstances <= budget.Current().MaxInstances);
        Assert.True(plan.Edges <= budget.EdgeBudget);
    }

    [Fact]
    public void Decimation_SharesBudgetAndIsDeterministic()
    {
        var tree = new ImplicitTileTree(_world2D, 2, 2_000_000_000L);
        ViewVolume view = Ortho(32f, 1000, 1000);
        ViewSelection sel = ViewSelector.Select(tree, view, new ViewSelectorOptions(MaxInstances: 2_000_000));
        var a = new List<TileDraw>();
        var b = new List<TileDraw>();
        TileDrawPlan pa = TileDrawPlanner.Plan(sel, tree, view, 1000, 0, a);
        TileDrawPlan pb = TileDrawPlanner.Plan(sel, tree, view, 1000, 0, b);
        Assert.Equal(pa, pb);
        Assert.Equal(a, b);
        Assert.Equal(1000, pa.NodeInstances);
        Assert.Contains(a, d => d.NodeCount < d.Available);
    }

    [Fact]
    public void Decimation_KeepsTopImportance()
    {
        var draw = new TileDraw(TileKey.Root, TileDrawMode.Nodes, 8, 3, 0);
        float[] importance = [1f, 9f, 2f, 8f, 3f, 7f, 0f, 4f];
        Assert.Equal([1, 3, 5], TileDrawPlanner.SelectNodes(draw, importance));
        Assert.Empty(TileDrawPlanner.SelectNodes(draw with { Mode = TileDrawMode.Splat }, importance));
    }

    [Fact]
    public void ZoomedOut_FallsBackToSplats_ZoomedInDrawsNodes()
    {
        var tree = new ImplicitTileTree(_world2D, 2, 1_000_000_000_000L);
        var list = new List<TileDraw>();

        ViewVolume far = Ortho(0.05f);
        ViewSelection farSel = ViewSelector.Select(tree, far, new ViewSelectorOptions(MaxInstances: 2_000_000));
        TileDrawPlan farPlan = TileDrawPlanner.Plan(farSel, tree, far, 2_000_000, 100_000, list);
        Assert.True(farPlan.SplatTiles > 0);
        Assert.Equal(0, farPlan.Edges);

        ViewVolume near = Ortho(64f, 5, 5);
        ViewSelection nearSel = ViewSelector.Select(tree, near, new ViewSelectorOptions(MaxInstances: 2_000_000));
        TileDrawPlan nearPlan = TileDrawPlanner.Plan(nearSel, tree, near, 2_000_000, 100_000, list);
        Assert.True(nearPlan.NodeTiles > 0);
    }

    [Fact]
    public void Edges_GoOnlyToFullTiles()
    {
        var tree = new ImplicitTileTree(_world2D, 2, 50_000L);
        ViewVolume view = Ortho(8f);
        ViewSelection sel = ViewSelector.Select(tree, view, new ViewSelectorOptions(MaxInstances: 2_000_000));
        var list = new List<TileDraw>();
        TileDrawPlan plan = TileDrawPlanner.Plan(sel, tree, view, 2_000_000, 1000, list);
        Assert.InRange(plan.Edges, 1, 1000);
        for (int i = 0; i < list.Count; i++)
        {
            if (!sel.Tiles[i].Full)
            {
                Assert.Equal(0, list[i].Edges);
            }
        }
    }

    [Fact]
    public void EmptySelection_GivesEmptyPlan()
    {
        var tree = new ImplicitTileTree(_world2D, 2, 1_000_000L);
        var list = new List<TileDraw> { default };
        TileDrawPlan plan = TileDrawPlanner.Plan(new ViewSelection([], 0, 1), tree, Ortho(1f), 1000, 100, list);
        Assert.Equal(default, plan);
        Assert.Empty(list);
    }

    private static ViewSelectorOptions ScaledOptions(RenderTier t, float detail) => new(
        Math.Max(1L, (long)(t.MaxInstances * detail)), t.RefineAtPixels / detail, Math.Max(1, (int)(t.MaxTiles * detail)));
}
