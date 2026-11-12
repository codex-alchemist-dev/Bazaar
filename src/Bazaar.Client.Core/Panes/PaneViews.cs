using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Bazaar.Client.Core.Chat;
using Bazaar.Client.Core.Constellation;
using Bazaar.Client.Core.Ryusei;

namespace Bazaar.Client.Core.Panes;

/// <summary>
/// Builds the Avalonia view for a pane view model. Chosen by view-model type, so a new kind needs
/// a view model and one case here; the registry and heads do not change.
/// </summary>
public static class PaneViews
{
    // Theme tokens (one theme file drives every head). Views bind to these, never to a fixed colour.
    private const string _borderToken = "Bazaar.brush.color.border.default";
    private const string _secondaryTextToken = "Bazaar.brush.color.text.secondary";

    public static Control Create(IPaneViewModel pane) => pane switch
    {
        GenericPaneViewModel generic => WithTitle(generic.Title, Generic(generic)),
        _ => WithTitle(pane.Title, new TextBlock { Text = pane.Kind }),
    };

    internal static StackPanel WithTitle(string title, Control body) => new StackPanel
    {
        Spacing = 8,
        Children =
        {
            new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 16 },
            body,
        },
    };

    internal static ScrollViewer KanbanBoard(KanbanPaneViewModel kanban)
    {
        var board = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var column in kanban.Columns)
        {
            var cards = new StackPanel { Spacing = 6 };
            cards.Children.Add(new TextBlock { Text = $"{column.Name} ({column.Cards.Count})", FontWeight = FontWeight.SemiBold });
            foreach (var card in column.Cards)
            {
                var label = card.IsBlocked ? $"{card.Title} [Blocked]" : card.Title;
                var content = new StackPanel { Spacing = 4 };
                content.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
                foreach (var chip in kanban.ChipsFor(card.Id))
                {
                    content.Children.Add(TriggerChip(chip));
                }

                var cardBorder = new Border
                {
                    Padding = new Thickness(8),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Child = content,
                };
                SetThemeBrush(cardBorder, Border.BorderBrushProperty, _borderToken);
                cards.Children.Add(cardBorder);
            }

            board.Children.Add(new Border { Width = 200, Child = cards });
        }

        return new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Content = board };
    }

    /// <summary>
    /// A read-only trigger chip on a card (doc 38, layer 1): kind, state and next fire on one line, the purpose below.
    /// Colours come from the theme tokens, so the chip follows the active theme.
    /// </summary>
    private static Border TriggerChip(RyuseiTaskChip chip)
    {
        var status = new TextBlock { Text = RyuseiChipRules.Status(chip), FontSize = 11 };
        var purpose = new TextBlock { Text = RyuseiChipRules.Purpose(chip), FontSize = 11, TextWrapping = TextWrapping.Wrap };
        SetThemeBrush(purpose, TextBlock.ForegroundProperty, _secondaryTextToken);

        var chipBorder = new Border
        {
            Padding = new Thickness(6, 4),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Child = new StackPanel { Spacing = 2, Children = { status, purpose } },
        };
        SetThemeBrush(chipBorder, Border.BorderBrushProperty, _borderToken);
        return chipBorder;
    }

    /// <summary>
    /// Sets a brush property from a theme token. The brush is read from the application resources when the view is
    /// built, so the token must exist in the active theme. A token the theme does not define falls back to transparent,
    /// so the view still renders.
    /// </summary>
    private static void SetThemeBrush(AvaloniaObject target, AvaloniaProperty property, string token)
    {
        var app = Application.Current;
        var brush = app is not null && app.TryGetResource(token, app.ActualThemeVariant, out var value) && value is IBrush found
            ? found
            : Brushes.Transparent;
        target.SetValue(property, brush);
    }

    internal static Control GcPane(GcPaneViewModel gc)
    {
        if (gc.Chat is null)
        {
            return new TextBlock { Text = "No group chat is connected.", Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        }

        // A fixed height gives the chat's message list a bounded area inside the stacked pane.
        return new Border { Height = 480, Child = new ChatView { DataContext = gc.Chat } };
    }

    internal static StackPanel RyuseiSchedule(RyuseiSchedulePaneViewModel schedule)
    {
        var body = new StackPanel { Spacing = 6 };

        // Rebuilds the rows from the view model. Called once now and again after each change the server confirms.
        void Render()
        {
            body.Children.Clear();
            if (schedule.Schedule.Rows.Count == 0)
            {
                body.Children.Add(new TextBlock { Text = "No triggers scheduled.", Opacity = 0.7 });
            }

            foreach (var row in schedule.Schedule.Rows)
            {
                body.Children.Add(ScheduleRow(schedule, row, RenderAfter));
            }

            if (schedule.LastError is { } error)
            {
                body.Children.Add(new TextBlock { Text = error, TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
            }
        }

        async Task RenderAfter(Task change)
        {
            await change.ConfigureAwait(true);
            Render();
        }

        Render();
        return body;
    }

    /// <summary>
    /// One trigger row with its Pause or Resume button. The button is disabled when no head supplied the actions, and
    /// only offers the action the row's state allows.
    /// </summary>
    private static Border ScheduleRow(RyuseiSchedulePaneViewModel schedule, RyuseiMicroRow row, Func<Task, Task> renderAfter)
    {
        var details = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = $"{row.KindLabel} · {row.StateLabel}", FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = row.Purpose, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = $"{row.NextFireText} · {row.TargetsText}", Opacity = 0.7, TextWrapping = TextWrapping.Wrap },
            },
        };

        var stack = new StackPanel { Spacing = 6, Children = { details } };
        if (string.Equals(row.State, "active", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(row.State, "paused", StringComparison.OrdinalIgnoreCase))
        {
            var paused = string.Equals(row.State, "paused", StringComparison.OrdinalIgnoreCase);
            var button = new Button
            {
                Content = paused ? "Resume" : "Pause",
                IsEnabled = schedule.CanChangeTriggers,
                HorizontalAlignment = HorizontalAlignment.Left,
                Tag = row.Id,
            };
            button.Click += (_, _) =>
            {
                var change = paused ? schedule.ResumeAsync(row.Id) : schedule.PauseAsync(row.Id);
                _ = renderAfter(change);
            };
            stack.Children.Add(button);
        }

        var rowBorder = new Border
        {
            Padding = new Thickness(8),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = stack,
        };
        SetThemeBrush(rowBorder, Border.BorderBrushProperty, _borderToken);
        return rowBorder;
    }


    internal static StackPanel Workday(WorkdayPaneViewModel workday)
    {
        var body = new StackPanel { Spacing = 10 };
        if (workday.Owners.Count == 0)
        {
            body.Children.Add(new TextBlock { Text = "No tasks on the board.", Opacity = 0.7 });
            return body;
        }

        foreach (var owner in workday.Owners)
        {
            body.Children.Add(new TextBlock { Text = $"{owner.Name} ({owner.Tasks.Count})", FontWeight = FontWeight.SemiBold });
            foreach (var task in owner.Tasks)
            {
                body.Children.Add(new TextBlock
                {
                    Text = task.Line,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(16, 0, 0, 0),
                });
            }
        }

        return body;
    }

    internal static StackPanel Roadmap(RoadmapPaneViewModel roadmap)
    {
        var body = new StackPanel { Spacing = 10 };
        if (roadmap.DependencyCycle is { Count: > 0 } cycle)
        {
            body.Children.Add(new TextBlock
            {
                Text = "Dependency cycle: " + string.Join(" → ", cycle),
                Foreground = Brushes.OrangeRed,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        if (roadmap.Phases.Count == 0 && roadmap.UnphasedObjectives.Count == 0)
        {
            body.Children.Add(new TextBlock { Text = "No objectives yet.", Opacity = 0.7 });
        }

        foreach (var phase in roadmap.Phases)
        {
            body.Children.Add(new TextBlock { Text = $"{phase.Title} · {phase.Gate}", FontWeight = FontWeight.SemiBold });
            foreach (var objective in phase.Objectives)
            {
                body.Children.Add(new TextBlock { Text = objective.Line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 0, 0) });
            }
        }

        if (roadmap.UnphasedObjectives.Count > 0)
        {
            body.Children.Add(new TextBlock { Text = "Unphased", FontWeight = FontWeight.SemiBold });
            foreach (var objective in roadmap.UnphasedObjectives)
            {
                body.Children.Add(new TextBlock { Text = objective.Line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 0, 0) });
            }
        }

        return body;
    }

    internal static StackPanel Dashboard(DashboardPaneViewModel dashboard)
    {
        var sections = new StackPanel { Spacing = 12 };
        foreach (var section in dashboard.Sections)
        {
            sections.Children.Add(new TextBlock { Text = section.Title, FontWeight = FontWeight.SemiBold });
            foreach (var item in section.Items)
            {
                sections.Children.Add(new TextBlock { Text = $"{item.Label}: {item.Value}" });
            }
        }

        return sections;
    }

    private static ScrollViewer Generic(GenericPaneViewModel generic)
    {
        var lines = new StackPanel { Spacing = 2 };
        foreach (var line in generic.Lines)
        {
            lines.Children.Add(new TextBlock { Text = line, FontFamily = new FontFamily("monospace") });
        }

        return new ScrollViewer { Content = lines };
    }
}
