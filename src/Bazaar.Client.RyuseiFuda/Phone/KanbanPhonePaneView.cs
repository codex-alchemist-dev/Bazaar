using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Bazaar.Client.RyuseiFuda.Workday;

namespace Bazaar.Client.RyuseiFuda.Phone;

/// <summary>
/// Builds the phone body of <see cref="KanbanPhonePaneViewModel"/>: the status line, then the five columns one under another.
/// Each card has one button per other column; a press calls the view model's revision-checked move. The body redraws on
/// the view model's <see cref="KanbanPhonePaneViewModel.Changed"/> event, so a conflict or a refusal shows at once.
/// </summary>
public static class KanbanPhonePaneView
{
    // Theme token for card borders (one theme file drives every head). A token the theme lacks falls back to transparent.
    private const string _borderToken = "Bazaar.brush.color.border.default";
    private const string _secondaryTextToken = "Bazaar.brush.color.text.secondary";

    /// <summary>The pane body, scrollable so a long board fits a phone-height page.</summary>
    public static ScrollViewer Create(KanbanPhonePaneViewModel pane)
    {
        ArgumentNullException.ThrowIfNull(pane);

        var body = new StackPanel { Spacing = 12, Margin = new Thickness(8) };
        var scroll = new ScrollViewer { Content = body };

        void Render()
        {
            body.Children.Clear();
            if (pane.Status is { } status)
            {
                body.Children.Add(new TextBlock
                {
                    Text = status,
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = FontWeight.SemiBold,
                    Name = "BoardStatus",
                });
            }

            foreach (var column in pane.Columns)
            {
                body.Children.Add(ColumnView(pane, column));
            }
        }

        pane.Changed += (_, _) => Render();
        Render();
        return scroll;
    }

    private static StackPanel ColumnView(KanbanPhonePaneViewModel pane, KanbanPhoneColumn column)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(new TextBlock
        {
            Text = $"{column.Title} ({column.Cards.Count})",
            FontWeight = FontWeight.SemiBold,
            Classes = { "column-title" },
        });

        if (column.Cards.Count == 0)
        {
            stack.Children.Add(new TextBlock { Text = "No cards", Opacity = 0.7 });
        }

        foreach (var card in column.Cards)
        {
            stack.Children.Add(CardView(pane, card));
        }

        return stack;
    }

    private static Border CardView(KanbanPhonePaneViewModel pane, KanbanPhoneCard card)
    {
        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(new TextBlock
        {
            Text = card.Blocked ? $"{card.Title} [Blocked]" : card.Title,
            TextWrapping = TextWrapping.Wrap,
        });

        if (card.BlockerTitles.Count > 0)
        {
            var blockers = new TextBlock
            {
                Text = $"Blocked by {string.Join(", ", card.BlockerTitles)}",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
            };
            SetThemeBrush(blockers, TextBlock.ForegroundProperty, _secondaryTextToken);
            content.Children.Add(blockers);
        }

        var moves = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var target in KanbanStates.All.Where(state => state != card.State))
        {
            var title = KanbanStates.TitleOf(target);
            var button = new Button
            {
                Content = $"To {title}",
                IsEnabled = !pane.IsBusy,
                Margin = new Thickness(0, 0, 6, 6),
            };
            AutomationProperties.SetName(button, $"Move {card.Title} to {title}");
            button.Click += (_, _) => _ = pane.MoveAsync(card.Id, target);
            moves.Children.Add(button);
        }

        content.Children.Add(moves);

        var border = new Border
        {
            Padding = new Thickness(8),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = content,
        };
        SetThemeBrush(border, Border.BorderBrushProperty, _borderToken);
        return border;
    }

    /// <summary>
    /// Sets a brush from a theme token. The token is read from the application resources when the view is built. A token the
    /// theme does not define falls back to transparent, so the view still renders.
    /// </summary>
    private static void SetThemeBrush(AvaloniaObject target, AvaloniaProperty property, string token)
    {
        var app = Application.Current;
        var brush = app is not null && app.TryGetResource(token, app.ActualThemeVariant, out var value) && value is IBrush found
            ? found
            : Brushes.Transparent;
        target.SetValue(property, brush);
    }
}
