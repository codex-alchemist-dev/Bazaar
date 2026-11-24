namespace Bazaar.Client.Posture.Tests;

public class PostureClassifierTests
{
    [Fact]
    public void No_hinge_is_flat()
    {
        var reading = new FoldReading(false, HingeOrientation.None, null, null);
        Assert.Equal(DevicePosture.Flat, PostureClassifier.Classify(reading));
    }

    [Theory]
    [InlineData(HingeOrientation.Vertical, DevicePosture.Book)]
    [InlineData(HingeOrientation.Horizontal, DevicePosture.Tabletop)]
    [InlineData(HingeOrientation.None, DevicePosture.HalfOpen)]
    public void Half_open_follows_hinge_orientation(HingeOrientation orientation, DevicePosture expected)
    {
        var reading = new FoldReading(true, orientation, false, null);
        Assert.Equal(expected, PostureClassifier.Classify(reading));
    }

    [Fact]
    public void Platform_flat_state_is_used_without_an_angle()
    {
        var reading = new FoldReading(true, HingeOrientation.Vertical, true, null);
        Assert.Equal(DevicePosture.Flat, PostureClassifier.Classify(reading));
    }

    [Fact]
    public void Hinge_with_no_state_and_no_angle_counts_as_half_open()
    {
        var reading = new FoldReading(true, HingeOrientation.Vertical, null, null);
        Assert.Equal(DevicePosture.Book, PostureClassifier.Classify(reading));
    }

    [Fact]
    public void Angle_wins_over_the_reported_state()
    {
        var reading = new FoldReading(true, HingeOrientation.Vertical, true, 100);
        Assert.Equal(DevicePosture.Book, PostureClassifier.Classify(reading));
    }

    [Fact]
    public void Flat_has_hysteresis()
    {
        var near = new FoldReading(true, HingeOrientation.Vertical, null, 160);
        Assert.Equal(DevicePosture.Book, PostureClassifier.Classify(near, DevicePosture.Book));
        Assert.Equal(DevicePosture.Flat, PostureClassifier.Classify(near, DevicePosture.Flat));
        var open = near with { AngleDegrees = 170 };
        Assert.Equal(DevicePosture.Flat, PostureClassifier.Classify(open, DevicePosture.Book));
    }

    [Fact]
    public void ToState_drops_the_hinge_when_there_is_none()
    {
        var state = PostureClassifier.ToState(new FoldReading(false, HingeOrientation.None, null, null), new HingeInset(HingeOrientation.Vertical, 10, 4));
        Assert.True(state.Hinge.IsNone);
        Assert.Equal(DevicePosture.Flat, state.Posture);
    }
}

public class LayoutSelectorTests
{
    private static PostureState State(DevicePosture p, HingeOrientation o = HingeOrientation.Vertical) =>
        new(p, null, new HingeInset(o, 400, 10));

    [Fact]
    public void Wide_flat_window_is_dual_pane()
    {
        var s = LayoutSelector.Select(State(DevicePosture.Flat), 900, LayoutVariants.All);
        Assert.Equal(new LayoutSelection(PostureLayoutClass.DualPane, LayoutVariantKind.Unfolded), s);
    }

    [Fact]
    public void Narrow_flat_window_is_folded()
    {
        var s = LayoutSelector.Select(PostureState.Flat, 400, LayoutVariants.All);
        Assert.Equal(PostureLayoutClass.Folded, s.Class);
    }

    [Fact]
    public void Flat_without_an_unfolded_variant_is_folded()
    {
        var s = LayoutSelector.Select(PostureState.Flat, 900, LayoutVariants.Book);
        Assert.Equal(PostureLayoutClass.Folded, s.Class);
    }

    [Fact]
    public void Book_and_tabletop_use_their_own_variant()
    {
        Assert.Equal(new LayoutSelection(PostureLayoutClass.Book, LayoutVariantKind.Book), LayoutSelector.Select(State(DevicePosture.Book), 900, LayoutVariants.All));
        Assert.Equal(
            new LayoutSelection(PostureLayoutClass.Tabletop, LayoutVariantKind.Tabletop),
            LayoutSelector.Select(State(DevicePosture.Tabletop, HingeOrientation.Horizontal), 900, LayoutVariants.All));
    }

