using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.RyuseiFuda.Orrery;
using Bazaar.Client.RyuseiFuda.Panes;
using Bazaar.Client.Views.Tests.Fixtures;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Views.Tests;

/// <summary>The RyuseiFuda pane modules render from view documents when a head supplies a view context (doc 41c).</summary>
public class RyuseiPaneModuleViewTests
{
    private sealed class PlainFactory : IPaneViewFactory
    {
        public Control Create(IPaneViewModel pane) => new TextBlock { Text = "generic" };

        public Control WithTitle(string title, Control body) => body;
    }

    private sealed class Triggers : IRyuseiTriggerActions
    {
        public List<string> Calls { get; } = [];

        public Task PauseAsync(string triggerId, CancellationToken ct)
        {
            Calls.Add($"pause:{triggerId}");
            return Task.CompletedTask;
        }

        public Task ResumeAsync(string triggerId, CancellationToken ct)
        {
            Calls.Add($"resume:{triggerId}");
            return Task.CompletedTask;
        }
    }

    private const string _model = """
        { "triggers": [
          { "id": "t-1", "kind": "interval", "state": "active", "nextFire": "2026-11-20T08:00:00Z", "targetNames": ["Charles"], "purpose": "Standup" } ] }
        """;

    private static (PaneRegistry Registry, Triggers Triggers, Harness Head) Compose()
    {
        var head = new Harness();
        var triggers = new Triggers();
        var viewContext = new ViewContext(ViewFixtures.Theme(), head.Actions, new FakeLocalizer(), head.Errors.Add);
        var context = new PaneModuleContext().With(viewContext).With<IRyuseiTriggerActions>(triggers);
        var registry = PaneRegistry.Compose(new PlainFactory(), [new RyuseiSchedulePaneModule()], context);
        return (registry, triggers, head);
    }

    private static IEnumerable<Button> VisibleButtons(Control root) =>
        root.GetLogicalDescendants().OfType<Button>().Where(b => b.IsVisible);

    [AvaloniaFact]
    public async Task Pause_runs_the_trigger_action_and_the_row_then_offers_resume()
    {
        (PaneRegistry registry, Triggers triggers, Harness head) = Compose();
        using JsonDocument json = JsonDocument.Parse(_model);
        IPaneViewModel pane = registry.Create(new PaneDescriptor(PaneKinds.RyuseiSchedule, "Schedule", "ryusei.schedule", json.RootElement.Clone()));
        Control view = registry.CreateView(pane);

        Button pause = Assert.Single(VisibleButtons(view));
        Assert.Equal("Pause", pause.Content);
        pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(100);

        Assert.Equal(["pause:t-1"], triggers.Calls);
        Assert.Empty(head.Actions.Calls);
        Assert.Empty(head.Errors);
        Assert.Equal("Resume", Assert.Single(VisibleButtons(view)).Content);
    }

    [AvaloniaFact]
    public void Without_a_head_view_context_the_code_built_view_is_used()
    {
        var registry = PaneRegistry.Compose(new PlainFactory(), [new RyuseiSchedulePaneModule()], new PaneModuleContext());
        using JsonDocument json = JsonDocument.Parse(_model);
        IPaneViewModel pane = registry.Create(new PaneDescriptor(PaneKinds.RyuseiSchedule, "Schedule", "ryusei.schedule", json.RootElement.Clone()));

        Control view = registry.CreateView(pane);

        Assert.IsType<StackPanel>(view);
    }
}
