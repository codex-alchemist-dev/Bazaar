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
                cards.Children.Add(new Border
                {
                    Padding = new Thickness(8),
                    BorderThickness = new Thickness(1),
                    BorderBrush = Brushes.Gray,
                    CornerRadius = new CornerRadius(4),
                    Child = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                });
            }

            board.Children.Add(new Border { Width = 200, Child = cards });
        }

        return new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Content = board };
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
        var rows = new StackPanel { Spacing = 6 };
        if (schedule.Schedule.Rows.Count == 0)
        {
            rows.Children.Add(new TextBlock { Text = "No triggers scheduled.", Opacity = 0.7 });
            return rows;
        }

        foreach (var row in schedule.Schedule.Rows)
        {
            rows.Children.Add(new Border
            {
                Padding = new Thickness(8),
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Gray,
                CornerRadius = new CornerRadius(4),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = $"{row.KindLabel} · {row.StateLabel}", FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = row.Purpose, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = $"{row.NextFireText} · {row.TargetsText}", Opacity = 0.7, TextWrapping = TextWrapping.Wrap },
                    },
                },
            });
        }

        return rows;
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
