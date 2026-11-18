using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Bazaar.Client.Views.Tests.Fixtures;
using Bazaar.Rubedo.Views;

namespace Bazaar.Client.Views.Tests;

/// <summary>The four closed actions (doc 41c, section 6) run through IViewActions, and a failure is reported, not lost.</summary>
public class ActionDispatchTests
{
    private static ViewDocument ActionsDocument() => ViewFixtures.Document(
        ViewFixtures.Node(
            "stack",
            [
                ViewFixtures.Node("button", [], ("label", ViewFixtures.Lit("Go")), ("action", ViewFixtures.Lit("nav"))),
                ViewFixtures.Node("button", [], ("label", ViewFixtures.Lit("Run")), ("action", ViewFixtures.Lit("run"))),
                ViewFixtures.Node("button", [], ("label", ViewFixtures.Lit("Pane")), ("action", ViewFixtures.Lit("pane"))),
                ViewFixtures.Node("input", [], ("label", ViewFixtures.Lit("Name")), ("value", ViewFixtures.Lit("")), ("onChange", ViewFixtures.Lit("name"))),
            ]),
        ("nav", new ViewAction(ViewActionKind.Navigate, "gc.feed")),
        ("run", new ViewAction(ViewActionKind.Invoke, "kanban.refresh")),
        ("pane", new ViewAction(ViewActionKind.OpenPane, "vault")),
        ("name", new ViewAction(ViewActionKind.SetSetting, "displayName")));

    [AvaloniaFact]
    public void Each_of_the_four_actions_dispatches_through_the_head_interface()
    {
        var harness = new Harness();
        var stack = Assert.IsType<StackPanel>(harness.Build(ActionsDocument()));
        var buttons = stack.Children.OfType<Button>().ToList();

        foreach (Button button in buttons)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        var input = stack.Children.OfType<TextBox>().Single();
        input.Text = "Ada";

        Assert.Equal(
            ["navigate:gc.feed", "invoke:kanban.refresh", "openPane:vault", "setSetting:displayName=Ada"],
            harness.Actions.Calls);
        Assert.Empty(harness.Errors);
    }

    [AvaloniaFact]
    public void Building_an_input_does_not_write_its_value()
    {
        var harness = new Harness();

        harness.Build(ActionsDocument());

        Assert.Empty(harness.Actions.Calls);
    }

    [AvaloniaFact]
    public void A_setting_action_on_a_button_has_no_value_and_fails_closed()
    {
        var harness = new Harness();
        var node = ViewFixtures.Node("button", [], ("label", ViewFixtures.Lit("Save")), ("action", ViewFixtures.Lit("name")));

        var control = harness.Build(ViewFixtures.Document(node, ("name", new ViewAction(ViewActionKind.SetSetting, "displayName"))));

        Assert.Contains("belongs on an input", ViewFixtures.PlaceholderText(control));
    }

    [AvaloniaFact]
    public void An_input_whose_change_names_a_non_setting_action_fails_closed()
    {
        var harness = new Harness();
        var node = ViewFixtures.Node("input", [], ("label", ViewFixtures.Lit("Go")), ("value", ViewFixtures.Lit("")), ("onChange", ViewFixtures.Lit("nav")));

        var control = harness.Build(ViewFixtures.Document(node, ("nav", new ViewAction(ViewActionKind.Navigate, "x"))));

        Assert.Contains("must name a setSetting action", ViewFixtures.PlaceholderText(control));
    }

    [AvaloniaFact]
    public void A_failing_action_is_reported_through_the_error_callback()
    {
        var harness = new Harness { Actions = { FailWith = new InvalidOperationException("head refused") } };
        var stack = Assert.IsType<StackPanel>(harness.Build(ActionsDocument()));
        var button = stack.Children.OfType<Button>().First();

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var error = Assert.Single(harness.Errors);
        Assert.Equal("head refused", error.Message);
    }
}
