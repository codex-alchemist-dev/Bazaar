using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.Theme;

namespace Bazaar.Client.Views.Tests;

public class HeadWiringTests
{
    [Fact]
    public void The_built_in_dark_theme_passes_the_theme_validator()
    {
        Assert.Equal("bazaar-dark", BuiltInThemes.Dark.Name);
        Assert.Equal(ThemeBase.Dark, BuiltInThemes.Dark.Base);
    }

    [Fact]
    public async Task An_unwired_action_fails_closed_and_a_wired_one_runs()
    {
        var seen = new List<string>();
        var router = new ViewActionRouter(openPane: kind => { seen.Add(kind); return Task.CompletedTask; });

        await router.OpenPaneAsync("bazaar.org");
        await Assert.ThrowsAsync<NotSupportedException>(() => router.InvokeAsync("x"));
        await Assert.ThrowsAsync<NotSupportedException>(() => router.NavigateAsync("x"));
        await Assert.ThrowsAsync<NotSupportedException>(() => router.SetSettingAsync("k", "v"));

        Assert.Equal(["bazaar.org"], seen);
    }

    [Fact]
    public void The_view_context_travels_in_the_pane_context_and_is_absent_when_not_supplied()
    {
        var view = ViewContextServices.CreateDefault(new ViewActionRouter(), _ => { });

        Assert.Same(view, new PaneModuleContext().WithViewContext(view).GetViewContext());
        Assert.Null(new PaneModuleContext().GetViewContext());
        Assert.Equal("some.key", view.Localizer.Text("some.key"));
    }
}

public class ViewActionServicesTests
{
    private sealed class Nav : Bazaar.Client.Navigation.Abstractions.IPaneNavigator
    {
        public List<string> Calls { get; } = [];

        public Task OpenPaneAsync(string paneKind, CancellationToken cancellationToken = default)
        {
            Calls.Add("open:" + paneKind);
            return Task.CompletedTask;
        }

        public Task NavigateAsync(string target, CancellationToken cancellationToken = default)
        {
            Calls.Add("nav:" + target);
            return Task.CompletedTask;
        }
    }

    private sealed class Commands : Bazaar.Client.Navigation.Abstractions.ICommandRunner
    {
        public List<string> Ran { get; } = [];

        public Task RunAsync(string commandId, CancellationToken cancellationToken = default)
        {
            Ran.Add(commandId);
            return Task.CompletedTask;
        }
    }

    private sealed class Store : Bazaar.Client.Preferences.Abstractions.IPreferenceStore
    {
#pragma warning disable CS0067
        public event Action<string>? Changed;
#pragma warning restore CS0067

        public string? GetValue(string key) => null;

        public bool GetBool(string key) => false;

        public long GetInt(string key) => 0;

        public Bazaar.Client.Preferences.Abstractions.PreferenceResult TrySet(string key, string value) =>
            key == "ok.key"
                ? Bazaar.Client.Preferences.Abstractions.PreferenceResult.Ok
                : new(Bazaar.Client.Preferences.Abstractions.PreferenceRefusal.UnknownKey, "not declared");

        public Bazaar.Client.Preferences.Abstractions.PreferenceResult ApplyFromServer(string key, string value) => TrySet(key, value);

        public bool IsDeclared(string key) => key == "ok.key";
    }

    [Fact]
    public async Task The_four_actions_reach_the_navigator_the_runner_and_the_store_and_a_refused_setting_throws()
    {
        var nav = new Nav();
        var commands = new Commands();
        var router = ViewActionRouter.ForServices(nav, commands, new Store());

        await router.NavigateAsync("gc:general");
        await router.OpenPaneAsync("bazaar.org");
        await router.InvokeAsync("a.b");
        await router.SetSettingAsync("ok.key", "1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => router.SetSettingAsync("other", "1"));

        Assert.Equal(["nav:gc:general", "open:bazaar.org"], nav.Calls);
        Assert.Equal(["a.b"], commands.Ran);
    }
}
