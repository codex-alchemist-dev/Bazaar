using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bazaar.Client.Views.Tests.Fixtures;
using Bazaar.Rubedo;
using Bazaar.Rubedo.Views;

namespace Bazaar.Client.Views.Tests;

/// <summary>
/// <c>visibleWhen</c> and <c>enabledWhen</c> (doc 41a section 1, doc 41c section 8) are bool Rubedo expressions on any node.
/// A bad condition fails the node closed, so it is never left visible or enabled by accident.
/// </summary>
public class ConditionTests
{
    private static ViewDocument CountDocument(ViewNode root) => new(
        ViewValidator.SupportedDslVersion,
        "bazaar.test.view",
        new Dictionary<string, ViewDataSource>(StringComparer.Ordinal) { ["count"] = new("fixture:count", RubedoType.Number) },
        root,
        new Dictionary<string, ViewAction>(StringComparer.Ordinal) { ["open"] = new(ViewActionKind.Navigate, "gc") });

    private static ViewNode Hello(params (string Name, object? Value)[] extra) =>
        ViewFixtures.Node("text", [], [("text", "hello"), .. extra]);

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_literal_visibleWhen_shows_or_hides_the_control(bool visible)
    {
        var harness = new Harness();

        Control control = harness.Build(CountDocument(Hello(("visibleWhen", visible))), data: ViewFixtures.Data(("count", 0m)));

        Assert.Equal(visible, control.IsVisible);
    }

    [AvaloniaTheory]
    [InlineData(0, false)]
    [InlineData(3, true)]
    public void An_expression_visibleWhen_follows_the_data(int count, bool expected)
    {
        var harness = new Harness();
        ViewNode node = Hello(("visibleWhen", ViewFixtures.Bind("count > 0")));

        Control control = harness.Build(CountDocument(node), data: ViewFixtures.Data(("count", (decimal)count)));

        Assert.Equal(expected, control.IsVisible);
        Assert.Null(ViewFixtures.AsPlaceholder(control));
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_bare_identifier_reads_a_bool_field(bool flag)
    {
        var harness = new Harness();
        ViewNode node = Hello(("visibleWhen", ViewFixtures.Bind("mentioned")));

        Control control = harness.Build(CountDocument(node), data: ViewFixtures.Data(("count", 0m), ("mentioned", flag)));

        Assert.Equal(flag, control.IsVisible);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void enabledWhen_enables_or_disables_a_button(bool enabled)
    {
        var harness = new Harness();
        ViewNode button = ViewFixtures.Node("button", [], ("label", "Open"), ("action", "open"), ("enabledWhen", enabled));

        Control control = harness.Build(CountDocument(button), data: ViewFixtures.Data(("count", 0m)));

        Assert.IsType<Button>(control);
        Assert.Equal(enabled, control.IsEnabled);
    }

    [AvaloniaFact]
    public void A_condition_that_is_not_a_bool_fails_the_node_closed()
    {
        var harness = new Harness();
        ViewNode node = Hello(("visibleWhen", "yes"));

        Control control = harness.Build(CountDocument(node), data: ViewFixtures.Data(("count", 0m)));

        Assert.Contains("must be a bool", ViewFixtures.PlaceholderText(control));
    }

    [AvaloniaFact]
    public void A_number_expression_is_not_a_condition()
    {
        var harness = new Harness();
        ViewNode node = Hello(("visibleWhen", ViewFixtures.Bind("count + 1")));

        Control control = harness.Build(CountDocument(node), data: ViewFixtures.Data(("count", 1m)));

        Assert.Contains("must be a bool", ViewFixtures.PlaceholderText(control));
    }

    [AvaloniaFact]
    public void A_hidden_node_leaves_its_siblings_alone()
    {
        var harness = new Harness();
        ViewNode root = ViewFixtures.Node("stack", [Hello(("visibleWhen", false)), ViewFixtures.Text("kept")]);

        var stack = Assert.IsType<StackPanel>(harness.Build(CountDocument(root), data: ViewFixtures.Data(("count", 0m))));

        Assert.False(stack.Children[0].IsVisible);
        Assert.True(stack.Children[1].IsVisible);
        Assert.Empty(ViewTree.Placeholders(stack));
    }
}
