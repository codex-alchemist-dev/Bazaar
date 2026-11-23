using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Bazaar.Client.Hive.Grants;

namespace Bazaar.Client.Hive.Tests;

public class HivePaneViewTests
{
    [AvaloniaFact]
    public void View_shows_the_copies_only_notice_labels_and_deprecated_copies()
    {
        var view = HiveTestData.CreateRegistry().CreateView(
            HiveTestData.CreateRegistry().Create(HiveTestData.Descriptor(HiveTestData.Model)));
        var texts = view.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text ?? string.Empty).ToList();

        Assert.Contains(texts, text => text.StartsWith("Copies only.", StringComparison.Ordinal));
        Assert.Contains("hive/procedure/retry.md · origin alpha · procedural · trust T2 · active", texts);
        Assert.Contains(texts, text => text.StartsWith("[deprecated] hive/procedure/old.md", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Grant_buttons_are_disabled_without_actions()
    {
        var registry = HiveTestData.CreateRegistry();
        var view = registry.CreateView(registry.Create(HiveTestData.Descriptor(HiveTestData.Model)));

        var buttons = view.GetLogicalDescendants().OfType<Button>().ToList();

        Assert.NotEmpty(buttons);
        Assert.All(buttons, button => Assert.False(button.IsEnabled));
    }

    [AvaloniaFact]
    public void Clicking_a_toggle_requests_the_grant_and_shows_the_status()
    {
        var fake = new FakeHiveGrantActions();
        var registry = HiveTestData.CreateRegistry(fake);
        var view = registry.CreateView(registry.Create(HiveTestData.Descriptor(HiveTestData.Model)));

        // Kinds render in sorted order, so "failure" comes first and its alpha row holds the first "beta: not granted" toggle.
        var toggle = view.GetLogicalDescendants().OfType<Button>()
            .First(button => button.Content is string text && text == "beta: not granted" && IsUnder(button, "alpha"));
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var call = Assert.Single(fake.Calls);
        Assert.Equal(("grant", new HiveGrantKey("alpha", "beta", "failure")), call);
        var status = view.GetLogicalDescendants().OfType<TextBlock>()
            .Single(text => text.Text == "Requested. The matrix changes when the server confirms.");
        Assert.NotNull(status);
    }

    /// <summary>True when the button sits in the row of <paramref name="fromProject"/>. The row label is its first text block.</summary>
    private static bool IsUnder(Button button, string fromProject)
    {
        var row = button.GetLogicalAncestors().OfType<WrapPanel>().First();
        return row.GetLogicalChildren().OfType<TextBlock>().First().Text == fromProject;
    }
}
