using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Bazaar.Client.Kodo;
using Bazaar.Client.Views.Tests.Fixtures;
using Bazaar.Rubedo.Views;

namespace Bazaar.Client.Views.Tests;

/// <summary>The Kodo alerts view as a view document (doc 46, sections 14.2 and 16.1; doc 41c).</summary>
public class KodoAlertsViewTests
{
    private static readonly DateTimeOffset _t = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);

    private static readonly KodoAlertRow[] _alerts =
    [
        new("host.disk.free:data", KodoAlertSeverity.Warning, "kodo.rule.threshold", _t, true, false),
        new("db.replica.lag:main", KodoAlertSeverity.Critical, "kodo.rule.trend", _t.AddMinutes(-5), true, false),
        new("gateway.lane:free", KodoAlertSeverity.Info, "kodo.rule.threshold", _t.AddMinutes(-9), false, false),
        new("host.cert.expiry:web", KodoAlertSeverity.Warning, "kodo.rule.expiry", _t.AddMinutes(-20), true, true),
    ];

    private static ViewDocument Document() => ViewDocumentReader.Read(KodoAlertsViewData.ReadViewJson());

    private static List<Button> VisibleButtons(Control root) =>
        ViewTree.Descendants(root).OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();

    private static List<string> VisibleTexts(Control root) =>
        ViewTree.Descendants(root).OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToList();

    [Fact]
    public void The_document_validates_clean() => Assert.Empty(ViewValidator.Validate(Document()));

    [AvaloniaFact]
    public void The_most_severe_alert_comes_first_and_shows_one_severity_badge_per_row()
    {
        var harness = new Harness();

        Control root = harness.Build(Document(), data: KodoAlertsViewData.From(_alerts, "No alerts."));

        Assert.Empty(ViewTree.Placeholders(root));
        List<string> texts = VisibleTexts(root);
        Assert.Equal("Critical", texts.First(t => t is "Critical" or "Warning" or "Info"));
        Assert.Equal(1, texts.Count(t => t == "Critical"));
        Assert.Equal(2, texts.Count(t => t == "Warning"));
        Assert.Equal(1, texts.Count(t => t == "Info"));
        Assert.Contains("kodo.rule.trend", texts);
        Assert.DoesNotContain("No alerts.", texts);
    }

    [AvaloniaFact]
    public void Only_alerts_the_viewer_may_acknowledge_and_that_are_open_offer_a_button()
    {
        var harness = new Harness();
        Control root = harness.Build(Document(), data: KodoAlertsViewData.From(_alerts, "No alerts."));

        List<Button> buttons = VisibleButtons(root);
        foreach (Button button in buttons)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        Assert.Equal(
            ["invoke:kodo.alert.ack?id=db.replica.lag:main", "invoke:kodo.alert.ack?id=host.disk.free:data"],
            harness.Actions.Calls);
        Assert.Contains("Acknowledged", VisibleTexts(root));
        Assert.Empty(harness.Errors);
    }

    [AvaloniaFact]
    public void No_alerts_shows_the_empty_line()
    {
        var harness = new Harness();

        Control root = harness.Build(Document(), data: KodoAlertsViewData.From([], "No alerts."));

        Assert.Contains("No alerts.", VisibleTexts(root));
        Assert.Empty(VisibleButtons(root));
    }
}