    [Fact]
    public void Missing_book_variant_falls_back_to_half_open_then_folded()
    {
        var book = State(DevicePosture.Book);
        Assert.Equal(new LayoutSelection(PostureLayoutClass.Book, LayoutVariantKind.HalfOpen), LayoutSelector.Select(book, 900, LayoutVariants.HalfOpen | LayoutVariants.Unfolded));
        Assert.Equal(new LayoutSelection(PostureLayoutClass.Folded, LayoutVariantKind.Folded), LayoutSelector.Select(book, 900, LayoutVariants.Unfolded));
    }

    [Fact]
    public void Unknown_half_open_uses_hinge_orientation_for_geometry()
    {
        var h = State(DevicePosture.HalfOpen, HingeOrientation.Horizontal);
        Assert.Equal(PostureLayoutClass.Tabletop, LayoutSelector.Select(h, 900, LayoutVariants.HalfOpen).Class);
        var v = State(DevicePosture.HalfOpen);
        Assert.Equal(PostureLayoutClass.Book, LayoutSelector.Select(v, 900, LayoutVariants.HalfOpen).Class);
        Assert.Equal(PostureLayoutClass.Folded, LayoutSelector.Select(v, 900, LayoutVariants.Book).Class);
    }
}

public class PostureGeometryTests
{
    [Fact]
    public void Folded_gives_the_first_pane_the_whole_window()
    {
        var (first, second) = PostureGeometry.Settled(PostureLayoutClass.Folded, HingeInset.None, 400, 800);
        Assert.Equal(new PaneRegion(0, 0, 400, 800), first);
        Assert.True(second.IsEmpty);
    }

    [Fact]
    public void Book_splits_at_the_hinge_and_keeps_the_gap_clear()
    {
        var hinge = new HingeInset(HingeOrientation.Vertical, 500, 20);
        var (first, second) = PostureGeometry.Settled(PostureLayoutClass.Book, hinge, 1000, 800);
        Assert.Equal(500, first.Width);
        Assert.Equal(520, second.X);
        Assert.Equal(480, second.Width);
        Assert.True(first.X + first.Width <= hinge.Offset);
        Assert.True(second.X >= hinge.Offset + hinge.Size);
    }

    [Fact]
    public void Tabletop_stacks_content_over_controls_around_a_horizontal_hinge()
    {
        var hinge = new HingeInset(HingeOrientation.Horizontal, 400, 16);
        var (top, bottom) = PostureGeometry.Settled(PostureLayoutClass.Tabletop, hinge, 600, 1000);
        Assert.Equal(new PaneRegion(0, 0, 600, 400), top);
        Assert.Equal(new PaneRegion(0, 416, 600, 584), bottom);
    }

    [Fact]
    public void Without_a_matching_hinge_the_split_uses_the_ratio_and_no_gap()
    {
        var (first, second) = PostureGeometry.Settled(PostureLayoutClass.DualPane, HingeInset.None, 1000, 800, 0.4);
        Assert.Equal(400, first.Width);
        Assert.Equal(400, second.X);
        var wrong = new HingeInset(HingeOrientation.Horizontal, 300, 10);
        Assert.Equal(400, PostureGeometry.Settled(PostureLayoutClass.DualPane, wrong, 1000, 800, 0.4).First.Width);
    }

    [Fact]
    public void Transitional_runs_from_folded_to_settled()
    {
        var hinge = new HingeInset(HingeOrientation.Vertical, 500, 20);
        var (f0, s0) = PostureGeometry.Transitional(PostureLayoutClass.Book, hinge, 1000, 800, 0);
        Assert.Equal(1000, f0.Width);
        Assert.True(s0.IsEmpty);
        var (f1, s1) = PostureGeometry.Transitional(PostureLayoutClass.Book, hinge, 1000, 800, 1);
        Assert.Equal(PostureGeometry.Settled(PostureLayoutClass.Book, hinge, 1000, 800), (f1, s1));
        var (fh, _) = PostureGeometry.Transitional(PostureLayoutClass.Book, hinge, 1000, 800, 0.5);
        Assert.Equal(750, fh.Width);
    }
}
