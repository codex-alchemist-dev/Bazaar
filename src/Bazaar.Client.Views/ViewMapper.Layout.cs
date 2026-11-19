using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Bazaar.Rubedo.Views;

namespace Bazaar.Client.Views;

/// <summary>Layout widgets: stack, row, grid, scroll and divider.</summary>
public sealed partial class ViewMapper
{
    /// <summary>The spacing token a layout node uses between children. Defaults to space.sm.</summary>
    private static string GapToken(ViewNode node) => OptionalLiteral(node, "gap", "space.sm");

    private StackPanel MapStack(ViewNode node, Scope scope)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = _theme.Length(GapToken(node)),
        };
        AddChildren(panel.Children, node, scope);
        return panel;
    }

    /// <summary>A horizontal row. With the wrap flag it flows onto new lines; each child gets half the gap on every side.</summary>
    private Control MapRow(ViewNode node, Scope scope)
    {
        double gap = _theme.Length(GapToken(node));
        if (!OptionalFlag(node, "wrap", fallback: false))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = gap };
            AddChildren(row.Children, node, scope);
            return row;
        }

        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (ViewNode child in node.Children)
        {
            wrap.Children.Add(new Border { Margin = new Thickness(gap / 2), Child = Node(child, scope) });
        }

        return wrap;
    }

    /// <summary>A grid with a fixed column count. Children fill rows left to right, top to bottom.</summary>
    private Grid MapGrid(ViewNode node, Scope scope)
    {
        int columns = (int)LiteralNumber(node, "columns", min: 1, max: 12);
        int rows = (node.Children.Count + columns - 1) / columns;

        var grid = new Grid();
        for (int column = 0; column < columns; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }

        for (int row = 0; row < rows; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        for (int index = 0; index < node.Children.Count; index++)
        {
            Control cell = Node(node.Children[index], scope);
            Grid.SetRow(cell, index / columns);
            Grid.SetColumn(cell, index % columns);
            grid.Children.Add(cell);
        }

        return grid;
    }

    /// <summary>A scrolling region around exactly one child.</summary>
    private ScrollViewer MapScroll(ViewNode node, Scope scope)
    {
        if (node.Children.Count != 1)
        {
            throw new ViewDeclarationException("needs exactly one child.");
        }

        return new ScrollViewer { Content = Node(node.Children[0], scope) };
    }

    /// <summary>A one-pixel separator in the border colour.</summary>
    private Border MapDivider() => new Border
    {
        Height = 1,
        Background = _theme.Brush("color.border.default"),
        Margin = new Thickness(0, _theme.Length("space.sm")),
    };
}
