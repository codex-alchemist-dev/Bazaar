namespace Bazaar.Client.SignIn.Tests;

public class SignInViewModelTests
{
    private static (SignInViewModel Model, FakeService Service, FakeLauncher Launcher) Make(Action<FakeService>? arrange = null, bool opens = true)
    {
        var service = new FakeService();
        arrange?.Invoke(service);
        var launcher = new FakeLauncher(opens);
        return (new SignInViewModel(service, launcher), service, launcher);
    }

    [Theory]
    [InlineData(ServerReachability.Unreachable, SignInPhase.Unreachable)]
    [InlineData(ServerReachability.NotReady, SignInPhase.NotReady)]
    [InlineData(ServerReachability.Ready, SignInPhase.SignedOut)]
    public async Task Start_shows_where_the_server_stands(ServerReachability reachability, SignInPhase expected)
    {
        var (model, _, _) = Make(s => s.Reachability = reachability);

        await model.StartAsync();

        Assert.Equal(expected, model.Phase);
        Assert.False(string.IsNullOrWhiteSpace(model.Message));
    }

    [Fact]
    public async Task Retry_is_offered_only_when_the_server_is_not_up_and_runs_the_probe_again()
    {
        var (model, service, _) = Make(s => s.Reachability = ServerReachability.Unreachable);
        await model.StartAsync();
        Assert.True(model.RetryCommand.CanExecute(null));

        service.Reachability = ServerReachability.Ready;
        await model.StartAsync();

        Assert.Equal(SignInPhase.SignedOut, model.Phase);
        Assert.False(model.RetryCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_saved_unexpired_session_signs_in_without_a_code()
    {
        var (model, _, _) = Make(s => s.Session = new ClientSession("a", "r", DateTimeOffset.UtcNow.AddMinutes(5)));
        var raised = 0;
        model.SignedIn += (_, _) => raised++;

        await model.StartAsync();

        Assert.Equal(SignInPhase.SignedIn, model.Phase);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task An_expired_session_is_refreshed_and_a_refused_refresh_falls_back_to_the_code()
    {
        var (model, service, _) = Make(s => s.Session = new ClientSession("a", "r", DateTimeOffset.UtcNow.AddMinutes(-5)));
        await model.StartAsync();
        Assert.Equal(SignInPhase.SignedOut, model.Phase);

        service.RefreshResult = new SignInResult(SignInOutcome.SignedIn, "ok");
        await model.StartAsync();
        Assert.Equal(SignInPhase.SignedIn, model.Phase);
    }

    [Fact]
    public async Task The_sign_in_page_opens_in_the_browser_and_a_failed_launch_says_where_to_go()
    {
        var (model, _, launcher) = Make();
        await model.StartAsync();

        model.OpenSignInPageCommand.Execute(null);
        await Task.Yield();
        Assert.Equal("/signin", Assert.Single(launcher.Opened).AbsolutePath);

        var (noBrowser, _, _) = Make(opens: false);
        await noBrowser.StartAsync();
        noBrowser.OpenSignInPageCommand.Execute(null);
        await Task.Yield();
        Assert.Contains("address", noBrowser.Message);
    }

    [Fact]
    public async Task Submit_needs_a_well_formed_code_and_signs_in()
    {
        var (model, service, _) = Make();
        await model.StartAsync();
        Assert.False(model.SubmitCodeCommand.CanExecute(null));

        model.CodeText = "not a code!";
        Assert.False(model.SubmitCodeCommand.CanExecute(null));

        model.CodeText = "bazaar://signin?code=abc123";
        Assert.True(model.SubmitCodeCommand.CanExecute(null));
        model.SubmitCodeCommand.Execute(null);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(["bazaar://signin?code=abc123"], service.Redeemed);
        Assert.Equal(SignInPhase.SignedIn, model.Phase);
        Assert.Equal("", model.CodeText);
    }

    [Fact]
    public async Task A_refused_code_returns_to_the_code_step_with_the_reason()
    {
        var (model, _, _) = Make(s => s.RedeemResult = new SignInResult(SignInOutcome.Refused, "Sign-in was refused."));
        await model.StartAsync();

        model.CodeText = "abc123";
        model.SubmitCodeCommand.Execute(null);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(SignInPhase.SignedOut, model.Phase);
        Assert.Equal("Sign-in was refused.", model.Message);
        Assert.Equal("", model.CodeText);
    }

    [Fact]
    public async Task A_forwarded_app_address_redeems_straight_away_and_is_ignored_when_not_waiting_for_a_code()
    {
        var (model, service, _) = Make();
        await model.AcceptLaunchAddressAsync("bazaar://signin?code=early1");
        Assert.Empty(service.Redeemed);

        await model.StartAsync();
        await model.AcceptLaunchAddressAsync("bazaar://signin?code=abc123");
        Assert.Equal(["abc123"], service.Redeemed);
        Assert.Equal(SignInPhase.SignedIn, model.Phase);

        await model.AcceptLaunchAddressAsync("bazaar://signin?code=late1");
        Assert.Single(service.Redeemed);
    }

    [Fact]
    public async Task Sign_out_forgets_the_session()
    {
        var (model, service, _) = Make(s => s.Session = new ClientSession("a", "r", DateTimeOffset.UtcNow.AddMinutes(5)));
        await model.StartAsync();

        model.SignOutCommand.Execute(null);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Null(service.Session);
        Assert.Equal(SignInPhase.SignedOut, model.Phase);
    }
}
