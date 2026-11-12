using Bazaar.Client.Core.Ryusei;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Panes;

/// <summary>
/// Registers the RyuseiFuda schedule pane (<see cref="PaneKinds.RyuseiSchedule"/>). The pause and resume actions come
/// from the head through <see cref="PaneModuleContext.With{T}"/>; with none, the pane's buttons stay disabled.
/// </summary>
public sealed class RyuseiSchedulePaneModule : IPaneModule
{
    public string Id => PaneKinds.RyuseiSchedule;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        var actions = context.Get<IRyuseiTriggerActions>();
        registry.Register(PaneKinds.RyuseiSchedule, descriptor => new RyuseiSchedulePaneViewModel(descriptor, actions));
        registry.RegisterView<RyuseiSchedulePaneViewModel>(PaneViews.RyuseiSchedule);
    }
}
