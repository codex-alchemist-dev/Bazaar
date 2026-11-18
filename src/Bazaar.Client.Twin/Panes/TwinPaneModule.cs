using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Twin.Panes;

/// <summary>Registers the Digital Twin project card pane (<see cref="PaneKinds.Twin"/>).</summary>
public sealed class TwinPaneModule : IPaneModule
{
    public string Id => PaneKinds.Twin;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(PaneKinds.Twin, descriptor => new TwinPaneViewModel(descriptor));
        registry.RegisterView<TwinPaneViewModel>(TwinPaneViews.Card);
    }
}
