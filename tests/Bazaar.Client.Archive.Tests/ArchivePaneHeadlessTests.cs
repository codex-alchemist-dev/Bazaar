using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Archive.Panes;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.Archive.Tests;

public class ArchivePaneHeadlessTests
{
    private const string _book = """
        {
          "chapters": [
            {
              "id": "ch-1", "title": "Launch", "version": 1, "predecessorId": null,
              "sentences": [
                { "text": "The team shipped the first build.", "sourceRef": "gc_message:m-1" },
                { "text": "Nobody recorded where this came from.", "sourceRef": null }
              ]
            },
            {
              "id": "ch-2", "title": "Second look", "version": 2, "predecessorId": "ch-1",
              "sentences": [ { "text": "The build was re-tested.", "sourceRef": "gc_event:e-9" } ]
            }
          ]
        }
        """;

    private static Window Show(ArchivePaneViewModel archive)
    {
        var view = new ArchivePaneModule();
        var registry = PaneRegistry.Compose(PaneViews.Instance, [view]);
        var window = new Window { Width = 800, Height = 900, Content = registry.CreateView(archive) };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return window;
    }

    [AvaloniaFact]
    public void Book_shows_each_sentence_with_its_source_and_an_explicit_missing_source_link()
    {
        var window = Show(ArchivePaneViewModelTests.Book(_book));

        var links = window.GetVisualDescendants().OfType<HyperlinkButton>().Select(l => (string?)l.Content).ToList();
        Assert.Contains("gc_message:m-1", links);
        Assert.Contains("missing source", links);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "The team shipped the first build.");
    }

    [AvaloniaFact]
    public void Next_button_moves_to_the_next_chapter_and_shows_its_predecessor_link()
    {
        var archive = ArchivePaneViewModelTests.Book(_book);
        var window = Show(archive);

        var next = window.GetVisualDescendants().OfType<Button>().Single(b => (string?)b.Content == "Next");
        next.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.Equal("ch-2", archive.SelectedChapter?.Id);
        Assert.Contains(window.GetVisualDescendants().OfType<HyperlinkButton>(), l => (string?)l.Content == "Regenerates Launch");
        var nextAfter = window.GetVisualDescendants().OfType<Button>().Single(b => (string?)b.Content == "Next");
        Assert.False(nextAfter.IsEnabled);
    }
}
