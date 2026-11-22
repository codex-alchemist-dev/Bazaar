using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bazaar.Client.GitGui.Diff;

/// <summary>
/// The native text diff: a header, any notice, and a virtualised list of rows with old and new line numbers, a +/- marker and
/// the text. Colours come from the theme tokens. It draws on every head, including Android, and needs no editor component.
/// </summary>
public sealed class NativeDiffSurface : IDiffSurface
{
    private const double _numberWidth = 48;

    public DiffSurfaceKind Kind => DiffSurfaceKind.Native;

    public Control Create(DiffModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var root = new DockPanel();

        var header = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 6) };
        var title = model.OldPath is { Length: > 0 } old && old != model.Path ? $"{old} → {model.Path}" : model.Path;
        header.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold });
        header.Children.Add(Themed(new TextBlock { Text = $"{model.Status} · +{model.Additions} -{model.Deletions}" }, ThemeBrushes.TextSecondary));
        if (model.Truncated)
        {
            header.Children.Add(Notice("This file's diff was cut at the server size limit. Open it on the provider's site to see the rest."));
        }

        if (NoticeText(model) is { } notice)
        {
            header.Children.Add(Notice(notice));
        }

        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // A ListBox virtualises its rows, so a patch with thousands of lines builds only the visible ones.
        var list = new ListBox
        {
            ItemsSource = model.Rows(),
            ItemTemplate = new FuncDataTemplate<DiffLine>((line, _) => line is null ? null : Row(line)),
            Focusable = false,
        };
        AutomationProperties.SetName(list, $"Diff of {model.Path}");
        root.Children.Add(list);
        return root;
    }

    private static string? NoticeText(DiffModel model) => model.Notice switch
    {
        DiffNotice.Binary => "Binary file; there is no text diff.",
        DiffNotice.NoPatch when string.Equals(model.Status, "renamed", StringComparison.OrdinalIgnoreCase) => "Renamed with no content changes.",
        DiffNotice.NoPatch => "No patch to show for this file.",
        _ => null,
    };

    private static TextBlock Notice(string text) => Themed(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, ThemeBrushes.TextAccent);

    private static Grid Row(DiffLine line)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{_numberWidth},{_numberWidth},16,*") };
        var mono = new FontFamily("monospace");

        string marker;
        string? brush;
        switch (line.Kind)
        {
            case DiffLineKind.Added:
                marker = "+";
                brush = ThemeBrushes.Added;
                break;
            case DiffLineKind.Removed:
                marker = "-";
                brush = ThemeBrushes.Removed;
                break;
            case DiffLineKind.HunkHeader:
                marker = string.Empty;
                brush = ThemeBrushes.TextAccent;
                break;
            case DiffLineKind.Collapsed:
            case DiffLineKind.NoNewline:
                marker = string.Empty;
                brush = ThemeBrushes.TextSecondary;
                break;
            default:
                marker = " ";
                brush = null;
                break;
        }

        AddCell(grid, 0, line.OldNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, ThemeBrushes.TextSecondary, mono, HorizontalAlignment.Right);
        AddCell(grid, 1, line.NewNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, ThemeBrushes.TextSecondary, mono, HorizontalAlignment.Right);
        AddCell(grid, 2, marker, brush, mono, HorizontalAlignment.Center);
        AddCell(grid, 3, line.Text, brush, mono, HorizontalAlignment.Left);
        AutomationProperties.SetName(grid, line.Kind switch
        {
            DiffLineKind.Added => $"added: {line.Text}",
            DiffLineKind.Removed => $"removed: {line.Text}",
            _ => line.Text,
        });
        return grid;
    }

    private static void AddCell(Grid grid, int column, string text, string? brushToken, FontFamily font, HorizontalAlignment alignment)
    {
        var cell = new TextBlock { Text = text, FontFamily = font, HorizontalAlignment = alignment, Margin = new Thickness(2, 0) };
        if (brushToken is not null)
        {
            Themed(cell, brushToken);
        }

        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private static TextBlock Themed(TextBlock block, string token)
    {
        if (ThemeBrushes.Find(token) is { } brush)
        {
            block.Foreground = brush;
        }

        return block;
    }
}
