using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Bazaar.Client.SignIn.Avalonia;

namespace Bazaar.Client.SignIn.Tests;

public class SignInViewHeadlessTests
{
    private static T Find<T>(Control root, string id) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == id);

    [AvaloniaFact]
    public async Task The_screen_walks_from_the_page_to_a_signed_in_state_by_its_controls()
    {
        var service = new FakeService();
        var model = new SignInViewModel(service, new FakeLauncher());
        var window = new Window { Content = SignInView.Create(model) };
        window.Show();
        await model.StartAsync();

        Assert.True(Find<Button>(window, SignInView.Ids.OpenPage).IsVisible);
        Assert.False(Find<Button>(window, SignInView.Ids.Retry).IsVisible);
        Assert.False(Find<Button>(window, SignInView.Ids.SignOut).IsVisible);
        Assert.Contains("signin?audience=native", Find<SelectableTextBlock>(window, SignInView.Ids.PageAddress).Text);

        Find<TextBox>(window, SignInView.Ids.Code).Text = "abc123";
        Assert.True(model.SubmitCodeCommand.CanExecute(null));
        model.SubmitCodeCommand.Execute(null);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.True(Find<Button>(window, SignInView.Ids.SignOut).IsVisible);
        Assert.False(Find<TextBox>(window, SignInView.Ids.Code).IsVisible);
        Assert.Equal("You are signed in.", Find<TextBlock>(window, SignInView.Ids.Message).Text);
    }

    [AvaloniaFact]
    public async Task An_unreachable_server_shows_retry_and_no_code_box()
    {
        var model = new SignInViewModel(new FakeService { Reachability = ServerReachability.Unreachable }, new FakeLauncher());
        var window = new Window { Content = SignInView.Create(model) };
        window.Show();
        await model.StartAsync();

        Assert.True(Find<Button>(window, SignInView.Ids.Retry).IsVisible);
        Assert.False(Find<TextBox>(window, SignInView.Ids.Code).IsVisible);
        Assert.Contains("Cannot reach", Find<TextBlock>(window, SignInView.Ids.Message).Text);
    }
}
