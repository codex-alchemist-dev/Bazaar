using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Panes;

/// <summary>Registers the RyuseiFuda schedule pane (<see cref="PaneKinds.RyuseiSchedule"/>).</summary>
public sealed class RyuseiSchedulePaneModule : IPaneModule
{
    public string Id => PaneKinds.RyuseiSchedule;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(PaneKinds.RyuseiSchedule, descriptor => new RyuseiSchedulePaneViewModel(descriptor));
        registry.RegisterView<RyuseiSchedulePaneViewModel>(PaneViews.RyuseiSchedule);
    }
}
