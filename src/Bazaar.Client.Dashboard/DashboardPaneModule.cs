using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Dashboard;

/// <summary>Registers the start-screen dashboard (<see cref="PaneKinds.Dashboard"/>).</summary>
public sealed class DashboardPaneModule : IPaneModule
{
    public string Id => PaneKinds.Dashboard;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(PaneKinds.Dashboard, descriptor => new DashboardPaneViewModel(descriptor));
        registry.RegisterView<DashboardPaneViewModel>(DashboardPaneView.Create);
    }
}
