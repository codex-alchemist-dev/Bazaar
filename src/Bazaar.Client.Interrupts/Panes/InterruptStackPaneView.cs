using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bazaar.Client.Interrupts.Panes;

/// <summary>The body of the interrupt stack pane. Colours come from theme tokens only.</summary>
internal static class InterruptStackPaneView
{
    private const string _borderToken = "Bazaar.brush.color.border.default";
    private const string _secondaryTextToken = "Bazaar.brush.color.text.secondary";

    internal static Control Create(InterruptStackPaneViewModel pane)
    {
        var body = new StackPanel { Spacing = 8 };
        if (pane.Agents.Count == 0)
        {
            body.Children.Add(new TextBlock { Text = "No agent is interrupted.", Opacity = 0.7 });
        }

        foreach (var agent in pane.Agents)
        {
            var card = new StackPanel { Spacing = 4 };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(new TextBlock { Text = agent.Name, FontWeight = FontWeight.SemiBold });
            header.Children.Add(new StackIndicator { Model = agent.Indicator });
            card.Children.Add(header);

            if (agent.OverLimitNote is { } note)
            {
                card.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap });
            }

            foreach (var frame in agent.Frames)
            {
                var line = $"{(frame.IsTop ? "Top" : "Suspended")} · {frame.Source} · {frame.PushedAt}";
                card.Children.Add(new TextBlock { Text = line, FontSize = 12 });
                var reason = new TextBlock { Text = frame.Reason, FontSize = 12, TextWrapping = TextWrapping.Wrap };
                SetThemeBrush(reason, TextBlock.ForegroundProperty, _secondaryTextToken);
                card.Children.Add(reason);
            }

            var border = new Border
            {
                Padding = new Thickness(8),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = card,
            };
            SetThemeBrush(border, Border.BorderBrushProperty, _borderToken);
            body.Children.Add(border);
        }

        return new ScrollViewer { Content = body };
    }

    private static void SetThemeBrush(AvaloniaObject target, AvaloniaProperty property, string token)
    {
        var app = Application.Current;
        var brush = app is not null && app.TryGetResource(token, app.ActualThemeVariant, out var value) && value is IBrush found
            ? found
            : Brushes.Transparent;
        target.SetValue(property, brush);
    }
}
