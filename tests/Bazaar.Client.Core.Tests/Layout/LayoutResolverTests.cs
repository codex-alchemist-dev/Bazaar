using System.Text.Json;
using Bazaar.Client.Core.Layout;

namespace Bazaar.Client.Core.Tests.Layout;

public class LayoutResolverTests
{
    private static readonly LayoutNode _leaf = new(null, [], "bazaar.gc", null, null);

    private static LayoutSlot Slot(string deviceClass, string display, string posture, string marker) =>
        new(deviceClass, display, posture, new LayoutNode(null, [], "bazaar.gc", marker, null));

    private static LayoutDocument Doc(params LayoutSlot[] slots) => new(1, "Test", [], slots);

    [Fact]
    public void Exact_match_beats_wildcards()
    {
        LayoutDocument doc = Doc(
            Slot("*", "*", "*", "fallback"),
            Slot("foldable", "cover", "*", "cover"),
            Slot("foldable", "cover", "folded", "exact"));

        LayoutSlot? slot = LayoutResolver.Select(doc, "foldable", "cover", "folded");

        Assert.Equal("exact", slot!.Root.PaneId);
    }

    [Fact]
    public void Wildcard_slot_is_used_when_no_exact_slot_exists()
    {
        LayoutDocument doc = Doc(Slot("*", "*", "*", "fallback"), Slot("foldable", "cover", "*", "cover"));

        Assert.Equal("cover", LayoutResolver.Select(doc, "foldable", "cover", "halfOpen")!.Root.PaneId);
        Assert.Equal("fallback", LayoutResolver.Select(doc, "desktop", "single", "unfolded")!.Root.PaneId);
    }

    [Fact]
    public void Ties_go_to_the_earlier_slot()
    {
        LayoutDocument doc = Doc(Slot("phone", "*", "*", "first"), Slot("phone", "*", "*", "second"));

        Assert.Equal("first", LayoutResolver.Select(doc, "phone", "single", "folded")!.Root.PaneId);
    }

    [Fact]
    public void No_match_returns_null_so_the_baked_default_applies()
    {
        LayoutDocument doc = Doc(Slot("phone", "single", "folded", "only"));

        Assert.Null(LayoutResolver.Select(doc, "desktop", "single", "unfolded"));
    }

    [Fact]
    public void Lock_patterns_cover_matching_slots_only()
    {
        LayoutSlot phoneFolded = Slot("phone", "single", "folded", "x");
        LayoutSlot desktop = Slot("desktop", "single", "unfolded", "x");

        Assert.True(LayoutResolver.IsLocked(["phone/*/*"], phoneFolded));
        Assert.False(LayoutResolver.IsLocked(["phone/*/*"], desktop));
        Assert.True(LayoutResolver.IsLocked(["*"], desktop));
        Assert.False(LayoutResolver.IsLocked([], desktop));
    }

    [Fact]
    public void Person_layout_replaces_unlocked_slot_and_adds_new_ones()
    {
        LayoutDocument company = new(1, "Company", [], [Slot("phone", "*", "*", "company-phone"), Slot("desktop", "*", "*", "company-desktop")]);
        LayoutDocument person = new(1, "Mine", [], [Slot("desktop", "*", "*", "mine-desktop"), Slot("tablet", "*", "*", "mine-tablet")]);

        LayoutMergeResult result = LayoutScopeMerger.Merge(company, person, actorIsOwner: false);

        Assert.Empty(result.Issues);
        Assert.Equal(3, result.Effective.Slots.Count);
        Assert.Equal("mine-desktop", LayoutResolver.Select(result.Effective, "desktop", "single", "unfolded")!.Root.PaneId);
        Assert.Equal("company-phone", LayoutResolver.Select(result.Effective, "phone", "single", "folded")!.Root.PaneId);
    }

    [Fact]
    public void Person_cannot_override_a_company_lock()
    {
        LayoutDocument company = new(1, "Company", ["phone/*/*"], [Slot("phone", "*", "*", "company-phone")]);
        LayoutDocument person = new(1, "Mine", [], [Slot("phone", "*", "*", "mine-phone")]);

        LayoutMergeResult result = LayoutScopeMerger.Merge(company, person, actorIsOwner: false);

        Assert.Equal("locked-slot", Assert.Single(result.Issues).Code);
        Assert.Equal("company-phone", LayoutResolver.Select(result.Effective, "phone", "single", "folded")!.Root.PaneId);
    }

    [Fact]
    public void Owner_may_override_a_company_lock()
    {
        LayoutDocument company = new(1, "Company", ["phone/*/*"], [Slot("phone", "*", "*", "company-phone")]);
        LayoutDocument owner = new(1, "Owner", [], [Slot("phone", "*", "*", "owner-phone")]);

        LayoutMergeResult result = LayoutScopeMerger.Merge(company, owner, actorIsOwner: true);

        Assert.Empty(result.Issues);
        Assert.Equal("owner-phone", LayoutResolver.Select(result.Effective, "phone", "single", "folded")!.Root.PaneId);
    }

    [Fact]
    public void Person_locks_are_ignored_when_merging()
    {
        LayoutDocument company = new(1, "Company", [], [Slot("phone", "*", "*", "company-phone")]);
        LayoutDocument person = new(1, "Mine", ["*"], [Slot("phone", "*", "*", "mine-phone")]);

        LayoutMergeResult result = LayoutScopeMerger.Merge(company, person, actorIsOwner: false);

        Assert.Empty(result.Issues);
        Assert.Empty(result.Effective.Locks);
        Assert.Equal("mine-phone", LayoutResolver.Select(result.Effective, "phone", "single", "folded")!.Root.PaneId);
    }

    [Fact]
    public void Tree_builder_maps_containers_and_unknown_kinds()
    {
        using JsonDocument doc = JsonDocument.Parse("{\"x\":1}");
        LayoutNode root = new("row", [
            new LayoutNode("tabs", [new LayoutNode(null, [], "bazaar.gc", "gc", null)], null, null, null),
            new LayoutNode("hologram", [], "vendor.unknown", "u", doc.RootElement.Clone()),
            _leaf,
        ], null, null, null);

        PlacedNode placed = LayoutTree.Build(root, kind => kind == "bazaar.gc");

        Assert.Equal(PlacedKind.Row, placed.Kind);
        Assert.Equal(PlacedKind.Tabs, placed.Children[0].Kind);
        Assert.Equal(PlacedKind.Pane, placed.Children[0].Children[0].Kind);
        Assert.Equal(PlacedKind.Placeholder, placed.Children[1].Kind);
        Assert.Equal("vendor.unknown", placed.Children[1].PaneKind);
        Assert.NotNull(placed.Children[1].PaneConfig);
    }

    [Fact]
    public void Unknown_container_stacks_its_children()
    {
        LayoutNode root = new("hologram", [_leaf], null, null, null);

        PlacedNode placed = LayoutTree.Build(root, _ => true);

        Assert.Equal(PlacedKind.Stack, placed.Kind);
        Assert.Single(placed.Children);
    }
}
