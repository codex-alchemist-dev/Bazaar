using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Core.Ryusei;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Tests.Panes;

public class RyuseiTaskChipTests
{
    private const string _cardId = "0196a6f0-0000-7000-8000-000000000001";

    private const string _board = """
        {"cards":[
          {"id":"0196a6f0-0000-7000-8000-000000000001","title":"Schema","state":"ready",
           "triggers":[
             {"id":"t2","kind":"event","state":"fired","nextFire":null,"purpose":"Already ran"},
             {"id":"t1","kind":"timer","state":"active","nextFire":"2026-11-13T09:00:00+00:00","purpose":"Check schema review"},
             {"id":"t3","kind":"interval","state":"paused","nextFire":null,"purpose":"Weekly sweep"},
             {"id":"bad","kind":"timer","state":"active","nextFire":"soon","purpose":"Malformed fire time"}
           ]},
          {"id":"0196a6f0-0000-7000-8000-000000000002","title":"Plain","state":"backlog"}
        ]}
        """;

    [Fact]
    public void Visible_keeps_only_live_chips_and_orders_by_next_fire_with_no_fire_last()
    {
        var chips = new[]
        {
            new RyuseiTaskChip("c", "interval", "paused", null, "No fire"),
            new RyuseiTaskChip("b", "timer", "active", DateTimeOffset.Parse("2026-11-13T10:00:00+00:00", CultureInfo.InvariantCulture), "Later"),
            new RyuseiTaskChip("a", "timer", "active", DateTimeOffset.Parse("2026-11-13T09:00:00+00:00", CultureInfo.InvariantCulture), "Sooner"),
            new RyuseiTaskChip("d", "event", "fired", null, "Terminal"),
            new RyuseiTaskChip("e", "watcher", "cancelled", null, "Terminal"),
        };

        var visible = RyuseiChipRules.Visible(chips);

        Assert.Equal(["a", "b", "c"], visible.Select(c => c.TriggerId));
    }

    [Fact]
    public void Status_names_kind_state_and_the_server_offset()
    {
        var chip = new RyuseiTaskChip("t", "interval", "active", DateTimeOffset.Parse("2026-11-13T09:00:00+00:00", CultureInfo.InvariantCulture), "p");

        Assert.Equal("Interval · Active · Next 2026-11-13 09:00 +00:00", RyuseiChipRules.Status(chip));
        Assert.Equal("Timer · Paused", RyuseiChipRules.Status(new RyuseiTaskChip("t", "timer", "paused", null, "p")));
    }

    [Fact]
    public void Purpose_is_cut_to_the_chip_limit_with_an_ellipsis()
    {
        var chip = new RyuseiTaskChip("t", "timer", "active", null, new string('x', 100));

        var text = RyuseiChipRules.Purpose(chip);

        Assert.Equal(RyuseiChipRules.MaxPurposeLength, text.Length);
        Assert.EndsWith("…", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Card_chips_read_only_live_triggers_and_skip_a_malformed_one()
    {
        var pane = new KanbanPaneViewModel(PaneRegistryTests.Descriptor(PaneKinds.Kanban, _board));

        var chips = pane.ChipsFor(Guid.Parse(_cardId));

        Assert.Equal(["t1", "t3"], chips.Select(c => c.TriggerId));
        Assert.Empty(pane.ChipsFor(Guid.Parse("0196a6f0-0000-7000-8000-000000000002")));
    }

    [AvaloniaFact]
    public void Board_card_shows_its_trigger_chips_with_their_status_line()
    {
        var registry = FirstPartyPanes.CreateRegistry();
        var view = registry.CreateView(registry.Create(PaneRegistryTests.Descriptor(PaneKinds.Kanban, _board)));
        var texts = view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

        Assert.Contains("Schema", texts);
        Assert.Contains("Timer · Active · Next 2026-11-13 09:00 +00:00", texts);
        Assert.Contains("Check schema review", texts);
        Assert.Contains("Interval · Paused", texts);
        Assert.DoesNotContain(texts, t => t?.StartsWith("Event", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Board_pane_title_reads_RyuseiFuda_Board()
    {
        var board = FirstPartyPanes.WindowPanes().Single(p => p.Kind == PaneKinds.Kanban);

        Assert.Equal("RyuseiFuda · Board", board.Title);
    }
}
