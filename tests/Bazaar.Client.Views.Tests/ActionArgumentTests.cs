using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Bazaar.Client.Views.Tests.Fixtures;
using Bazaar.Rubedo.Views;

namespace Bazaar.Client.Views.Tests;

/// <summary>Button arguments (doc 41c, section 6.1): resolved in the button's scope when clicked, and failed closed when the head cannot take them.</summary>
public class ActionArgumentTests
{
    private static ViewDocument RowButtons(ViewNode button) =>
        ViewFixtures.ListDocument(ViewFixtures.Node("list", [button], ("over", ViewFixtures.Lit("items")), ("key", ViewFixtures.Lit("id"))))
            with
        {
            Actions = new Dictionary<string, ViewAction>(StringComparer.Ordinal)
            {
                ["pause"] = new(ViewActionKind.Invoke, "ryusei.pause"),
            },
        };

    private static ViewNode PauseButton(ViewArgs args) => ViewFixtures.Node(
        "button",
        [],
        ("label", ViewFixtures.Lit("Pause")),
        ("action", ViewFixtures.Lit("pause")),
        (ViewArgs.PropName, args));

    private static void Click(Control control) => ((Button)control).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public void Each_row_button_sends_its_own_row_values()
    {
        var harness = new Harness();
        var args = new ViewArgs(new Dictionary<string, object?>
        {
            ["id"] = ViewFixtures.Bind("id"),
            ["paused"] = true,
            ["limit"] = 3.0,
        });

        var list = Assert.IsType<KeyedListPanel>(harness.Build(RowButtons(PauseButton(args))));
        foreach (Control row in list.Children)
        {
            Click(row);
        }

        Assert.Equal(
            ["invoke:ryusei.pause?id=a&limit=3&paused=True", "invoke:ryusei.pause?id=b&limit=3&paused=True"],
            harness.Actions.Calls);
        Assert.Empty(harness.Errors);
    }

    [AvaloniaFact]
    public void A_head_without_argument_overloads_fails_the_action_closed()
    {
        var errors = new List<Exception>();
        var mapper = new ViewMapper(new ViewContext(ViewFixtures.Theme(), new NoArgsActions(), new FakeLocalizer(), errors.Add));
        var args = new ViewArgs(new Dictionary<string, object?> { ["id"] = ViewFixtures.Bind("id") });

        var list = Assert.IsType<KeyedListPanel>(mapper.Build(RowButtons(PauseButton(args)), Harness.DefaultData()));
        Click(list.Children[0]);

        Assert.IsType<NotSupportedException>(Assert.Single(errors));
    }

    [AvaloniaFact]
    public void An_argument_that_does_not_evaluate_fails_the_click_not_the_build()
    {
        var harness = new Harness();
        var args = new ViewArgs(new Dictionary<string, object?> { ["id"] = ViewFixtures.Bind("missing.field") });

        var list = Assert.IsType<KeyedListPanel>(harness.Build(RowButtons(PauseButton(args))));
        Click(list.Children[0]);

        Assert.Empty(harness.Actions.Calls);
        Assert.Single(harness.Errors);
    }

    [AvaloniaFact]
    public void A_button_without_args_still_uses_the_plain_overload()
    {
        var harness = new Harness();
        var node = ViewFixtures.Node("button", [], ("label", ViewFixtures.Lit("Open")), ("action", ViewFixtures.Lit("open")));

        var button = harness.Build(ViewFixtures.Document(node, ("open", new ViewAction(ViewActionKind.Navigate, "gc.feed"))));
        Click(button);

        Assert.Equal(["navigate:gc.feed"], harness.Actions.Calls);
    }
}
