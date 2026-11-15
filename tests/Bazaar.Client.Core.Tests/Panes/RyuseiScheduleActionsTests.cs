using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Core.Ryusei;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Tests.Panes;

public class RyuseiScheduleActionsTests
{
    private const string _model = """
        {"triggers":[
          {"id":"b","kind":"interval","state":"active","nextFire":"2026-11-11T09:00:00+00:00","targetNames":["Charles"],"purpose":"Daily check"},
          {"id":"a","kind":"timer","state":"paused","nextFire":null,"targetNames":[],"purpose":"Paused wake"}
        ]}
        """;

    [Fact]
    public async Task Pause_changes_the_row_only_after_the_actions_succeed()
    {
        var actions = new FakeActions();
        var pane = new RyuseiSchedulePaneViewModel(PaneRegistryTests.Descriptor(PaneKinds.RyuseiSchedule, _model), actions);

        await pane.PauseAsync("b", TestContext.Current.CancellationToken);

        Assert.Equal(["pause:b"], actions.Calls);
        Assert.Null(pane.LastError);
        Assert.Equal("Paused", pane.Schedule.Rows.Single(r => r.Id == "b").StateLabel);
    }

    [Fact]
    public async Task A_refused_change_keeps_the_row_and_sets_a_plain_error()
    {
        var actions = new FakeActions { Refuse = true };
        var pane = new RyuseiSchedulePaneViewModel(PaneRegistryTests.Descriptor(PaneKinds.RyuseiSchedule, _model), actions);

        await pane.PauseAsync("b", TestContext.Current.CancellationToken);

        Assert.Equal("The change was not saved. Try again.", pane.LastError);
        Assert.Equal("Active", pane.Schedule.Rows.Single(r => r.Id == "b").StateLabel);
    }

    [Fact]
    public async Task Without_actions_the_change_is_refused_locally_and_nothing_moves()
    {
        var pane = new RyuseiSchedulePaneViewModel(PaneRegistryTests.Descriptor(PaneKinds.RyuseiSchedule, _model));

        await pane.ResumeAsync("a", TestContext.Current.CancellationToken);

        Assert.False(pane.CanChangeTriggers);
        Assert.Equal("Trigger changes are not connected to a server.", pane.LastError);
        Assert.Equal("Paused", pane.Schedule.Rows.Single(r => r.Id == "a").StateLabel);
    }

    [Fact]
    public void Module_hands_the_head_supplied_actions_to_the_pane()
    {
        var actions = new FakeActions();
        var registry = PaneRegistry.Compose(FirstPartyPanes.Modules, new PaneModuleContext().With<IRyuseiTriggerActions>(actions));

        var pane = Assert.IsType<RyuseiSchedulePaneViewModel>(registry.Create(PaneRegistryTests.Descriptor(PaneKinds.RyuseiSchedule, _model)));

        Assert.True(pane.CanChangeTriggers);
    }

    [AvaloniaFact]
    public void Pause_button_runs_the_action_and_the_row_then_offers_resume()
    {
        var actions = new FakeActions();
        var view = ScheduleView(actions);

        ClickButton(view, "Pause");

        Assert.Equal(["pause:b"], actions.Calls);
        var texts = view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Repeating interval · Paused", texts);
        var labels = view.GetLogicalDescendants().OfType<Button>().Select(b => b.Content as string).ToList();
        Assert.Contains("Resume", labels);
        Assert.DoesNotContain("Pause", labels);
    }

    [AvaloniaFact]
    public void Resume_button_on_a_paused_row_runs_the_resume_action()
    {
        var actions = new FakeActions();
        var view = ScheduleView(actions);

        ClickButton(view, "Resume");

        Assert.Equal(["resume:a"], actions.Calls);
        var texts = view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("One-time timer · Active", texts);
    }

    [AvaloniaFact]
    public void Buttons_are_disabled_when_no_head_supplied_actions()
    {
        var view = ScheduleView(actions: null);

        var buttons = view.GetLogicalDescendants().OfType<Button>().ToList();

        Assert.Equal(2, buttons.Count);
        Assert.All(buttons, b => Assert.False(b.IsEnabled));
    }

    [AvaloniaFact]
    public void A_refused_change_shows_the_error_and_keeps_the_row()
    {
        var actions = new FakeActions { Refuse = true };
        var view = ScheduleView(actions);

        ClickButton(view, "Pause");

        var texts = view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("The change was not saved. Try again.", texts);
        Assert.Contains("Repeating interval · Active", texts);
    }

    private static Control ScheduleView(IRyuseiTriggerActions? actions)
    {
        var context = actions is null ? new PaneModuleContext() : new PaneModuleContext().With(actions);
        var registry = PaneRegistry.Compose(FirstPartyPanes.Modules, context);
        var pane = registry.Create(PaneRegistryTests.Descriptor(PaneKinds.RyuseiSchedule, _model));
        return registry.CreateView(pane);
    }

    private static void ClickButton(Control view, string content)
    {
        var button = view.GetLogicalDescendants().OfType<Button>().Single(b => (b.Content as string) == content);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private sealed class FakeActions : IRyuseiTriggerActions
    {
        public List<string> Calls { get; } = [];

        public bool Refuse { get; init; }

        public Task PauseAsync(string triggerId, CancellationToken ct) => Record("pause", triggerId);

        public Task ResumeAsync(string triggerId, CancellationToken ct) => Record("resume", triggerId);

        private Task Record(string verb, string triggerId)
        {
            Calls.Add($"{verb}:{triggerId}");
            return Refuse ? Task.FromException(new InvalidOperationException("server refused")) : Task.CompletedTask;
        }
    }
}
