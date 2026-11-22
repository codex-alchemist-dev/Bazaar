using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Rendering.Tests;

public class SectionSetTests
{
    private static readonly SectionCullSettings _loose = new() { MinPixels = 1f, MinVisibleMembers = 1, FullAlphaPixels = 2f };

    private static Dictionary<string, (float X, float Y)> Pos(params (string Id, float X, float Y)[] p) =>
        p.ToDictionary(t => t.Id, t => (t.X, t.Y));

    private static List<SectionRect> Run(SectionSet set, Dictionary<string, (float X, float Y)> pos,
        SectionCullSettings? s = null, float zoom = 1f)
    {
        var cam = new Camera2D();
        cam.ZoomAt(400f, 300f, 800, 600, zoom);
        var o = new List<SectionRect>();
        set.Compute(pos, cam, 800, 600, s ?? _loose, o);
        return o;
    }

    [Fact]
    public void Rect_bounds_members_with_padding()
    {
        var set = new SectionSet([new SectionDef("a", "A", ["n1", "n2"])]);
        var r = Assert.Single(Run(set, Pos(("n1", -10f, -20f), ("n2", 30f, 40f))));
        Assert.Equal(-22f, r.MinX);
        Assert.Equal(-32f, r.MinY);
        Assert.Equal(42f, r.MaxX);
        Assert.Equal(52f, r.MaxY);
    }

    [Fact]
    public void Child_sits_inside_parent_and_draws_after_it()
    {
        var set = new SectionSet([
            new SectionDef("child", "C", ["n1"], "parent"),
            new SectionDef("parent", "P", ["n2"])]);
        var rects = Run(set, Pos(("n1", 0f, 0f), ("n2", 100f, 0f)));
        Assert.Equal(["parent", "child"], rects.Select(r => r.Id));
        SectionRect p = rects[0], c = rects[1];
        Assert.True(p.MinX < c.MinX && p.MinY < c.MinY && p.MaxX > c.MaxX && p.MaxY > c.MaxY);
        Assert.Equal("parent", c.ParentId);
        Assert.Equal(1, c.Depth);
    }

    [Fact]
    public void Member_without_position_is_ignored_and_empty_section_skipped()
    {
        var set = new SectionSet([
            new SectionDef("a", "A", ["n1", "ghost"]),
            new SectionDef("b", "B", ["ghost"])]);
        var r = Assert.Single(Run(set, Pos(("n1", 5f, 5f))));
        Assert.Equal(-7f, r.MinX);
        Assert.Equal(17f, r.MaxX);
    }

    [Fact]
    public void Offscreen_section_is_culled()
    {
        var set = new SectionSet([new SectionDef("a", "A", ["n1", "n2"])]);
        Assert.Empty(Run(set, Pos(("n1", 50000f, 0f), ("n2", 50050f, 0f))));
    }

    [Fact]
    public void Too_small_section_is_culled_and_few_visible_members_cull()
    {
        var set = new SectionSet([new SectionDef("a", "A", ["n1", "n2"])]);
        var pos = Pos(("n1", 0f, 0f), ("n2", 10f, 0f));
        Assert.Empty(Run(set, pos, new SectionCullSettings { MinPixels = 500f, MinVisibleMembers = 1 }));
        var far = Pos(("n1", 0f, 0f), ("n2", 50000f, 0f));
        Assert.Empty(Run(set, far, new SectionCullSettings { MinPixels = 1f, MinVisibleMembers = 2 }));
    }

    [Fact]
    public void Alpha_grows_monotonically_with_zoom_and_label_needs_size()
    {
        var set = new SectionSet([new SectionDef("a", "A", ["n1", "n2"])]);
        var pos = Pos(("n1", 0f, 0f), ("n2", 40f, 0f));
        var s = new SectionCullSettings { MinPixels = 30f, FullAlphaPixels = 300f, LabelMinPixels = 100f, MinVisibleMembers = 1 };
        float prev = -1f;
        bool sawNoLabel = false, sawLabel = false;
        foreach (float zoom in new[] { 0.6f, 1f, 1.5f, 2.5f, 4f, 8f })
        {
            var rects = Run(set, pos, s, zoom);
            if (rects.Count == 0)
            {
                continue;
            }

            Assert.True(rects[0].Alpha >= prev);
            prev = rects[0].Alpha;
            sawNoLabel |= !rects[0].ShowLabel;
            sawLabel |= rects[0].ShowLabel;
        }

        Assert.True(sawNoLabel && sawLabel);
        Assert.Equal(s.MaxAlpha, prev, 4);
    }

    [Fact]
    public void Ordering_is_deterministic_and_cap_sheds_smallest()
    {
        var defs = new[]
        {
            new SectionDef("z", "Z", ["a", "b"]),
            new SectionDef("m", "M", ["a", "c"]),
            new SectionDef("a", "A", ["a", "d"]),
        };
        var pos = Pos(("a", 0f, 0f), ("b", 20f, 0f), ("c", 60f, 0f), ("d", 100f, 0f));
        var one = Run(new SectionSet(defs), pos);
        var two = Run(new SectionSet(defs.Reverse().ToArray()), pos);
        Assert.Equal(["a", "m", "z"], one.Select(r => r.Id));
        Assert.Equal(one.Select(r => r.Id), two.Select(r => r.Id));

        var capped = Run(new SectionSet(defs), pos, new SectionCullSettings { MinPixels = 1f, MinVisibleMembers = 1, MaxSections = 2 });
        Assert.Equal(["a", "m"], capped.Select(r => r.Id));
    }

    [Fact]
    public void Parent_cycle_does_not_hang()
    {
        var set = new SectionSet([
            new SectionDef("a", "A", ["n1"], "b"),
            new SectionDef("b", "B", ["n2"], "a")]);
        Assert.Equal(2, Run(set, Pos(("n1", 0f, 0f), ("n2", 10f, 0f))).Count);
    }
}
