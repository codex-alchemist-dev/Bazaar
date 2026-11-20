using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bazaar.Client.Rubedo.Playground;

/// <summary>
/// The playground pane body (doc 41d section 7). Built in code like the other first-party pane bodies. Every control has an
/// accessible name from a localisation key, the focus order is the reading order (mode, source, context, format, result,
/// diagnostics), nothing has a fixed height or font size so the head's text scale applies, and colours come from theme tokens.
/// </summary>
public static class PlaygroundView
{
    /// <summary>Automation ids the tests (and screen-reader scripts) look controls up by.</summary>
    public const string SourceId = "playground.source";
    public const string ModeId = "playground.mode";
    public const string FormatId = "playground.format";
    public const string ResultId = "playground.result";
    public const string DiagnosticsId = "playground.diagnostics";
    public const string FormatMessageId = "playground.format-message";

    private const string _secondaryTextToken = "Bazaar.brush.color.text.secondary";

    /// <summary>The automation id of the context control for an identifier of <c>RubedoSchema.Standard</c>.</summary>
    public static string ContextId(string identifier) => "playground.context." + identifier;

    public static Control Build(PlaygroundViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);

        var mode = new ToggleButton { Content = vm.Text(PlaygroundText.ModeView), IsChecked = vm.Mode == PlaygroundMode.View };
        Name(mode, ModeId, vm.Text(PlaygroundText.ModeLabel));
        mode.IsCheckedChanged += (_, _) => vm.Mode = mode.IsChecked == true ? PlaygroundMode.View : PlaygroundMode.Expression;

