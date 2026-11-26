using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Bazaar.Accessibility.Avalonia;

namespace Bazaar.Client.Rubedo.Playground.Tests;

public class PlaygroundViewTests
{
    private static readonly string[] _identifiers =
        ["deviceClass", "display", "posture", "widthDp", "heightDp", "textScale", "reduceMotion", "isOwner", "colorScheme"];

    private static (Window Window, PlaygroundViewModel Vm) Show()
    {
        var vm = new PlaygroundViewModel();
        var window = new Window { Content = PlaygroundView.Build(vm), Width = 900, Height = 900 };
        window.Show();
        return (window, vm);
    }

    private static T Find<T>(Window window, string automationId)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(c => AutomationProperties.GetAutomationId(c) == automationId);

    [AvaloniaFact]
    public void View_renders_source_result_and_all_nine_context_controls()
    {
        var (window, _) = Show();

        Assert.Equal("deviceClass == \"phone\"", Find<TextBox>(window, PlaygroundView.SourceId).Text);
        Assert.Equal("false", Find<TextBox>(window, PlaygroundView.ResultId).Text);
        foreach (var identifier in _identifiers)
        {
            Assert.Contains(window.GetVisualDescendants().OfType<Control>(), c => AutomationProperties.GetAutomationId(c) == PlaygroundView.ContextId(identifier));
        }
    }

    [AvaloniaFact]
    public void Typing_updates_the_result()
    {
        var (window, _) = Show();

        Find<TextBox>(window, PlaygroundView.SourceId).Text = "2 * 21";

        Assert.Equal("42", Find<TextBox>(window, PlaygroundView.ResultId).Text);
    }

    [AvaloniaFact]
    public void Invalid_source_shows_a_diagnostic_with_its_code()
    {
        var (window, vm) = Show();

        Find<TextBox>(window, PlaygroundView.SourceId).Text = "1 +";

        var list = Find<ListBox>(window, PlaygroundView.DiagnosticsId);
        Assert.True(list.IsVisible);
        var row = Assert.IsType<TextBlock>(Assert.Single(list.Items.Cast<object>()));
        Assert.Contains(vm.Diagnostics[0].Code, row.Text, StringComparison.Ordinal);
        Assert.Equal(string.Empty, Find<TextBox>(window, PlaygroundView.ResultId).Text);
    }

    [AvaloniaFact]
    public void Device_class_control_changes_the_result()
    {
        var (window, _) = Show();
        Assert.Equal("false", Find<TextBox>(window, PlaygroundView.ResultId).Text);

        Find<ComboBox>(window, PlaygroundView.ContextId("deviceClass")).SelectedItem = "phone";

        Assert.Equal("true", Find<TextBox>(window, PlaygroundView.ResultId).Text);
    }

    [AvaloniaFact]
    public void Number_and_flag_controls_reach_the_context()
    {
        var (window, vm) = Show();

        Find<NumericUpDown>(window, PlaygroundView.ContextId("widthDp")).Value = 360;
        Find<CheckBox>(window, PlaygroundView.ContextId("isOwner")).IsChecked = false;

        Assert.Equal(360, vm.WidthDp);
        Assert.False(vm.IsOwner);
    }

    [AvaloniaFact]
    public void Format_button_rewrites_the_source()
    {
        var (window, vm) = Show();
        Find<TextBox>(window, PlaygroundView.SourceId).Text = "1+2";

        var format = Find<Button>(window, PlaygroundView.FormatId);
        format.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal("1 + 2", vm.Source);
        Assert.Equal("1 + 2", Find<TextBox>(window, PlaygroundView.SourceId).Text);
    }

    [AvaloniaFact]
    public void Mode_toggle_switches_to_view_documents_and_disables_format()
    {
        var (window, vm) = Show();

        Find<ToggleButton>(window, PlaygroundView.ModeId).IsChecked = true;

        Assert.Equal(PlaygroundMode.View, vm.Mode);
        Assert.False(Find<Button>(window, PlaygroundView.FormatId).IsEnabled);
        Assert.True(Find<ListBox>(window, PlaygroundView.DiagnosticsId).IsVisible);
    }

    [AvaloniaFact]
    public void Every_interactive_control_has_an_accessible_name_and_is_a_tab_stop()
    {
        var (window, _) = Show();

        Assert.Empty(AccessibleControlScan.Find(window));

        var names = window.GetVisualDescendants().OfType<Control>()
            .Where(c => c is not TextBlock && AutomationProperties.GetAutomationId(c)?.StartsWith("playground.", StringComparison.Ordinal) == true)
            .Select(c => AutomationProperties.GetName(c) ?? string.Empty)
            .ToList();
        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
        Assert.DoesNotContain(names, n => n.StartsWith('<'));
    }

    [AvaloniaFact]
    public void Labels_come_from_the_localisation_catalogue()
    {
        var (window, vm) = Show();

        Assert.Equal(vm.Text(PlaygroundText.ContextLabel("deviceClass")), AutomationProperties.GetName(Find<ComboBox>(window, PlaygroundView.ContextId("deviceClass"))));
        Assert.Equal("Format source", AutomationProperties.GetName(Find<Button>(window, PlaygroundView.FormatId)));
    }

    [AvaloniaFact]
    public void Focus_order_follows_reading_order()
    {
        var (window, _) = Show();

        var ids = window.GetVisualDescendants().OfType<Control>()
            .Select(AutomationProperties.GetAutomationId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToList();

        Assert.True(ids.IndexOf(PlaygroundView.ModeId) < ids.IndexOf(PlaygroundView.SourceId));
        Assert.True(ids.IndexOf(PlaygroundView.SourceId) < ids.IndexOf(PlaygroundView.ContextId("deviceClass")));
        Assert.True(ids.IndexOf(PlaygroundView.ContextId("colorScheme")) < ids.IndexOf(PlaygroundView.FormatId));
        Assert.True(ids.IndexOf(PlaygroundView.FormatId) < ids.IndexOf(PlaygroundView.ResultId));
    }
}
