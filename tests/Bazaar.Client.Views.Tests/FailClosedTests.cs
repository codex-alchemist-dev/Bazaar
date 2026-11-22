using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bazaar.Client.Views.Tests.Fixtures;
using Bazaar.Rubedo;
using Bazaar.Rubedo.Views;

namespace Bazaar.Client.Views.Tests;

/// <summary>An unknown or broken node shows a visible placeholder in its own place and never throws or hides its siblings (doc 41, section 7).</summary>
public class FailClosedTests
{
    [AvaloniaFact]
    public void An_unknown_widget_shows_a_placeholder_naming_it_and_its_siblings_still_render()
    {
        var harness = new Harness();
        var root = ViewFixtures.Node("stack", [ViewFixtures.Text("kept"), ViewFixtures.Node("carousel")]);

        var stack = Assert.IsType<StackPanel>(harness.Build(ViewFixtures.Document(root)));

        Assert.Equal("kept", Assert.IsType<TextBlock>(stack.Children[0]).Text);
        var placeholder = Assert.IsType<Border>(stack.Children[1]);
        Assert.Equal(ViewMapper.PlaceholderName, placeholder.Name);
        string text = ViewFixtures.PlaceholderText(placeholder);
        Assert.Contains("'carousel'", text);
        Assert.Contains("bazaar.test.view", text);
        Assert.Contains("unknown widget", text);
    }

    [AvaloniaFact]
    public void A_button_without_a_label_fails_closed()
    {
        var harness = new Harness();
        var node = ViewFixtures.Node("button", [], ("action", ViewFixtures.Lit("nav")));

        Control control = harness.Build(ViewFixtures.Document(node, ("nav", new ViewAction(ViewActionKind.Navigate, "x"))));

        Assert.Contains("needs prop 'label'", ViewFixtures.PlaceholderText(control));
    }

    [AvaloniaFact]
    public void A_button_naming_an_unknown_action_fails_closed()
    {
        var harness = new Harness();
        var node = ViewFixtures.Node("button", [], ("label", ViewFixtures.Lit("Go")), ("action", ViewFixtures.Lit("nope")));

        Control control = harness.Build(ViewFixtures.Document(node));

        Assert.Contains("unknown action 'nope'", ViewFixtures.PlaceholderText(control));
    }

    [AvaloniaFact]
    public void A_binding_outside_the_scope_fails_closed()
    {
        var harness = new Harness();
        var node = ViewFixtures.Node("text", [], ("value", ViewFixtures.Bind("missing")));

        Control control = harness.Build(ViewFixtures.Document(node));

        Assert.Contains("'missing' is not in the context", ViewFixtures.PlaceholderText(control));
    }

    [AvaloniaFact]
    public void A_grid_with_an_out_of_range_column_count_fails_closed()
    {
        var harness = new Harness();
        var node = ViewFixtures.Node("grid", [ViewFixtures.Text("a")], ("columns", ViewFixtures.Lit(40.0)));

        Control control = harness.Build(ViewFixtures.Document(node));

        Assert.Contains("from 1 to 12", ViewFixtures.PlaceholderText(control));
    }

    [AvaloniaFact]
    public void A_scroll_needs_exactly_one_child()
    {
        var harness = new Harness();

        Control control = harness.Build(ViewFixtures.Document(ViewFixtures.Node("scroll", [ViewFixtures.Text("a"), ViewFixtures.Text("b")])));

        Assert.Contains("exactly one child", ViewFixtures.PlaceholderText(control));
    }

    [AvaloniaFact]
    public void A_tab_without_a_title_fails_the_tabs_widget()
    {
        var harness = new Harness();
        var tab = ViewFixtures.Node("tab", [ViewFixtures.Text("page")]);

        Control control = harness.Build(ViewFixtures.Document(ViewFixtures.Node("tabs", [tab])));

        Assert.Contains("needs prop 'title'", ViewFixtures.PlaceholderText(control));
    }

