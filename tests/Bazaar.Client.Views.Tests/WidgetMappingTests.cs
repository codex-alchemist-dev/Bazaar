using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Bazaar.Client.Views.Tests.Fixtures;
using Bazaar.Rubedo;
using Bazaar.Rubedo.Views;
using Path = Avalonia.Controls.Shapes.Path;

namespace Bazaar.Client.Views.Tests;

/// <summary>Each of the 13 widget kinds (doc 41c, section 3) maps to its control; none fails closed on a valid node.</summary>
public class WidgetMappingTests
{
    [AvaloniaTheory]
    [InlineData("stack", typeof(StackPanel))]
    [InlineData("row", typeof(StackPanel))]
    [InlineData("grid", typeof(Grid))]
    [InlineData("scroll", typeof(ScrollViewer))]
    [InlineData("text", typeof(TextBlock))]
    [InlineData("icon", typeof(Path))]
    [InlineData("button", typeof(Button))]
    [InlineData("input", typeof(TextBox))]
    [InlineData("list", typeof(KeyedListPanel))]
    [InlineData("tabs", typeof(TabControl))]
    [InlineData("badge", typeof(Border))]
    [InlineData("divider", typeof(Border))]
    [InlineData("placeholder", typeof(Border))]
    public void Each_widget_maps_to_its_control(string widget, Type expected)
    {
        var harness = new Harness();

        Control control = harness.Build(ViewFixtures.Minimal(widget));

        Assert.IsType(expected, control);
        if (widget == "placeholder")
        {
            Assert.Contains("Coming later.", ViewFixtures.PlaceholderText(control));
        }
        else
        {
            Assert.Null(ViewFixtures.AsPlaceholder(control));
        }
    }

    /// <summary>The minimal document for each widget passes the real validator (doc 41c, section 8).</summary>
    [Theory]
    [InlineData("stack")]
    [InlineData("row")]
    [InlineData("grid")]
    [InlineData("scroll")]
    [InlineData("text")]
    [InlineData("icon")]
    [InlineData("button")]
    [InlineData("input")]
    [InlineData("list")]
    [InlineData("tabs")]
    [InlineData("badge")]
    [InlineData("divider")]
    [InlineData("placeholder")]
    public void Minimal_document_passes_the_real_validator(string widget)
    {
        Assert.Empty(ViewValidator.Validate(ViewFixtures.Minimal(widget)));
    }

    /// <summary>A list's item fields are in scope in its template: the template binds "title" and the list shows each row's title.</summary>
    [AvaloniaFact]
    public void A_list_template_reads_the_item_fields_it_binds()
    {
        var harness = new Harness();

        var list = Assert.IsType<KeyedListPanel>(harness.Build(ViewFixtures.Minimal("list")));

        Assert.Equal(["First", "Second"], list.Children.Cast<TextBlock>().Select(t => t.Text));
    }

    [AvaloniaFact]
    public void Row_with_wrap_flows_children_in_a_wrap_panel()
    {
        var harness = new Harness();
        var node = ViewFixtures.Node("row", [ViewFixtures.Text("a"), ViewFixtures.Text("b")], ("wrap", ViewFixtures.Lit(true)));

        var control = harness.Build(ViewFixtures.Document(node));

        var wrap = Assert.IsType<WrapPanel>(control);
        Assert.Equal(2, wrap.Children.Count);
    }

    [AvaloniaFact]
    public void Grid_places_children_row_by_row_in_the_declared_column_count()
    {
        var harness = new Harness();

        var grid = Assert.IsType<Grid>(harness.Build(ViewFixtures.Minimal("grid")));

        Assert.Equal(2, grid.ColumnDefinitions.Count);
        Assert.Equal(2, grid.RowDefinitions.Count);
        Assert.Equal((0, 0), (Grid.GetRow(grid.Children[0]), Grid.GetColumn(grid.Children[0])));
        Assert.Equal((0, 1), (Grid.GetRow(grid.Children[1]), Grid.GetColumn(grid.Children[1])));
        Assert.Equal((1, 0), (Grid.GetRow(grid.Children[2]), Grid.GetColumn(grid.Children[2])));
    }

    [AvaloniaFact]
    public void Text_style_small_uses_the_secondary_colour_and_small_size()
    {
        var harness = new Harness();
        var node = ViewFixtures.Node("text", [], ("text", ViewFixtures.Lit("note")), ("style", ViewFixtures.Lit("small")));

        var text = Assert.IsType<TextBlock>(harness.Build(ViewFixtures.Document(node)));

        Assert.Equal(12, text.FontSize);
        Assert.Equal(ViewFixtures.Parse(ViewFixtures.ColourSecondary), ViewFixtures.ColorOf(text.Foreground));
    }

    [AvaloniaFact]
    public void Icon_is_named_by_its_label_and_unknown_icons_fail_closed()
    {
        var harness = new Harness();

        var icon = Assert.IsType<Path>(harness.Build(ViewFixtures.Minimal("icon")));

        Assert.Equal("Done", Avalonia.Automation.AutomationProperties.GetName(icon));
        var bad = ViewFixtures.Node("icon", [], ("name", ViewFixtures.Lit("skull")), ("label", ViewFixtures.Lit("x")));
        Assert.Contains("not in the icon set", ViewFixtures.PlaceholderText(harness.Build(ViewFixtures.Document(bad))));
    }

    /// <summary>A button label is a localisation key or a literal: a known key shows its translation, a literal shows as written.</summary>
    [AvaloniaFact]
    public void Button_label_goes_through_the_localiser()
    {
        var harness = new Harness();
        var actions = ("go", new ViewAction(ViewActionKind.Navigate, "gc.feed"));
        ViewNode Button(string label) => ViewFixtures.Node("button", [], ("label", ViewFixtures.Lit(label)), ("action", ViewFixtures.Lit("go")));

        var key = Assert.IsType<Button>(harness.Build(ViewFixtures.Document(Button("greeting"), actions)));
        var literal = Assert.IsType<Button>(harness.Build(ViewFixtures.Document(Button("Open"), actions)));

        Assert.Equal("Hello from the catalogue", key.Content);
        Assert.Equal("Open", literal.Content);
    }
}
