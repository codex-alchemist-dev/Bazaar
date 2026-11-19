using Bazaar.Client.Layout;

namespace Bazaar.Client.Layout.Tests;

public class FoldableLayoutSelectionTests
{
    private static readonly Func<string, bool> _known = kind => kind == "bazaar.gc" || kind == "bazaar.dashboard";

    private static LayoutSlot Slot(string posture, string kind) =>
        new("foldable", "*", posture, new LayoutNode(null, [], kind, null, null));

    [Theory]
    [InlineData("folded", 320)]
    [InlineData("folded", 900)]
    [InlineData("unfolded", 320)]
    [InlineData("unfolded", 1800)]
    [InlineData("halfOpen", 599.99)]
    [InlineData("halfOpen", 320)]
    public void Variant_is_folded_or_unfolded_for_each_posture_and_width(string posture, double width)
    {
        string? expected = posture == "unfolded" || (posture == "halfOpen" && width >= 600) ? "unfolded" : "folded";

        Assert.Equal(expected, FoldableLayoutSelection.VariantFor(posture, width));
    }

    [Theory]
    [InlineData(600.0)]
    [InlineData(600.5)]
    [InlineData(1200.0)]
    public void Half_open_at_or_above_600_dp_shows_the_unfolded_variant(double width) =>
        Assert.Equal("unfolded", FoldableLayoutSelection.VariantFor("halfOpen", width));

    [Fact]
    public void Half_open_threshold_is_600_dp()
    {
        Assert.Equal(600, FoldableLayoutSelection.HalfOpenMinWidthDp);
        Assert.Equal("folded", FoldableLayoutSelection.VariantFor("halfOpen", 599.999));
    }

    [Theory]
    [InlineData("flat")]
    [InlineData("tabletop")]
    [InlineData("HalfOpen")]
    [InlineData("UNFOLDED")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void Unknown_or_null_posture_selects_no_variant(string? posture) =>
        Assert.Null(FoldableLayoutSelection.VariantFor(posture, 800));

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Invalid_width_selects_no_variant_even_for_a_known_posture(double width)
    {
        Assert.Null(FoldableLayoutSelection.VariantFor("folded", width));
        Assert.Null(FoldableLayoutSelection.VariantFor("unfolded", width));
        Assert.Null(FoldableLayoutSelection.VariantFor("halfOpen", width));
    }

    [Fact]
    public void Place_uses_the_effective_layout_for_the_selected_variant()
    {
        var effective = new LayoutDocument(1, "Mine", [], [Slot("folded", "bazaar.gc"), Slot("unfolded", "bazaar.dashboard")]);
        var baked = new LayoutDocument(1, "Baked", [], [Slot("folded", "bazaar.dashboard"), Slot("unfolded", "bazaar.dashboard")]);

        PlacedNode? folded = FoldableLayoutSelection.Place(effective, baked, "foldable", "cover", "folded", 360, _known);
        PlacedNode? unfolded = FoldableLayoutSelection.Place(effective, baked, "foldable", "inner", "unfolded", 1800, _known);

        Assert.Equal("bazaar.gc", folded!.PaneKind);
        Assert.Equal("bazaar.dashboard", unfolded!.PaneKind);
    }

    [Fact]
    public void Half_open_wide_places_the_unfolded_variant_and_narrow_places_the_folded_one()
    {
        var doc = new LayoutDocument(1, "Doc", [], [Slot("folded", "bazaar.gc"), Slot("unfolded", "bazaar.dashboard")]);

        PlacedNode? wide = FoldableLayoutSelection.Place(doc, null, "foldable", "inner", "halfOpen", 600, _known);
        PlacedNode? narrow = FoldableLayoutSelection.Place(doc, null, "foldable", "inner", "halfOpen", 599, _known);

        Assert.Equal("bazaar.dashboard", wide!.PaneKind);
        Assert.Equal("bazaar.gc", narrow!.PaneKind);
    }

    [Fact]
    public void Unknown_posture_ignores_the_effective_layout_and_uses_the_baked_folded_variant()
    {
        // The effective layout matches everything, so only a fail-closed rule can pick the baked default here.
        var effective = new LayoutDocument(1, "Mine", [], [new LayoutSlot("*", "*", "*", new LayoutNode(null, [], "bazaar.dashboard", null, null))]);
        var baked = new LayoutDocument(1, "Baked", [], [Slot("folded", "bazaar.gc"), Slot("unfolded", "bazaar.dashboard")]);

        PlacedNode? placed = FoldableLayoutSelection.Place(effective, baked, "foldable", "cover", "tabletop", 800, _known);

        Assert.Equal("bazaar.gc", placed!.PaneKind);
    }

    [Fact]
    public void Invalid_width_ignores_the_effective_layout_and_uses_the_baked_folded_variant()
    {
        var effective = new LayoutDocument(1, "Mine", [], [Slot("unfolded", "bazaar.dashboard")]);
        var baked = new LayoutDocument(1, "Baked", [], [Slot("folded", "bazaar.gc")]);

        PlacedNode? placed = FoldableLayoutSelection.Place(effective, baked, "foldable", "inner", "unfolded", double.NaN, _known);

        Assert.Equal("bazaar.gc", placed!.PaneKind);
    }

    [Fact]
    public void Null_posture_with_no_baked_folded_slot_returns_null_so_the_head_shows_its_own_fallback()
    {
        var effective = new LayoutDocument(1, "Mine", [], [Slot("folded", "bazaar.gc")]);
        var baked = new LayoutDocument(1, "Baked", [], [Slot("unfolded", "bazaar.dashboard")]);

        Assert.Null(FoldableLayoutSelection.Place(effective, baked, "foldable", "cover", null, 360, _known));
        Assert.Null(FoldableLayoutSelection.Place(effective, null, "foldable", "cover", "flat", 360, _known));
    }

    [Fact]
    public void Unregistered_pane_kind_in_the_selected_slot_becomes_a_placeholder()
    {
        var effective = new LayoutDocument(1, "Mine", [], [Slot("folded", "vendor.unknown")]);

        PlacedNode? placed = FoldableLayoutSelection.Place(effective, null, "foldable", "cover", "folded", 360, _known);

        Assert.Equal(PlacedKind.Placeholder, placed!.Kind);
    }
}
