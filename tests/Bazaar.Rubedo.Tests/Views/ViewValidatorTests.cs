using Bazaar.Rubedo.Semantics;
using Bazaar.Rubedo.Views;

namespace Bazaar.Rubedo.Tests.Views;

public class ViewValidatorTests
{
    private static ViewNode Node(string widget, Dictionary<string, object?>? props = null, params ViewNode[] children) =>
        new(widget, (IReadOnlyDictionary<string, object?>)(props ?? new Dictionary<string, object?>()), children);

    private static ViewDocument Doc(
        ViewNode root,
        Dictionary<string, ViewDataSource>? data = null,
        Dictionary<string, ViewAction>? actions = null,
        int dslVersion = ViewValidator.SupportedDslVersion,
        string name = "bazaar.gc.feed") =>
        new(dslVersion, name,
            (IReadOnlyDictionary<string, ViewDataSource>)(data ?? new Dictionary<string, ViewDataSource>()),
            root,
            (IReadOnlyDictionary<string, ViewAction>)(actions ?? new Dictionary<string, ViewAction>()));

    private static string[] Codes(ViewDocument document) =>
        ViewValidator.Validate(document).Select(d => d.Code).ToArray();

    [Fact]
    public void A_minimal_valid_document_has_no_problems()
    {
        var document = Doc(Node("stack", null, Node("text", new() { ["label"] = "Hello" })));

        Assert.Empty(ViewValidator.Validate(document));
    }

