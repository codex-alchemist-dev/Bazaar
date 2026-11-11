using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Panes;

/// <summary>Registers the start-screen dashboard (<see cref="PaneKinds.Dashboard"/>).</summary>
public sealed class DashboardPaneModule : IPaneModule
{
    public string Id => PaneKinds.Dashboard;

    public void Register(PaneRegistry registry, PaneModuleContext context) =>
        registry.Register(PaneKinds.Dashboard, descriptor => new DashboardPaneViewModel(descriptor));
}