    [AvaloniaFact]
    public void Tabs_with_no_tab_fails_closed()
    {
        Control control = new Harness().Build(ViewFixtures.Document(ViewFixtures.Node("tabs", [])));

        Assert.Contains("needs at least one tab", ViewFixtures.PlaceholderText(control));
    }

    [AvaloniaFact]
    public void Tabs_with_a_child_that_is_not_a_tab_fails_closed()
    {
        var tabs = ViewFixtures.Node("tabs", [ViewFixtures.Text("loose")]);

        Control control = new Harness().Build(ViewFixtures.Document(tabs));

        Assert.Contains("must be a tab, not 'text'", ViewFixtures.PlaceholderText(control));
    }

    [AvaloniaFact]
    public void A_tab_with_an_empty_title_fails_closed()
    {
        var tab = ViewFixtures.Node("tab", [ViewFixtures.Text("page")], ("title", ViewFixtures.Lit("")));

        Control control = new Harness().Build(ViewFixtures.Document(ViewFixtures.Node("tabs", [tab])));

        Assert.Contains("non-empty title", ViewFixtures.PlaceholderText(control));
    }

    [AvaloniaFact]
    public void A_tab_outside_tabs_is_an_unknown_widget_and_fails_closed()
    {
        var stray = ViewFixtures.Node("tab", [ViewFixtures.Text("page")], ("title", ViewFixtures.Lit("Loose")));

        var stack = Assert.IsType<StackPanel>(new Harness().Build(ViewFixtures.Document(ViewFixtures.Node("stack", [stray]))));

        Assert.Contains("unknown widget", ViewFixtures.PlaceholderText(stack.Children[0]));
    }

    [AvaloniaFact]
    public void A_list_over_an_undeclared_source_fails_closed()
    {
        var node = ViewFixtures.Node("list", [ViewFixtures.Text("row")], ("over", ViewFixtures.Lit("nope")), ("key", ViewFixtures.Lit("id")));

        Control control = new Harness().Build(ViewFixtures.ListDocument(node));

        Assert.Contains("must name a declared list source, not 'nope'", ViewFixtures.PlaceholderText(control));
    }

    [AvaloniaFact]
    public void A_list_key_that_is_not_a_field_of_the_source_fails_closed()
    {
        var node = ViewFixtures.Node("list", [ViewFixtures.Text("row")], ("over", ViewFixtures.Lit("items")), ("key", ViewFixtures.Lit("sku")));

        Control control = new Harness().Build(ViewFixtures.ListDocument(node));

        Assert.Contains("must name a field of 'items'", ViewFixtures.PlaceholderText(control));
    }

    [AvaloniaFact]
    public void A_declared_placeholder_shows_its_message()
    {
        var harness = new Harness();

        Control control = harness.Build(ViewFixtures.Minimal("placeholder"));

        Assert.Contains("Coming later.", ViewFixtures.PlaceholderText(control));
    }

    [AvaloniaFact]
    public void A_literal_text_prop_is_shown_as_written()
    {
        // The schema has no localisation-key marker yet (doc 41c, section 7), so a string prop is always a literal.
        var harness = new Harness();
        var node = ViewFixtures.Node("text", [], ("value", ViewFixtures.Lit("greeting")));

        var text = Assert.IsType<TextBlock>(harness.Build(ViewFixtures.Document(node)));

        Assert.Equal("greeting", text.Text);
    }

    [AvaloniaFact]
    public void An_expression_prop_is_evaluated_against_the_declared_data()
    {
        var harness = new Harness();
        var document = new ViewDocument(
            ViewValidator.SupportedDslVersion,
            "bazaar.test.view",
            new Dictionary<string, ViewDataSource>(StringComparer.Ordinal) { ["count"] = new ViewDataSource("fixture:count", Bazaar.Rubedo.RubedoType.Number) },
            ViewFixtures.Node("text", [], ("value", new ViewExpr("count + 1"))),
            new Dictionary<string, ViewAction>());

        var text = Assert.IsType<TextBlock>(harness.Build(document, data: ViewFixtures.Data(("count", 41m))));

        Assert.Equal("42", text.Text);
    }
}
