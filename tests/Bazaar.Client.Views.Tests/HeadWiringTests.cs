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
