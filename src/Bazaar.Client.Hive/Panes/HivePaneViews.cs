using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Bazaar.Client.Hive.Grants;

namespace Bazaar.Client.Hive.Panes;

/// <summary>The body of the Hive pane. The pane module registers it with the pane registry.</summary>
internal static class HivePaneViews
{
    internal static StackPanel Hive(HivePaneViewModel pane)
    {
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock { Text = HivePaneViewModel.CopiesOnlyNotice, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold });
        body.Children.Add(new TextBlock
        {
            Text = pane.ReviewDays == 0
                ? "Review window: 0 days. Every batch stays in review."
                : $"Review window: {pane.ReviewDays} days, then copied automatically under the grant.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        });

        body.Children.Add(GrantMatrix(pane));
        body.Children.Add(CopyList(pane));
        return body;
    }

    /// <summary>The owner grant matrix: one block per kind, a row per from-project, a toggle button per to-project.</summary>
    private static StackPanel GrantMatrix(HivePaneViewModel pane)
    {
        var matrix = new StackPanel { Spacing = 8 };
        matrix.Children.Add(new TextBlock { Text = "Grants (from project → to project)", FontWeight = FontWeight.SemiBold });
        if (pane.Matrix.Kinds.Count == 0)
        {
            matrix.Children.Add(new TextBlock { Text = "No grants yet.", Opacity = 0.7 });
        }

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
        foreach (var kind in pane.Matrix.Kinds)
        {
            matrix.Children.Add(new TextBlock { Text = kind.Kind, FontWeight = FontWeight.SemiBold });
            foreach (var row in kind.Rows)
            {
                var cells = new WrapPanel { Orientation = Orientation.Horizontal };
                cells.Children.Add(new TextBlock { Text = row.FromProject, Margin = new Thickness(0, 4, 8, 0), MinWidth = 120 });
                foreach (var cell in row.Cells)
                {
                    cells.Children.Add(cell.IsSelf
                        ? new TextBlock { Text = "—", Margin = new Thickness(4), Opacity = 0.5 }
                        : GrantToggle(pane, cell, status));
                }

                matrix.Children.Add(cells);
            }
        }

        matrix.Children.Add(status);
        return matrix;
    }

    /// <summary>A toggle for one cell. It is disabled when no head supplied grant actions.</summary>
    private static Button GrantToggle(HivePaneViewModel pane, HiveMatrixCell cell, TextBlock status)
    {
        var button = new Button
        {
            Content = $"{cell.Key.ToProject}: {(cell.Granted ? "granted" : "not granted")}",
            IsEnabled = pane.CanChangeGrants,
            Margin = new Thickness(4),
        };
        button.Click += (_, _) => _ = ApplyAsync(pane, cell, status);
        return button;
    }

    private static async Task ApplyAsync(HivePaneViewModel pane, HiveMatrixCell cell, TextBlock status)
    {
        await pane.RequestAsync(cell.Key, grant: !cell.Granted).ConfigureAwait(true);
        status.Text = pane.LastStatus ?? string.Empty;
    }

    /// <summary>Every copy with its origin label. Deprecated copies stay visible and carry their marker.</summary>
    private static StackPanel CopyList(HivePaneViewModel pane)
    {
        var copies = new StackPanel { Spacing = 4 };
        copies.Children.Add(new TextBlock { Text = "Copies", FontWeight = FontWeight.SemiBold });
        if (pane.Copies.Count == 0)
        {
            copies.Children.Add(new TextBlock { Text = "No copies yet.", Opacity = 0.7 });
        }

        foreach (var copy in pane.Copies)
        {
            copies.Children.Add(new TextBlock
            {
                Text = copy.Line,
                TextWrapping = TextWrapping.Wrap,
                Opacity = copy.IsDeprecated ? 0.6 : 1,
                Margin = new Thickness(8, 0, 0, 0),
            });
        }

        return copies;
    }
}
