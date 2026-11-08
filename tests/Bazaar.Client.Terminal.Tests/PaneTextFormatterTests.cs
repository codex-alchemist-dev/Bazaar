using System.Text.Json;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Terminal.Tests;

public sealed class PaneTextFormatterTests
{
    private static readonly PaneRegistry _registry = FirstPartyPanes.CreateRegistry();

    [Fact]
    public void Kanban_lists_each_column_with_its_cards_and_blocked_marking()
    {
        var descriptor = new PaneDescriptor(PaneKinds.Kanban, "Board", string.Empty, JsonElement.Parse("""
            {"cards":[
              {"id":"0196a6f0-0000-7000-8000-000000000001","title":"Schema","state":"ready","unfinishedBlockerIds":["0196a6f0-0000-7000-8000-000000000009"],"blockerTitles":["Other"]},
              {"id":"0196a6f0-0000-7000-8000-000000000002","title":"Codec","state":"in_progress"}
            ]}
            """));

        var lines = PaneTextFormatter.Format(descriptor, _registry);

        Assert.Equal("Board", lines[0]);
        Assert.Contains("  Ready (1)", lines);
        Assert.Contains("    - Schema [Blocked]", lines);
        Assert.Contains("  In progress (1)", lines);
        Assert.Contains("    - Codec", lines);
        Assert.Contains("  Done (0)", lines);
    }

    [Fact]
    public void Dashboard_lists_sections_and_items()
    {
        var descriptor = new PaneDescriptor(PaneKinds.Dashboard, "Start", string.Empty, JsonElement.Parse("""
            {"sections":[{"title":"Awaiting you","items":[{"label":"Approvals","value":2}]}]}
            """));

        var lines = PaneTextFormatter.Format(descriptor, _registry);

        Assert.Equal(["Start", "  Awaiting you", "    Approvals: 2"], lines);
    }

    [Fact]
    public void Unknown_kind_renders_generic_lines()
    {
        var descriptor = new PaneDescriptor("godot.scene-tree", "Scene", string.Empty, JsonElement.Parse("{\"name\":\"Main\"}"));

        var lines = PaneTextFormatter.Format(descriptor, _registry);

        Assert.Equal(["Scene", "  {", "    name: Main"], lines);
    }
}
