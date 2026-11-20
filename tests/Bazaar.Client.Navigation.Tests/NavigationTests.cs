using Bazaar.Client.Navigation.Abstractions;

namespace Bazaar.Client.Navigation.Tests;

public class NavigationTests
{
    private sealed class Surface : IPaneSurface
    {
        public List<string> Shown { get; } = [];

        public bool CanShow(string paneKind) => paneKind.StartsWith("bazaar.", StringComparison.Ordinal);

        public Task ShowAsync(string paneKind, CancellationToken cancellationToken)
        {
            Shown.Add(paneKind);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_known_pane_kind_is_shown_and_an_unknown_one_fails_closed()
    {
        var surface = new Surface();
        var nav = new PaneNavigator(surface);

        await nav.OpenPaneAsync("bazaar.org", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<NavigationException>(() => nav.OpenPaneAsync("evil.pane", TestContext.Current.CancellationToken));

        Assert.Equal(["bazaar.org"], surface.Shown);
    }

    [Fact]
    public async Task A_target_with_no_scheme_is_a_pane_kind()
    {
        var surface = new Surface();

        await new PaneNavigator(surface).NavigateAsync("bazaar.gc", TestContext.Current.CancellationToken);

        Assert.Equal(["bazaar.gc"], surface.Shown);
    }

    [Fact]
    public async Task A_scheme_goes_to_its_handler_with_the_rest_of_the_target()
    {
        var nav = new PaneNavigator(new Surface());
        string? seen = null;
        nav.RegisterScheme("gc", (value, _) =>
        {
            seen = value;
            return Task.CompletedTask;
        });

        await nav.NavigateAsync("gc:general:2", TestContext.Current.CancellationToken);

        Assert.Equal("general:2", seen);
    }

    [Fact]
    public async Task An_unknown_scheme_fails_closed_and_a_scheme_cannot_be_claimed_twice()
    {
        var nav = new PaneNavigator(new Surface());
        nav.RegisterScheme("gc", (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<NavigationException>(() => nav.NavigateAsync("javascript:alert(1)", TestContext.Current.CancellationToken));
        Assert.Throws<InvalidOperationException>(() => nav.RegisterScheme("gc", (_, _) => Task.CompletedTask));
    }

    [Fact]
    public async Task A_registered_command_runs_and_an_unregistered_or_disabled_one_is_refused()
    {
        var runner = new CommandRunner();
        int runs = 0;
        bool on = true;
        runner.Register("a.b", _ =>
        {
            runs++;
            return Task.CompletedTask;
        }, () => on);

        await runner.RunAsync("a.b", TestContext.Current.CancellationToken);
        on = false;
        await Assert.ThrowsAsync<NavigationException>(() => runner.RunAsync("a.b", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NavigationException>(() => runner.RunAsync("nope", TestContext.Current.CancellationToken));

        Assert.Equal(1, runs);
        Assert.Throws<InvalidOperationException>(() => runner.Register("a.b", _ => Task.CompletedTask));
    }
}
