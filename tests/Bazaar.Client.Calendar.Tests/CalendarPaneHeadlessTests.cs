using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Calendar.Panes;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.Calendar.Tests;

public class CalendarPaneHeadlessTests
{
    private const string _model = """
        {"displayTimezone":"viewer","events":[
          {"id":"a","kind":"vacation","title":"Sam away","startsAt":"2026-11-10","endsAt":"2026-11-11","allDay":true,"tz":"UTC","agentId":null,"projectId":null,"affectsAvailability":true,"status":"confirmed"},
          {"id":"b","kind":"release","title":"Ship 0.2","startsAt":"2026-11-20T09:00:00Z","endsAt":"2026-11-20T10:00:00Z","allDay":false,"tz":"UTC","agentId":null,"projectId":null,"affectsAvailability":false,"status":"cancelled"}
        ]}
        """;

    private static (PaneRegistry Registry, Control View) Build()
    {
        var mapper = CalendarTestSupport.Mapper(TimeZoneInfo.Utc);
        var registry = PaneRegistry.Compose(Bazaar.Client.Core.Panes.PaneViews.Instance, [new CalendarPaneModule(mapper)]);
        var pane = registry.Create(new Bazaar.Contracts.Panes.PaneDescriptor("bazaar.calendar", "Calendar", string.Empty, System.Text.Json.JsonElement.Parse(_model)));
        return (registry, registry.CreateView(pane));
    }

    [AvaloniaFact]
    public void Month_grid_shows_chips_with_kind_and_status_in_text()
    {
        var (_, view) = Build();
        var window = new Window { Content = view, Width = 900, Height = 700 };
        window.Show();

        var texts = view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("November 2026", texts);
        Assert.Equal(2, texts.Count(t => t == "Vacation: Sam away"));
        var cancelled = view.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Text == "Release: Ship 0.2 (cancelled)");
        Assert.Equal(TextDecorations.Strikethrough, cancelled.TextDecorations);

        var names = view.GetLogicalDescendants().OfType<Control>().Select(AutomationProperties.GetName).ToList();
        Assert.Contains("Release, Cancelled: Ship 0.2", names);
        Assert.Contains("Previous month", names);
        Assert.Contains("Next month", names);
        window.Close();
    }

    [AvaloniaFact]
    public void Next_month_button_changes_the_grid()
    {
        var (_, view) = Build();
        var window = new Window { Content = view, Width = 900, Height = 700 };
        window.Show();

        var next = view.GetLogicalDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Next month");
        next.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        var texts = view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("December 2026", texts);
        Assert.DoesNotContain("Vacation: Sam away", texts);
        window.Close();
    }
}
