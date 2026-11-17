using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Bazaar.Accessibility.Avalonia;
using Bazaar.Client.Core.Constellation;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.RyuseiFuda.Orrery;
using Bazaar.Client.RyuseiFuda.Panes;
using Bazaar.Client.Core;
using Bazaar.Contracts.Panes;

namespace Bazaar.Accessibility.Gate.Tests;

// First-party panes that build their own controls (doc 49 section 2). Each window is shown and scanned, and the
// controls the test expects are checked as rendered, so an empty tree cannot pass.
public class PaneViewsAccessibilityTests
{
    private const string ScheduleModel = """
        {"triggers":[
          {"id":"b","kind":"interval","state":"active","nextFire":"2026-11-11T09:00:00+00:00","targetNames":["Charles"],"purpose":"Daily check"},
          {"id":"a","kind":"timer","state":"paused","nextFire":null,"targetNames":[],"purpose":"Paused wake"}
        ]}
        """;

    [AvaloniaFact]
    public void Constellation_pane_has_named_controls()
    {
        var pane = new ConstellationPaneViewModel(new PaneDescriptor(PaneKinds.Constellation, "Constellation", string.Empty, JsonElement.Parse("{}")));
        var window = new Window { Content = new ConstellationPaneView(pane) };
        window.Show();

        Assert.Empty(AccessibleControlScan.Find(window));
    }

    [AvaloniaFact]
    public void RyuseiFuda_schedule_pane_has_named_controls()
    {
        var registry = PaneRegistry.Compose(
            [new RyuseiSchedulePaneModule()],
            new PaneModuleContext().With<IRyuseiTriggerActions>(new IdleTriggerActions()));
        var descriptor = new PaneDescriptor(PaneKinds.RyuseiSchedule, "RyuseiFuda · Schedule", string.Empty, JsonElement.Parse(ScheduleModel));
        var window = new Window { Content = PaneHost.Build([descriptor], registry) };
        window.Show();

        // Not vacuous: the active trigger has a Pause button, which only exists once the rows render.
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Pause");
        Assert.Empty(AccessibleControlScan.Find(window));
    }

    private sealed class IdleTriggerActions : IRyuseiTriggerActions
    {
        public Task PauseAsync(string triggerId, CancellationToken ct) => Task.CompletedTask;

        public Task ResumeAsync(string triggerId, CancellationToken ct) => Task.CompletedTask;
    }
}
