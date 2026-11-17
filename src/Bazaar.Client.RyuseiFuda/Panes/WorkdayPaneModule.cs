using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.RyuseiFuda.Panes;

/// <summary>Registers the RyuseiFuda Workday pane (<see cref="PaneKinds.Workday"/>).</summary>
public sealed class WorkdayPaneModule : IPaneModule
{
    public string Id => PaneKinds.Workday;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(PaneKinds.Workday, descriptor => new WorkdayPaneViewModel(descriptor));
        registry.RegisterView<WorkdayPaneViewModel>(RyuseiFudaPaneViews.Workday);
    }
}
