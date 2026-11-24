using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Bazaar.Client.Walkthrough.Avalonia;

namespace Bazaar.Client.Walkthrough.Tests;

public class WalkthroughViewHeadlessTests
{
    private static T Find<T>(Control root, string id) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == id);

    [AvaloniaFact]
    public async Task The_card_is_driven_by_its_controls_and_hides_on_skip()
    {
        var model = new WalkthroughViewModel(new FakeStore(), new FakeFacts(WalkthroughFacts.None), new FakeActions());
        var window = new Window { Content = WalkthroughView.Create(model) };
        window.Show();
        await model.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Welcome to your company", Find<TextBlock>(window, WalkthroughView.Ids.Title).Text);
        Assert.Equal("Step 1 of 5", Find<TextBlock>(window, WalkthroughView.Ids.Progress).Text);
        Assert.False(Find<Button>(window, WalkthroughView.Ids.Action).IsVisible);

        Find<Button>(window, WalkthroughView.Ids.Next).Command!.Execute(null);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal("Create your company", Find<TextBlock>(window, WalkthroughView.Ids.Title).Text);
        Assert.True(Find<Button>(window, WalkthroughView.Ids.Action).IsVisible);
        Assert.Equal("Open create company", Find<Button>(window, WalkthroughView.Ids.Action).Content);

        Find<Button>(window, WalkthroughView.Ids.Skip).Command!.Execute(null);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.False(Find<Border>(window, WalkthroughView.Ids.Card).IsVisible);
    }
}
