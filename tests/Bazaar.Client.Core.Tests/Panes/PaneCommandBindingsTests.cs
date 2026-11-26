using Bazaar.Client.Commands;
using Bazaar.Client.Constellation;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Dashboard;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.RyuseiFuda;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Tests.Panes;

public class PaneCommandBindingsTests
{
    private sealed class RecordingNavigator(bool result = true) : IPaneNavigator
    {
        public List<string> Shown { get; } = [];

        public bool Show(string paneKind)
        {
            Shown.Add(paneKind);
            return result;
        }
    }

    private static (CommandRouter Router, RecordingNavigator Navigator, List<string> Reports) Compose(bool navigatorResult = true)
    {
        var router = new CommandRouter();
        var navigator = new RecordingNavigator(navigatorResult);
        var reports = new List<string>();
        var features = new PaneModuleContext()
            .With<ICommandRouter>(router)
            .With<IPaneNavigator>(navigator);
        FirstPartyPanes.CreateRegistry(
            [new DashboardPaneModule(), new ConstellationPaneModule(), RyuseiFudaPanes.Board],
            reports.Add,
            features);
        return (router, navigator, reports);
    }

    [Fact]
    public void A_pane_module_command_shows_its_pane_through_the_navigator()
    {
        var (router, navigator, _) = Compose();

        Assert.True(router.TryInvoke(DashboardPaneModule.OpenCommandId));
        Assert.True(router.TryInvoke(ConstellationPaneModule.OpenCommandId));

        Assert.Equal([PaneKinds.Dashboard, PaneKinds.Constellation], navigator.Shown);
    }

    [Fact]
    public void A_layout_that_cannot_show_the_pane_is_reported()
    {
        var (router, _, reports) = Compose(navigatorResult: false);

        router.TryInvoke(ConstellationPaneModule.OpenCommandId);

        Assert.Contains(reports, r => r.Contains(ConstellationPaneModule.OpenCommandId, StringComparison.Ordinal));
    }

    [Fact]
    public void Without_a_router_the_modules_register_nothing_and_still_compose()
    {
        var registry = FirstPartyPanes.CreateRegistry([new DashboardPaneModule(), new ConstellationPaneModule()]);

        Assert.Contains(PaneKinds.Dashboard, registry.Kinds);
    }

    [Fact]
    public void The_late_navigator_shows_nothing_until_the_head_binds_it()
    {
        var navigator = new LatePaneNavigator();
        Assert.False(navigator.Show(PaneKinds.Kanban));

        var shown = new List<string>();
        navigator.Bind(kind =>
        {
            shown.Add(kind);
            return true;
        });

        Assert.True(navigator.Show(PaneKinds.Kanban));
        Assert.Equal([PaneKinds.Kanban], shown);
    }
}
