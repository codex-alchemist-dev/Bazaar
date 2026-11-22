using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Accessibility.Avalonia;
using Bazaar.Client.Kodo.Live;
using Microsoft.Extensions.Time.Testing;
using static Bazaar.Client.Kodo.Tests.Panes.KodoTestFrames;

namespace Bazaar.Client.Kodo.Tests.Panes;

public class KodoViewsHeadlessTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static IEnumerable<string> Texts(Control root) =>
        root.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty);

    [AvaloniaFact]
    public void The_status_view_shows_the_state_word_score_and_permitted_controls_with_names()
    {
        var time = new FakeTimeProvider(_now);
        var session = new KodoStreamSession(time);
        session.Apply(Snapshot(1, "failing"));
        var model = new KodoStatusPaneViewModel(session, new KodoAllPermissions()) { Controls = [Control(KodoControl.Restart), Control(KodoControl.RollBack, false, "Blocked")], Extras = new KodoStatusExtras(3, [new KodoScoreCap("A critical probe is failing", 49)]) };
        var view = new KodoStatusView(model);
        var window = new Window { Content = view };
        window.Show();

        var texts = Texts(view).ToList();
        Assert.Contains("Failing", texts);
        Assert.Contains("Score 90 of 100", texts);
        Assert.Contains("Capped at 49: A critical probe is failing", texts);
        Assert.Contains("3 open alerts", texts);
        var buttons = view.GetLogicalDescendants().OfType<Button>().ToList();
        Assert.Equal(2, buttons.Count);
        Assert.False(buttons[1].IsEnabled);
        Assert.Empty(AccessibleControlScan.Find(window));
    }

    [AvaloniaFact]
    public void The_status_view_turns_unobserved_after_silence_on_refresh()
    {
        var time = new FakeTimeProvider(_now);
        var session = new KodoStreamSession(time);
        session.Apply(Snapshot(1));
        var view = new KodoStatusView(new KodoStatusPaneViewModel(session, new KodoAllPermissions()));
        Assert.Contains("Healthy", Texts(view));

        time.Advance(TimeSpan.FromMinutes(2));
        view.Refresh();

        var texts = Texts(view).ToList();
        Assert.Contains("Unobserved", texts);
        Assert.DoesNotContain("Healthy", texts);
        Assert.Contains("Offline: last event 2 min ago", texts);
    }

    [AvaloniaFact]
    public void The_status_view_hides_everything_without_the_grant()
    {
        var session = new KodoStreamSession(new FakeTimeProvider(_now));
        session.Apply(Snapshot(1));
        var view = new KodoStatusView(new KodoStatusPaneViewModel(session, new KodoNoPermissions()) { Controls = [Control(KodoControl.Restart)] });

        Assert.Empty(view.GetLogicalDescendants().OfType<Button>());
        Assert.Contains("Kodo status is not available for your role.", Texts(view));
    }

    [AvaloniaFact]
    public void Clicking_an_enabled_control_raises_the_request()
    {
        var session = new KodoStreamSession(new FakeTimeProvider(_now));
        session.Apply(Snapshot(1));
        var model = new KodoStatusPaneViewModel(session, new KodoAllPermissions()) { Controls = [Control(KodoControl.Restart)] };
        var raised = new List<KodoControl>();
        model.ControlRequested += (_, c) => raised.Add(c);
        var view = new KodoStatusView(model);
        var window = new Window { Content = view };
        window.Show();

        view.GetLogicalDescendants().OfType<Button>().Single().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal([KodoControl.Restart], raised);
    }

    [AvaloniaFact]
    public void The_fleet_view_lists_devices_in_attention_order_and_changes_sort_from_the_selector()
    {
        var model = new KodoFleetPaneViewModel(new FakeTimeProvider(_now), new KodoAllPermissions());
        model.SetDevices(
        [
            new KodoFleetDeviceInput("1", "Zed", "0.2.0", "0.2.0", _now, 0, "connected"),
            new KodoFleetDeviceInput("2", "Amy", "0.1.0", "0.2.0", _now, 4, "disconnected"),
        ]);
        var view = new KodoFleetView(model);
        var window = new Window { Content = view };
        window.Show();

        var list = view.GetLogicalDescendants().OfType<ListBox>().Single();
        Assert.Equal(2, list.ItemCount);
        var names = list.Items.OfType<ListBoxItem>().Select(i => AutomationProperties.GetName(i)).ToList();
        Assert.StartsWith("Amy:", names[0]);
        Assert.StartsWith("Zed:", names[1]);

        view.GetLogicalDescendants().OfType<ComboBox>().Single().SelectedIndex = 1;

        Assert.Equal(KodoFleetSort.Name, model.Sort);
        Assert.Empty(AccessibleControlScan.Find(window));
    }

    [AvaloniaFact]
    public void The_fleet_view_shows_the_empty_text_and_respects_the_grant()
    {
        var empty = new KodoFleetView(new KodoFleetPaneViewModel(new FakeTimeProvider(_now), new KodoAllPermissions()));
        Assert.Contains("No devices reported.", Texts(empty));

        var denied = new KodoFleetView(new KodoFleetPaneViewModel(new FakeTimeProvider(_now), new KodoNoPermissions()));
        Assert.Contains("Fleet is not available for your role.", Texts(denied));
    }
}
