using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Bazaar.Client.Kodo;
using Bazaar.Client.Views.Tests.Fixtures;
using Bazaar.Rubedo.Views;

namespace Bazaar.Client.Views.Tests;

/// <summary>The Kodo Audit and updates view as a view document (doc 46, section 14.2, view 8; doc 41c).</summary>
public class KodoEventsViewTests
{
    private static readonly DateTimeOffset _t = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);

    private static readonly KodoEventRow[] _events =
    [
        new("rec-1", _t, KodoEventKind.Audit, "Owner restarted the host", 41),
        new("rec-2", _t.AddMinutes(5), KodoEventKind.Update, "Channel stable moved to 0.1.1", 42),
    ];

    private static ViewDocument Document() => ViewDocumentReader.Read(KodoEventsViewData.ReadViewJson());

    private static List<Button> Buttons(Control root) => ViewTree.Descendants(root).OfType<Button>().ToList();

    private static List<string> Texts(Control root) =>
        ViewTree.Descendants(root).OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text ?? string.Empty).ToList();

    [Fact]
    public void The_document_validates_clean() => Assert.Empty(ViewValidator.Validate(Document()));

    [AvaloniaFact]
    public void Events_show_newest_first_with_kind_and_chain_position()
    {
        var harness = new Harness();

        Control root = harness.Build(Document(), data: KodoEventsViewData.From(_events, "No events."));

        Assert.Empty(ViewTree.Placeholders(root));
        List<string> texts = Texts(root);
        Assert.True(texts.IndexOf("Channel stable moved to 0.1.1") < texts.IndexOf("Owner restarted the host"));
        Assert.Contains("#42", texts);
        Assert.Contains("2026-11-20 08:05:00 UTC", texts);
        Assert.Contains("Update", texts);
        Assert.DoesNotContain("No events.", texts);
    }

    [AvaloniaFact]
    public void No_events_shows_the_empty_line_and_no_proof_buttons()
    {
        var harness = new Harness();

        Control root = harness.Build(Document(), data: KodoEventsViewData.From([], "No events."));

        Assert.Contains("No events.", Texts(root));
        Assert.Empty(Buttons(root));
    }

    [AvaloniaFact]
    public void Each_proof_button_asks_for_its_own_record()
    {
        var harness = new Harness();
        Control root = harness.Build(Document(), data: KodoEventsViewData.From(_events, "No events."));

        foreach (Button button in Buttons(root))
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        Assert.Equal(
            ["invoke:kodo.journal.proof?id=rec-2", "invoke:kodo.journal.proof?id=rec-1"],
            harness.Actions.Calls);
        Assert.Empty(harness.Errors);
    }
}
