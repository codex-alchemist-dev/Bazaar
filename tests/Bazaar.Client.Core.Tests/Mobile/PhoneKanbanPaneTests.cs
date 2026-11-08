using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Client.Core.Mobile;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Tests.Mobile;

public class PhoneKanbanPaneTests
{
    [AvaloniaFact]
    public void Gc_page_renders_the_empty_state_with_no_session()
    {
        var registry = FirstPartyPanes.CreateRegistry();
        var shell = new PhoneShell(registry, FirstPartyPanes.PhonePaneBodies(registry));
        var window = new Window { Content = shell, Width = 360, Height = 740 };
        window.Show();

        Assert.Contains("No group chat is connected.", ShownText(window));
    }

    [AvaloniaFact]
    public void Kanban_page_renders_the_board_with_no_snapshot()
    {
        var registry = FirstPartyPanes.CreateRegistry();
        var shell = new PhoneShell(registry, FirstPartyPanes.PhonePaneBodies(registry));
        var window = new Window { Content = shell, Width = 360, Height = 740 };
        window.Show();

        var texts = ShownText(window);
        Assert.Contains("Kanban", texts);
        Assert.Contains("Backlog (0)", texts);
        Assert.Contains("Done (0)", texts);
        Assert.DoesNotContain(PaneKinds.Kanban, texts);
    }

    [AvaloniaFact]
    public void Kanban_page_renders_the_guid_parsed_snapshot()
    {
        var model = JsonElement.Parse("""
            {"cards":[{"id":"0196a6f0-0000-7000-8000-000000000001","title":"Schema","state":"ready",
            "ownerAgentId":null,"unfinishedBlockerIds":["0196a6f0-0000-7000-8000-000000000009"],"blockerTitles":["Other"]}]}
            """);
        var registry = FirstPartyPanes.CreateRegistry();
        var shell = new PhoneShell(registry, FirstPartyPanes.PhonePaneBodies(registry, model));
        var window = new Window { Content = shell, Width = 360, Height = 740 };
        window.Show();

        var texts = ShownText(window);
        Assert.Contains("Ready (1)", texts);
        Assert.Contains("Schema [Blocked]", texts);
    }

    [AvaloniaFact]
    public void Kanban_page_with_a_malformed_snapshot_falls_back_to_the_generic_view()
    {
        var model = JsonElement.Parse("""{"tasks":[]}""");
        var registry = FirstPartyPanes.CreateRegistry(report: _ => { });
        var shell = new PhoneShell(registry, FirstPartyPanes.PhonePaneBodies(registry, model));
        var window = new Window { Content = shell, Width = 360, Height = 740 };
        window.Show();

        Assert.DoesNotContain("Backlog (0)", ShownText(window));
    }

    [Fact]
    public void Other_kinds_get_no_body_so_they_keep_the_placeholder()
    {
        var registry = FirstPartyPanes.CreateRegistry();
        var bodies = FirstPartyPanes.PhonePaneBodies(registry);

        Assert.Null(bodies(PaneKinds.Dashboard));
    }

    private static List<string> ShownText(Window window) =>
        window.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
}
