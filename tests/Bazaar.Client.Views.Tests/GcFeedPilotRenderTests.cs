using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Client.Views.Tests.Fixtures;
using Bazaar.Rubedo.Views;

namespace Bazaar.Client.Views.Tests;

/// <summary>
/// The GC feed pilot (doc 41c, section 10.4) rendered by the Avalonia head. The document is the same file the Rubedo golden
/// corpus validates, so a change to the pilot is checked by the validator and by this mapping.
/// </summary>
public class GcFeedPilotRenderTests
{
    private static ViewDocument Pilot() =>
        ViewDocumentReader.Read(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "pilots", "gc-feed.view.json")));

    private static Dictionary<string, object?> Feed(decimal mentions) => ViewFixtures.Data(
        ("unreadGcs", 3m),
        ("mentions", mentions),
        ("gcs", new List<IReadOnlyDictionary<string, object?>>
        {
            ViewFixtures.Data(("id", "g1"), ("name", "General"), ("unread", 2m), ("mentioned", true)),
            ViewFixtures.Data(("id", "g2"), ("name", "Build"), ("unread", 0m), ("mentioned", false)),
        }));

    private static IEnumerable<T> Of<T>(Control root)
        where T : Control => root.GetLogicalDescendants().OfType<T>();

    [AvaloniaFact]
    public void The_pilot_renders_with_no_placeholder()
    {
        var harness = new Harness();

        Control root = harness.Build(Pilot(), data: Feed(mentions: 1m));

        string[] failures = Of<Border>(root).Where(b => b.Name == ViewMapper.PlaceholderName)
            .Select(b => string.Concat("[", ((TextBlock)b.Child!).Text, "]")).ToArray();
        Assert.True(failures.Length == 0, string.Join("\n", failures));
        Assert.Empty(harness.Errors);
    }

    [AvaloniaFact]
    public void The_pilot_shows_one_row_per_gc_with_its_name()
    {
        var harness = new Harness();

        Control root = harness.Build(Pilot(), data: Feed(mentions: 1m));

        string[] texts = Of<TextBlock>(root).Select(t => t.Text ?? string.Empty).ToArray();
        Assert.Contains("General", texts);
        Assert.Contains("Build", texts);
    }

    [AvaloniaFact]
    public void A_badge_shows_only_while_its_visibleWhen_is_true()
    {
        var harness = new Harness();

        Control withMentions = harness.Build(Pilot(), data: Feed(mentions: 2m));
        Control without = harness.Build(Pilot(), data: Feed(mentions: 0m));

        // The first badge is the mentions chip in the heading row.
        Assert.True(Of<Border>(withMentions).First(b => b.Name != ViewMapper.PlaceholderName && b.Child is TextBlock).IsVisible);
        Assert.False(Of<Border>(without).First(b => b.Name != ViewMapper.PlaceholderName && b.Child is TextBlock).IsVisible);
    }

    [AvaloniaFact]
    public void An_open_button_runs_the_navigate_action()
    {
        var harness = new Harness();
        Control root = harness.Build(Pilot(), data: Feed(mentions: 0m));

        Button open = Of<Button>(root).First();
        open.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(["navigate:gc"], harness.Actions.Calls);
    }
}
