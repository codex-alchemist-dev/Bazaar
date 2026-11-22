using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bazaar.Client.ColdArchive.Panes;

/// <summary>The body of the Archive pane. Colours come from theme tokens only; the pane is plain controls over the view model.</summary>
internal static class ColdArchivePaneView
{
    private const string _secondaryTextToken = "Bazaar.brush.color.text.secondary";

    internal static Control Create(ColdArchivePaneViewModel pane)
    {
        var kind = new ComboBox { ItemsSource = new[] { "gc", "project" }, SelectedItem = pane.UnitKind, Width = 110 };
        kind.SelectionChanged += (_, _) => pane.UnitKind = kind.SelectedItem as string ?? "gc";
        var id = new TextBox { Text = pane.UnitId, PlaceholderText = "unit id", MinWidth = 260 };
        id.TextChanged += (_, _) => pane.UnitId = id.Text ?? string.Empty;
        var unitRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        unitRow.Children.Add(kind);
        unitRow.Children.Add(id);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(Button("Refresh", () => _ = pane.RefreshAsync()));
        actions.Children.Add(Button("Archive", () => _ = pane.ArchiveAsync()));
        actions.Children.Add(Button("Verify", () => _ = pane.VerifyAsync()));
        actions.Children.Add(Button("Restore report", () => _ = pane.RestoreAsync()));

        var units = new ListBox { ItemsSource = pane.Units, MaxHeight = 200 };
        units.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ArchiveUnitInfo>((u, _) => new TextBlock { Text = u is null ? string.Empty : $"{u.Kind} · {u.Id}" });
        units.SelectionChanged += (_, _) =>
        {
            if (units.SelectedItem is ArchiveUnitInfo u)
            {
                pane.Select(u);
                kind.SelectedItem = u.Kind;
                id.Text = u.Id;
            }
        };

        var query = new TextBox { Text = pane.Query, PlaceholderText = "search archived text", MinWidth = 260 };
        query.TextChanged += (_, _) => pane.Query = query.Text ?? string.Empty;
        var searchRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        searchRow.Children.Add(query);
        searchRow.Children.Add(Button("Search", () => _ = pane.SearchAsync()));

        var hits = new ListBox { ItemsSource = pane.Hits, MaxHeight = 240 };
        hits.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ArchiveHit>((h, _) =>
            new TextBlock { Text = h is null ? string.Empty : $"{h.Kind} {h.Unit} · {h.RecordId} · {h.Snippet}", TextWrapping = TextWrapping.Wrap });

        var status = new TextBlock { Text = pane.Status, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(ColdArchivePaneViewModel.Status)) { Source = pane });
        if (status.TryFindResource(_secondaryTextToken, status.ActualThemeVariant, out var brush) && brush is IBrush b)
        {
            status.Foreground = b;
        }

        var body = new StackPanel { Spacing = 8, Margin = new Thickness(8) };
        body.Children.Add(unitRow);
        body.Children.Add(actions);
        body.Children.Add(status);
        body.Children.Add(new TextBlock { Text = "Archived", FontWeight = FontWeight.SemiBold });
        body.Children.Add(units);
        body.Children.Add(searchRow);
        body.Children.Add(hits);
        return new ScrollViewer { Content = body };
    }

    private static Button Button(string text, Action click)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => click();
        return button;
    }
}
