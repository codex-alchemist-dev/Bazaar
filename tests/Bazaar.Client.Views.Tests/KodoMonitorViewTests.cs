using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Bazaar.Client.Kodo;
using Bazaar.Client.Views.Tests.Fixtures;
using Bazaar.Rubedo.Views;

namespace Bazaar.Client.Views.Tests;

/// <summary>
/// The Kodo Monitor header as a view document (doc 46 sections 8.1, 12.1 and 14; doc 41c). The document validates, renders
/// through the real mapper with no failure card, shows exactly one state badge, and its controls follow the server's flags.
/// </summary>
public class KodoMonitorViewTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static ViewDocument Document() => ViewDocumentReader.Read(KodoMonitorData.ReadViewJson());

    private static IReadOnlyDictionary<string, object?> Data(
        KodoHealthState state,
        TimeSpan age,
        params KodoControlState[] controls) =>
        KodoMonitorData.From(KodoStatusViewModel.Build(new KodoStatusSnapshot(state, null, 90, _now - age, controls), _now));

    private static List<Button> Buttons(Control root) => ViewTree.Descendants(root).OfType<Button>().ToList();

    private static List<string> VisibleBadgeTexts(Control root) =>
        ViewTree.Descendants(root).OfType<Border>()
            .Where(b => b.IsVisible && b.Child is TextBlock)
            .Select(b => ((TextBlock)b.Child!).Text ?? string.Empty)
            .ToList();

    [Fact]
    public void The_view_document_validates_clean() =>
        Assert.Empty(ViewValidator.Validate(Document()));

    [AvaloniaTheory]
    [InlineData(KodoHealthState.Healthy, "Healthy")]
    [InlineData(KodoHealthState.Degraded, "Degraded")]
    [InlineData(KodoHealthState.Failing, "Failing")]
    [InlineData(KodoHealthState.Recovering, "Recovering")]
    [InlineData(KodoHealthState.Frozen, "Frozen")]
    public void Exactly_one_state_badge_shows_for_a_fresh_snapshot(KodoHealthState state, string word)
    {
        var harness = new Harness();

        Control root = harness.Build(Document(), data: Data(state, TimeSpan.FromSeconds(10)));

        Assert.Empty(ViewTree.Placeholders(root));
        Assert.Equal([word], VisibleBadgeTexts(root));
    }

    [AvaloniaFact]
    public void A_stale_snapshot_shows_as_unobserved_whatever_its_state()
    {
        var harness = new Harness();

        Control root = harness.Build(Document(), data: Data(KodoHealthState.Healthy, TimeSpan.FromMinutes(30)));

        Assert.Equal(["Unobserved"], VisibleBadgeTexts(root));
        Assert.Contains(ViewTree.Texts(root), t => t.StartsWith("Offline", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Only_the_controls_the_server_lists_are_visible_and_enabled_by_its_flag()
    {
        var harness = new Harness();
        var data = Data(
            KodoHealthState.Failing,
            TimeSpan.Zero,
            new KodoControlState(KodoControl.Restart, true, null),
            new KodoControlState(KodoControl.RollBack, false, "Frozen by incident mode"));

        Control root = harness.Build(Document(), data: data);

        Dictionary<string, Button> byLabel = Buttons(root).ToDictionary(b => (string)b.Content!, b => b);
        Assert.True(byLabel["Restart"].IsVisible);
        Assert.True(byLabel["Restart"].IsEnabled);
        Assert.True(byLabel["Roll back"].IsVisible);
        Assert.False(byLabel["Roll back"].IsEnabled);
        Assert.False(byLabel["Isolate"].IsVisible);
        Assert.False(byLabel["Exit incident mode"].IsVisible);
        Assert.Contains("Frozen by incident mode", ViewTree.Texts(root));
    }

    [AvaloniaFact]
    public void Clicking_a_control_invokes_its_command()
    {
        var harness = new Harness();
        Control root = harness.Build(Document(), data: Data(KodoHealthState.Degraded, TimeSpan.Zero, new KodoControlState(KodoControl.Isolate, true, null)));

        Button isolate = Buttons(root).Single(b => (string)b.Content! == "Isolate");
        isolate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(["invoke:kodo.isolate"], harness.Actions.Calls);
        Assert.Empty(harness.Errors);
    }
}
