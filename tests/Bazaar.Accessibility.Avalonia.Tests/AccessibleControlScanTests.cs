using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Bazaar.Accessibility.Avalonia;

namespace Bazaar.Accessibility.Avalonia.Tests;

public class AccessibleControlScanTests
{
    [AvaloniaFact]
    public void A_fully_named_window_has_no_problems()
    {
        var window = new Window
        {
            Content = new StackPanel
            {
                Children =
                {
                    new Button { [AutomationProperties.NameProperty] = "Send message" },
                    new TextBox { [AutomationProperties.NameProperty] = "Message text" },
                },
            },
        };
        window.Show();

        Assert.Empty(AccessibleControlScan.Find(window));
    }

    [AvaloniaFact]
    public void An_unnamed_button_is_reported_even_though_its_visible_text_is_set()
    {
        var button = new Button { Name = "sendButton", Content = "Send" };
        var window = new Window { Content = button };
        window.Show();

        AccessibleControlProblem problem = Assert.Single(AccessibleControlScan.Find(window));
        Assert.Equal("Button", problem.ControlType);
        Assert.Equal("sendButton", problem.Name);
        Assert.Equal("The control has no accessible name.", problem.Problem);
    }

    [AvaloniaFact]
    public void An_unnamed_list_box_is_reported()
    {
        var list = new ListBox { Name = "lines", ItemsSource = new[] { "one" } };
        var window = new Window { Content = list };
        window.Show();

        AccessibleControlProblem problem = Assert.Single(AccessibleControlScan.Find(window));
        Assert.Equal("ListBox", problem.ControlType);
    }

    [AvaloniaFact]
    public void A_placeholder_name_is_reported()
    {
        var box = new TextBox { [AutomationProperties.NameProperty] = "button" };
        var window = new Window { Content = box };
        window.Show();

        AccessibleControlProblem problem = Assert.Single(AccessibleControlScan.Find(window));
        Assert.Contains("placeholder", problem.Problem);
    }

    [AvaloniaFact]
    public void Non_interactive_controls_are_not_checked()
    {
        var window = new Window { Content = new TextBlock { Text = "Hello" } };
        window.Show();

        Assert.Empty(AccessibleControlScan.Find(window));
    }

    [AvaloniaFact]
    public void A_button_that_cannot_take_focus_is_reported()
    {
        var button = new Button { Name = "skipped", Focusable = false, [AutomationProperties.NameProperty] = "Skipped" };
        var window = new Window { Content = button };
        window.Show();

        AccessibleControlProblem problem = Assert.Single(AccessibleControlScan.Find(window));
        Assert.Equal(AccessibleControlScan.KeyboardProblem, problem.Problem);
    }

    [AvaloniaFact]
    public void A_button_outside_the_tab_order_is_reported()
    {
        var button = new Button { [AutomationProperties.NameProperty] = "Out of order" };
        KeyboardNavigation.SetIsTabStop(button, false);
        var window = new Window { Content = button };
        window.Show();

        Assert.Single(AccessibleControlScan.Find(window));
    }

    [AvaloniaFact]
    public void A_disabled_unfocusable_button_is_not_reported()
    {
        var button = new Button { IsEnabled = false, Focusable = false, [AutomationProperties.NameProperty] = "Off" };
        var window = new Window { Content = button };
        window.Show();

        Assert.Empty(AccessibleControlScan.Find(window));
    }
}
