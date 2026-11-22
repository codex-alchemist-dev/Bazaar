using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

namespace Bazaar.Client.Interrupts;

/// <summary>
/// A small reusable indicator of an agent's interrupt stack. Bind <see cref="Model"/>; at depth 0 the control is hidden
/// and empty. Heads and the presence chip embed it. Colours come from theme tokens, never fixed values.
/// </summary>
public sealed class StackIndicator : Border
{
    public static readonly StyledProperty<StackIndicatorViewModel?> ModelProperty =
        AvaloniaProperty.Register<StackIndicator, StackIndicatorViewModel?>(nameof(Model));

    private const string _borderToken = "Bazaar.brush.color.border.default";

    static StackIndicator()
    {
        ModelProperty.Changed.AddClassHandler<StackIndicator>((indicator, _) => indicator.Refresh());
    }

    public StackIndicator()
    {
        Padding = new Thickness(6, 2);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(3);
        Refresh();
    }

    public StackIndicatorViewModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    private void Refresh()
    {
        var model = Model ?? StackIndicatorViewModel.Empty;
        IsVisible = model.IsVisible;
        Child = model.IsVisible ? new TextBlock { Text = model.Text, FontSize = 11 } : null;
        AutomationProperties.SetName(this, model.AccessibleName);

        var app = Application.Current;
        BorderBrush = app is not null && app.TryGetResource(_borderToken, app.ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : Brushes.Transparent;
    }
}
