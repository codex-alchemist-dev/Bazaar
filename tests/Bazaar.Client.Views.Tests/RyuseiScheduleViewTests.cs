using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Bazaar.Client.RyuseiFuda;
using Bazaar.Client.RyuseiFuda.Orrery;
using Bazaar.Client.RyuseiFuda.Panes;
using Bazaar.Client.Views.Tests.Fixtures;
using Bazaar.Contracts.Panes;
using Bazaar.Rubedo.Views;

namespace Bazaar.Client.Views.Tests;

/// <summary>The RyuseiFuda schedule pane as a view document (doc 41c): per-row Pause and Resume through button arguments.</summary>
public class RyuseiScheduleViewTests
{
    private const string Model = """
        { "triggers": [
          { "id": "t-1", "kind": "interval", "state": "active", "nextFire": "2026-11-20T08:00:00Z", "targetNames": ["Charles"], "purpose": "Standup" },
          { "id": "t-2", "kind": "timer", "state": "paused", "nextFire": null, "targetNames": [], "purpose": "Backup" },
          { "id": "t-3", "kind": "event", "state": "fired", "nextFire": null, "targetNames": [], "purpose": "Done" }
        ] }
        """;

    private sealed class NoopTriggerActions : IRyuseiTriggerActions
    {
        public Task PauseAsync(string triggerId, CancellationToken ct) => Task.CompletedTask;

        public Task ResumeAsync(string triggerId, CancellationToken ct) => Task.CompletedTask;
    }

    private static RyuseiSchedulePaneViewModel Pane(bool connected)
    {
        using JsonDocument json = JsonDocument.Parse(Model);
        var descriptor = new PaneDescriptor(PaneKinds.RyuseiSchedule, "Schedule", "ryusei.schedule", json.RootElement.Clone());
        return new RyuseiSchedulePaneViewModel(descriptor, connected ? new NoopTriggerActions() : null);
    }

    private static ViewDocument Document() => ViewDocumentReader.Read(RyuseiScheduleViewData.ReadViewJson());

    private static IEnumerable<Button> VisibleButtons(Control root) =>
        root.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible);

    [Fact]
    public void The_document_validates_clean()
    {
        Assert.Empty(ViewValidator.Validate(Document()));
    }

    [AvaloniaFact]
    public void Active_rows_offer_pause_and_paused_rows_offer_resume()
    {
        var harness = new Harness();

        Control root = harness.Build(Document(), data: RyuseiScheduleViewData.From(Pane(connected: true)));

        Assert.Equal(["Pause", "Resume"], VisibleButtons(root).Select(b => (string)b.Content!).OrderByDescending(x => x == "Pause"));
    }

    [AvaloniaFact]
    public void A_row_button_invokes_its_command_with_that_rows_id()
    {
        var harness = new Harness();
        Control root = harness.Build(Document(), data: RyuseiScheduleViewData.From(Pane(connected: true)));

        foreach (Button button in VisibleButtons(root).ToList())
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        Assert.Equal(
            ["invoke:ryusei.trigger.pause?id=t-1", "invoke:ryusei.trigger.resume?id=t-2"],
            harness.Actions.Calls.OrderBy(c => c, StringComparer.Ordinal).OrderByDescending(c => c.Contains("pause")));
    }

    [AvaloniaFact]
    public void Without_trigger_actions_no_button_shows()
    {
        var harness = new Harness();

        Control root = harness.Build(Document(), data: RyuseiScheduleViewData.From(Pane(connected: false)));

        Assert.Empty(VisibleButtons(root));
    }
}
