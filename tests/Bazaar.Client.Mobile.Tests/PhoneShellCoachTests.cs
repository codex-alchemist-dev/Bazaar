using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.PhoneCoach;
using Bazaar.Client.Settings;

namespace Bazaar.Client.Mobile.Tests;

public class PhoneShellCoachTests
{
    private static ClientSettingsStore Store(string? path = null) => new(CoachController.Definitions, path);

    private static PhoneShell Shell(IClientSettings? settings, int panes = 3)
    {
        var registry = new PaneRegistry(TestPaneViews.Instance);
        string[] names = ["bazaar.first", "bazaar.second", "bazaar.third", "bazaar.fourth"];
        foreach (string name in names.Take(panes))
        {
            registry.Register(name, d => new GenericPaneViewModel(d));
        }

        var shell = new PhoneShell(registry, settings: settings, pageTitle: kinds => kinds[0].Replace("bazaar.", "", StringComparison.Ordinal));
        new Window { Content = shell, Width = 360, Height = 740 }.Show();
        return shell;
    }

    [AvaloniaFact]
    public void Strip_names_the_pages_and_follows_page_changes()
    {
        var shell = Shell(null);

        Assert.Equal("first", shell.Strip.State.Current);
        Assert.Equal(["first", "second", "third"], shell.Strip.State.Names.ToArray());
        Assert.False(shell.Peek.LeftVisible);
        Assert.True(shell.Peek.RightVisible);

        shell.GoToNext();
        Assert.Equal("second", shell.Strip.State.Current);
        Assert.True(shell.Peek.LeftVisible);
        Assert.True(shell.Peek.RightVisible);

        shell.GoToPage(2);
        Assert.Equal("third", shell.Strip.State.Current);
        Assert.False(shell.Peek.RightVisible);
    }

    [AvaloniaFact]
    public void Tapping_a_strip_name_changes_the_page()
    {
        var shell = Shell(null);

        shell.Strip.NameButtons[2].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(2, shell.SelectedPage);
    }

    [AvaloniaFact]
    public void Strip_follows_the_layout_when_panes_pair_up()
    {
        var shell = Shell(null, panes: 4);

        shell.ApplyPosture(PostureKind.Unfolded, HalfOpenLayoutPolicy.UnfoldedIfWide, 900);

        Assert.Equal(2, shell.Strip.State.Names.Count);
    }

    [AvaloniaFact]
    public void Coach_shows_once_and_dismiss_persists_across_a_restart()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shell-coach-{Guid.NewGuid():N}.json");
        try
        {
            var first = Shell(Store(path));
            Assert.NotNull(first.Coach);
            Assert.True(first.Coach!.IsShowing);

            first.Coach.Dismiss();

            var second = Shell(Store(path));
            Assert.False(second.Coach!.IsShowing);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void Coach_can_be_replayed_after_it_was_seen()
    {
        var shell = Shell(Store());
        shell.Coach!.Dismiss();

        shell.Coach.Replay();

        Assert.True(shell.Coach.IsShowing);
    }

    [AvaloniaFact]
    public void No_settings_means_no_coach()
    {
        Assert.Null(Shell(null).Coach);
    }

    [AvaloniaFact]
    public void Accessibility_actions_exist_for_each_gesture_and_drive_the_pager()
    {
        var shell = Shell(null);

        var actions = shell.AccessibilityActions;
        Assert.Equal(["Next chat", "Previous chat", "Open menu"], actions.Select(a => a.Label).ToArray());
        Assert.False(actions[1].Enabled);

        Assert.True(actions[0].Invoke());
        Assert.Equal(1, shell.SelectedPage);
        Assert.True(shell.AccessibilityActions[1].Enabled);
        Assert.True(shell.AccessibilityActions[0].Invoke());
        Assert.False(shell.AccessibilityActions[0].Enabled);
        Assert.True(shell.AccessibilityActions[1].Invoke());
        Assert.Equal(1, shell.SelectedPage);
    }

    [AvaloniaFact]
    public void Open_menu_action_asks_the_head_for_the_menu()
    {
        var shell = Shell(null);
        Assert.False(shell.AccessibilityActions.Single(a => a.Id == PagerActionIds.OpenMenu).Invoke());

        int requested = 0;
        shell.MenuRequested += (_, _) => requested++;

        Assert.True(shell.AccessibilityActions.Single(a => a.Id == PagerActionIds.OpenMenu).Invoke());
        Assert.Equal(1, requested);
    }

    [AvaloniaFact]
    public void Actions_changed_is_raised_when_the_page_changes()
    {
        var shell = Shell(null);
        int raised = 0;
        shell.AccessibilityActionsChanged += (_, _) => raised++;

        shell.GoToNext();

        Assert.True(raised > 0);
    }
}