        var source = new TextBox
        {
            Text = vm.Source,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinLines = 3,
            FontFamily = new FontFamily("monospace"),
        };
        Name(source, SourceId, vm.Text(PlaygroundText.SourceLabel));
        source.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                vm.Source = source.Text ?? string.Empty;
            }
        };

        var context = ContextControls(vm);

        var format = new Button { Content = vm.Text(PlaygroundText.FormatLabel), HorizontalAlignment = HorizontalAlignment.Left };
        Name(format, FormatId, vm.Text(PlaygroundText.FormatLabel));
        format.Click += (_, _) => vm.Format();

        var formatMessage = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(formatMessage, FormatMessageId);
        AutomationProperties.SetLiveSetting(formatMessage, AutomationLiveSetting.Polite);

        // Read-only text box rather than a TextBlock, so a keyboard user can reach the result and copy it.
        var result = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Text = vm.ResultText };
        Name(result, ResultId, vm.Text(PlaygroundText.ResultLabel));
        AutomationProperties.SetLiveSetting(result, AutomationLiveSetting.Polite);

        var diagnostics = new ListBox();
        Name(diagnostics, DiagnosticsId, vm.Text(PlaygroundText.DiagnosticsLabel));
        AutomationProperties.SetLiveSetting(diagnostics, AutomationLiveSetting.Polite);

        void Refresh()
        {
            if (!string.Equals(source.Text, vm.Source, StringComparison.Ordinal))
            {
                source.Text = vm.Source;
            }

            result.Text = vm.ResultText;
            diagnostics.ItemsSource = vm.Diagnostics.Select(DiagnosticRow).ToArray();
            diagnostics.IsVisible = vm.HasDiagnostics;
            format.IsEnabled = vm.CanFormat;
            formatMessage.Text = vm.FormatMessage;
            formatMessage.IsVisible = vm.FormatMessage.Length > 0;
            mode.IsChecked = vm.Mode == PlaygroundMode.View;
        }

        vm.PropertyChanged += OnChanged;

        void OnChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

        Refresh();

        var body = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(16),
            Children =
            {
                mode,
                Labelled(vm.Text(PlaygroundText.SourceLabel), source),
                Heading(vm.Text(PlaygroundText.ContextHeading)),
                context,
                format,
                formatMessage,
                Labelled(vm.Text(PlaygroundText.ResultLabel), result),
                diagnostics,
            },
        };

        // Detach on unload so a closed pane does not keep the view alive through the view model.
        body.DetachedFromVisualTree += (_, _) => vm.PropertyChanged -= OnChanged;
        body.AttachedToVisualTree += (_, _) =>
        {
            vm.PropertyChanged -= OnChanged;
            vm.PropertyChanged += OnChanged;
            Refresh();
        };

        return new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    /// <summary>The nine controls, one per identifier of <c>RubedoSchema.Standard</c>, wrapping so a large text scale still fits.</summary>
    private static WrapPanel ContextControls(PlaygroundViewModel vm)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(Choice(vm, "deviceClass", PlaygroundViewModel.DeviceClasses, () => vm.DeviceClass, v => vm.DeviceClass = v));
        panel.Children.Add(Choice(vm, "display", PlaygroundViewModel.Displays, () => vm.Display, v => vm.Display = v));
        panel.Children.Add(Choice(vm, "posture", PlaygroundViewModel.Postures, () => vm.Posture, v => vm.Posture = v));
        panel.Children.Add(Number(vm, "widthDp", () => vm.WidthDp, v => vm.WidthDp = v));
        panel.Children.Add(Number(vm, "heightDp", () => vm.HeightDp, v => vm.HeightDp = v));
        panel.Children.Add(Number(vm, "textScale", () => vm.TextScale, v => vm.TextScale = v));
        panel.Children.Add(Flag(vm, "reduceMotion", () => vm.ReduceMotion, v => vm.ReduceMotion = v));
        panel.Children.Add(Flag(vm, "isOwner", () => vm.IsOwner, v => vm.IsOwner = v));
        panel.Children.Add(Choice(vm, "colorScheme", PlaygroundViewModel.ColorSchemes, () => vm.ColorScheme, v => vm.ColorScheme = v));
        return panel;
    }

    private static StackPanel Choice(PlaygroundViewModel vm, string identifier, IReadOnlyList<string> values, Func<string> get, Action<string> set)
    {
        var label = vm.Text(PlaygroundText.ContextLabel(identifier));
        var box = new ComboBox { ItemsSource = values, SelectedItem = get(), MinWidth = 140 };
        Name(box, ContextId(identifier), label);
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is string chosen)
            {
                set(chosen);
            }
        };
        return Labelled(label, box, margin: new Thickness(0, 0, 12, 8));
    }

    private static StackPanel Number(PlaygroundViewModel vm, string identifier, Func<decimal> get, Action<decimal> set)
    {
        var label = vm.Text(PlaygroundText.ContextLabel(identifier));
        var box = new NumericUpDown { Value = get(), Minimum = 0, FormatString = "0.##", MinWidth = 140 };
        Name(box, ContextId(identifier), label);
        box.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } value)
            {
                set(value);
            }
        };
        return Labelled(label, box, margin: new Thickness(0, 0, 12, 8));
    }

    private static StackPanel Flag(PlaygroundViewModel vm, string identifier, Func<bool> get, Action<bool> set)
    {
        var label = vm.Text(PlaygroundText.ContextLabel(identifier));
        var box = new CheckBox { Content = label, IsChecked = get(), Margin = new Thickness(0, 0, 12, 8) };
        Name(box, ContextId(identifier), label);
        box.IsCheckedChanged += (_, _) => set(box.IsChecked == true);
        return Wrap(box);
    }

    // CheckBox is returned inside a panel so the wrap panel's children share one type.
    private static StackPanel Wrap(Control control) => new() { Children = { control } };

    private static TextBlock DiagnosticRow(PlaygroundDiagnosticLine line) =>
        new() { Text = line.Text, TextWrapping = TextWrapping.Wrap };

    private static StackPanel Labelled(string label, Control control, Thickness? margin = null)
    {
        var caption = new TextBlock { Text = label };
        SetThemeBrush(caption, TextBlock.ForegroundProperty, _secondaryTextToken);
        return new StackPanel { Spacing = 2, Margin = margin ?? default, Children = { caption, control } };
    }

    private static TextBlock Heading(string text)
    {
        var heading = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold };
        return heading;
    }

    private static void Name(Control control, string id, string name)
    {
        AutomationProperties.SetAutomationId(control, id);
        AutomationProperties.SetName(control, name);
        control.Name = id.Replace('.', '_').Replace('-', '_');
        KeyboardNavigation.SetIsTabStop(control, true);
    }

    /// <summary>Sets a brush from a theme token. A token the theme does not define leaves the control's default, so the text stays readable.</summary>
    private static void SetThemeBrush(AvaloniaObject target, AvaloniaProperty property, string token)
    {
        var app = Application.Current;
        if (app is not null && app.TryGetResource(token, app.ActualThemeVariant, out var value) && value is IBrush brush)
        {
            target.SetValue(property, brush);
        }
    }
}
