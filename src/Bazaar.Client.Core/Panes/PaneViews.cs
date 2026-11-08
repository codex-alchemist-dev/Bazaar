using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Bazaar.Client.Core.Constellation;

namespace Bazaar.Client.Core.Panes;

/// <summary>
/// Builds the Avalonia view for a pane view model. Chosen by view-model type, so a new kind needs
/// a view model and one case here; the registry and heads do not change.
/// </summary>
public static class PaneViews
{
    public static Control Create(IPaneViewModel pane) => pane switch
    {
        KanbanPaneViewModel kanban => WithTitle(kanban.Title, KanbanBoard(kanban)),
        DashboardPaneViewModel dashboard => WithTitle(dashboard.Title, Dashboard(dashboard)),
        ConstellationPaneViewModel constellation => WithTitle(constellation.Title, new ConstellationPaneView(constellation)),
        Vault.VaultSearchPaneViewModel vaultSearch => WithTitle(vaultSearch.Title, new Vault.VaultSearchView(vaultSearch)),
        GenericPaneViewModel generic => WithTitle(generic.Title, Generic(generic)),
        _ => WithTitle(pane.Title, new TextBlock { Text = pane.Kind }),
    };

    private static StackPanel WithTitle(string title, Control body) => new StackPanel
    {
        Spacing = 8,
        Children =
        {
            new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 16 },
            body,
        },
    };

    private static ScrollViewer KanbanBoard(KanbanPaneViewModel kanban)
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

    private static StackPanel Dashboard(DashboardPaneViewModel dashboard)
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
