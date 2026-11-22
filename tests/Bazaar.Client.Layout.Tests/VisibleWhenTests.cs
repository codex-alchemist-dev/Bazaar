using Bazaar.Rubedo.Rules;

namespace Bazaar.Client.Layout.Tests;

public class VisibleWhenTests
{
    private static RubedoContext Context(string deviceClass = "phone", decimal widthDp = 400) =>
        RubedoContext.ForDevice(deviceClass, "single", "folded", widthDp, 800, 1m, false, false, "light");

    private static LayoutNode Pane(string kind, string? paneId, string? visibleWhen) =>
        new(null, [], kind, paneId, null, visibleWhen);

    private static LayoutLoadResult Load(string visibleWhenJson) => LayoutValidator.Validate($$"""
        {
          "schemaVersion": 1, "name": "Rules",
          "slots": [ { "deviceClass": "*", "display": "*", "posture": "*",
            "root": { "container": "column", "children": [
              { "paneKind": "bazaar.gc", "paneId": "gc", "visibleWhen": {{visibleWhenJson}} }
            ] } } ]
        }
        """);

    [Fact]
    public void Validator_keeps_a_valid_rule_on_the_node()
    {
        LayoutLoadResult result = Load("\"deviceClass == \\\"phone\\\" && widthDp < 600\"");

        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(i => $"{i.Path} {i.Code}")));
        Assert.Equal("deviceClass == \"phone\" && widthDp < 600", result.Document!.Slots[0].Root.Children[0].VisibleWhen);
    }

    [Theory]
    [InlineData("\"widthDp >\"", "visible-when-invalid")]
    [InlineData("\"nope == 1\"", "visible-when-invalid")]
    [InlineData("\"widthDp + 1\"", "visible-when-type")]
    [InlineData("\"\"", "bad-visible-when")]
    [InlineData("5", "bad-visible-when")]
    public void Validator_rejects_a_bad_rule_with_a_pointer_path(string json, string code)
    {
        LayoutLoadResult result = Load(json);

        Assert.False(result.IsValid);
        Assert.Null(result.Document);
        Assert.Contains(result.Issues, i => i.Code == code && i.Path == "/slots/0/root/children/0/visibleWhen");
    }

    [Fact]
    public void Rule_text_inside_pane_config_is_still_scanned_for_forbidden_content()
    {
        LayoutLoadResult result = LayoutValidator.Validate("""
            { "schemaVersion": 1, "name": "Rules", "slots": [ { "deviceClass": "*", "display": "*", "posture": "*",
              "root": { "paneKind": "bazaar.gc", "paneConfig": { "visibleWhen": "<script>" } } } ] }
            """);

        Assert.Contains(result.Issues, i => i.Code == "forbidden-content");
    }

    [Fact]
    public void Node_is_shown_or_hidden_by_device_class_and_width()
    {
        var root = new LayoutNode("column", [Pane("bazaar.gc", "gc", "deviceClass == \"phone\" && widthDp < 600"), Pane("bazaar.dashboard", "dash", null)], null, null, null);

        LayoutPlacement phone = LayoutTree.Build(root, _ => true, Context("phone", 400));
        LayoutPlacement wide = LayoutTree.Build(root, _ => true, Context("phone", 900));
        LayoutPlacement desktop = LayoutTree.Build(root, _ => true, Context("desktop", 400));

        Assert.Equal(["gc", "dash"], phone.Root!.Children.Select(c => c.PaneId));
        Assert.Equal(["dash"], wide.Root!.Children.Select(c => c.PaneId));
        Assert.Equal(["dash"], desktop.Root!.Children.Select(c => c.PaneId));
        Assert.Empty(phone.Issues);
        Assert.Empty(wide.Issues);
    }

    [Fact]
    public void Rule_that_errors_hides_the_node_and_reports_an_issue()
    {
        var root = new LayoutNode("column", [Pane("bazaar.gc", "gc", "10 / (widthDp - 400) > 1"), Pane("bazaar.dashboard", "dash", null)], null, null, null);

        LayoutPlacement placement = LayoutTree.Build(root, _ => true, Context("phone", 400), "/slots/2/root");

        Assert.Equal(["dash"], placement.Root!.Children.Select(c => c.PaneId));
        LayoutIssue issue = Assert.Single(placement.Issues);
        Assert.Equal("visible-when-error", issue.Code);
        Assert.Equal("/slots/2/root/children/0/visibleWhen", issue.Path);
    }

    [Fact]
    public void Uncompilable_rule_on_a_hand_built_node_fails_closed()
    {
        LayoutPlacement placement = LayoutTree.Build(Pane("bazaar.gc", "gc", "widthDp >"), _ => true, Context());

        Assert.Null(placement.Root);
        Assert.Equal("visible-when-invalid", Assert.Single(placement.Issues).Code);
    }

    [Fact]
    public void Hidden_container_hides_its_children()
    {
        var inner = new LayoutNode("row", [Pane("bazaar.gc", "gc", null)], null, null, null, "deviceClass == \"desktop\"");
        var root = new LayoutNode("column", [inner, Pane("bazaar.dashboard", "dash", null)], null, null, null);

        LayoutPlacement placement = LayoutTree.Build(root, _ => true, Context("phone"));

        PlacedNode only = Assert.Single(placement.Root!.Children);
        Assert.Equal("dash", only.PaneId);
    }

    [Fact]
    public void Existing_build_overload_ignores_rules()
    {
        var root = new LayoutNode("column", [Pane("bazaar.gc", "gc", "false")], null, null, null);

        PlacedNode placed = LayoutTree.Build(root, _ => true);

        Assert.Single(placed.Children);
    }

    [Fact]
    public void Selection_applies_rules_and_reports_paths_from_the_chosen_document()
    {
        var slot = new LayoutSlot("*", "*", "*", new LayoutNode("column", [Pane("bazaar.gc", "gc", "isOwner"), Pane("bazaar.dashboard", "dash", null)], null, null, null));
        var baked = new LayoutDocument(1, "Baked", [], [slot]);

        LayoutPlacement placement = LayoutSelection.Place(null, baked, "phone", "single", "folded", _ => true, Context());

        Assert.Equal(["dash"], placement.Root!.Children.Select(c => c.PaneId));
        Assert.Empty(placement.Issues);
    }

    [Fact]
    public void Selection_with_no_matching_slot_returns_no_root()
    {
        LayoutPlacement placement = LayoutSelection.Place(null, null, "phone", "single", "folded", _ => true, Context());

        Assert.Null(placement.Root);
    }
}