    [Fact]
    public void Unsupported_dsl_version_is_reported()
    {
        var document = Doc(Node("stack"), dslVersion: 2);

        Assert.Contains("view.dsl_version", Codes(document));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bazaar.gc")]
    [InlineData("bazaar..gc")]
    [InlineData("bazaar.GC")]
    public void Bad_view_names_are_reported(string name)
    {
        var document = Doc(Node("stack"), name: name);

        Assert.Contains("view.bad_name", Codes(document));
    }

    [Fact]
    public void Unknown_widget_is_reported_and_its_children_are_not_walked()
    {
        var document = Doc(Node("sparkle", null, Node("button")));

        Assert.Equal(["view.unknown_widget"], Codes(document));
    }

    [Theory]
    [InlineData("button")]
    [InlineData("icon")]
    [InlineData("input")]
    public void Widgets_that_need_a_label_report_when_it_is_missing(string widget)
    {
        var document = Doc(Node("stack", null, Node(widget)));

        Assert.Contains("view.missing_label", Codes(document));
    }

    [Fact]
    public void An_icon_without_a_name_is_reported()
    {
        var document = Doc(Node("icon", new() { ["label"] = "gc.feed.mention" }));

        Assert.Equal(["view.missing_icon_name"], Codes(document));
    }

    [Fact]
    public void An_icon_with_a_name_and_a_label_is_valid()
    {
        var document = Doc(Node("icon", new() { ["name"] = "check", ["label"] = "gc.feed.mention" }));

        Assert.Empty(Codes(document));
    }

    [Fact]
    public void An_empty_label_counts_as_missing()
    {
        var document = Doc(Node("button", new() { ["label"] = "" }));

        Assert.Contains("view.missing_label", Codes(document));
    }

    [Fact]
    public void A_button_pointing_at_an_undeclared_action_is_reported()
    {
        var document = Doc(Node("button", new() { ["label"] = "Go", ["action"] = "missing" }));

        Assert.Contains("view.unknown_action", Codes(document));
    }

    [Fact]
    public void A_button_pointing_at_a_declared_action_is_accepted()
    {
        var actions = new Dictionary<string, ViewAction> { ["open"] = new(ViewActionKind.Navigate, "settings") };
        var document = Doc(Node("button", new() { ["label"] = "Go", ["action"] = "open" }), actions: actions);

        Assert.Empty(ViewValidator.Validate(document));
    }

    [Fact]
    public void Leaf_widgets_with_children_are_reported()
    {
        var document = Doc(Node("text", new() { ["label"] = "x" }, Node("divider")));

        Assert.Contains("view.unexpected_children", Codes(document));
    }

    private static Dictionary<string, ViewDataSource> FeedSource() =>
        new() { ["feed"] = new("gc.feed", null, new Dictionary<string, RubedoType> { ["id"] = RubedoType.Text }) };

    private static Dictionary<string, object?> ListProps() =>
        new() { ["over"] = "feed", ["key"] = "id" };

    [Fact]
    public void A_list_needs_exactly_one_item_template()
    {
        var none = Doc(Node("list", ListProps()), data: FeedSource());
        var two = Doc(Node("list", ListProps(), Node("text", new() { ["label"] = "a" }), Node("text", new() { ["label"] = "b" })), data: FeedSource());

        Assert.Contains("view.list_template", Codes(none));
        Assert.Contains("view.list_template", Codes(two));
    }

    [Fact]
    public void A_list_with_one_template_is_accepted()
    {
        var document = Doc(Node("list", ListProps(), Node("text", new() { ["label"] = "a" })), data: FeedSource());

        Assert.Empty(ViewValidator.Validate(document));
    }

    [Fact]
    public void Data_source_with_an_invalid_name_is_reported()
    {
        var data = new Dictionary<string, ViewDataSource> { ["bad-name"] = new("gc.feed", RubedoType.Bool) };
        var document = Doc(Node("stack"), data: data);

        Assert.Contains("view.bad_data_name", Codes(document));
    }

    [Fact]
    public void Data_source_that_shadows_a_context_identifier_is_reported()
    {
        var identifiers = RubedoSchema.Standard.Identifiers.Keys.ToList();
        var shadow = identifiers.First();
        var data = new Dictionary<string, ViewDataSource> { [shadow] = new("gc.feed", RubedoType.Bool) };
        var document = Doc(Node("stack"), data: data);

        Assert.Contains("view.data_shadows_context", Codes(document));
    }

    [Fact]
    public void A_visibleWhen_that_is_not_boolean_is_reported_with_its_path()
    {
        var data = new Dictionary<string, ViewDataSource> { ["count"] = new("gc.feed", RubedoType.Number) };
        var root = Node("stack", null, Node("text", new() { ["label"] = "x", ["visibleWhen"] = new ViewExpr("count + 1") }));
        var diagnostics = ViewValidator.Validate(Doc(root, data: data));

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("result-type", diagnostic.Code);
        Assert.StartsWith("root/0.visibleWhen:", diagnostic.Message);
    }

    [Fact]
    public void A_visibleWhen_that_uses_a_declared_data_source_is_accepted()
    {
        var data = new Dictionary<string, ViewDataSource> { ["unread"] = new("gc.feed", RubedoType.Number) };
        var root = Node("stack", null, Node("text", new() { ["label"] = "x", ["visibleWhen"] = new ViewExpr("unread > 0") }));

        Assert.Empty(ViewValidator.Validate(Doc(root, data: data)));
    }

    [Fact]
    public void An_expression_that_does_not_parse_is_reported()
    {
        var root = Node("stack", null, Node("text", new() { ["label"] = "x", ["visibleWhen"] = new ViewExpr("(((") }));

        Assert.NotEmpty(ViewValidator.Validate(Doc(root)));
    }

    [Fact]
    public void Unknown_action_kind_is_reported()
    {
        var actions = new Dictionary<string, ViewAction> { ["weird"] = new((ViewActionKind)99, null) };
        var document = Doc(Node("stack"), actions: actions);

        Assert.Contains("view.unknown_action_kind", Codes(document));
    }

    [Theory]
    [InlineData("stack", ViewWidget.Stack)]
    [InlineData("placeholder", ViewWidget.Placeholder)]
    [InlineData("tabs", ViewWidget.Tabs)]
    public void Widget_wire_names_parse(string wire, ViewWidget expected)
    {
        Assert.True(ViewWidgets.TryParse(wire, out var widget));
        Assert.Equal(expected, widget);
        Assert.Equal(wire, ViewWidgets.WireName(widget));
    }

    [Fact]
    public void Widget_wire_names_are_case_sensitive()
    {
        Assert.False(ViewWidgets.TryParse("Stack", out _));
    }
}
