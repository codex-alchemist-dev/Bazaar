using Bazaar.Client.Core.Layout;

namespace Bazaar.Client.Core.Tests.Layout;

public class LayoutSelectionTests
{
    private static readonly Func<string, bool> _known = kind => kind == "bazaar.gc";

    private static LayoutSlot Slot(string deviceClass, string display, string posture, string kind) =>
        new(deviceClass, display, posture, new LayoutNode(null, [], kind, null, null));

    [Fact]
    public void Effective_layout_wins_over_the_baked_default()
    {
        var effective = new LayoutDocument(1, "Mine", [], [Slot("phone", "*", "*", "bazaar.gc")]);
        var baked = new LayoutDocument(1, "Baked", [], [Slot("phone", "*", "*", "bazaar.dashboard")]);

        PlacedNode? placed = LayoutSelection.Place(effective, baked, "phone", "single", "folded", _known);

        Assert.Equal(PlacedKind.Pane, placed!.Kind);
        Assert.Equal("bazaar.gc", placed.PaneKind);
    }

    [Fact]
    public void Baked_default_is_used_when_the_effective_layout_has_no_matching_slot()
    {
        var effective = new LayoutDocument(1, "Mine", [], [Slot("desktop", "*", "*", "bazaar.gc")]);
        var baked = new LayoutDocument(1, "Baked", [], [Slot("phone", "*", "*", "bazaar.gc")]);

        PlacedNode? placed = LayoutSelection.Place(effective, baked, "phone", "single", "folded", _known);

        Assert.NotNull(placed);
        Assert.Equal(PlacedKind.Pane, placed.Kind);
    }

    [Fact]
    public void Null_when_nothing_matches_so_the_head_shows_its_own_fallback()
    {
        var baked = new LayoutDocument(1, "Baked", [], [Slot("phone", "*", "*", "bazaar.gc")]);

        Assert.Null(LayoutSelection.Place(null, baked, "desktop", "single", "unfolded", _known));
        Assert.Null(LayoutSelection.Place(null, null, "phone", "single", "folded", _known));
    }

    [Fact]
    public void Unregistered_pane_kind_becomes_a_placeholder()
    {
        var effective = new LayoutDocument(1, "Mine", [], [Slot("phone", "*", "*", "vendor.unknown")]);

        PlacedNode? placed = LayoutSelection.Place(effective, null, "phone", "single", "folded", _known);

        Assert.Equal(PlacedKind.Placeholder, placed!.Kind);
    }
}
