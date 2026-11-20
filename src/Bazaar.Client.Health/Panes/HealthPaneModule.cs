using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Health.Panes;

/// <summary>Registers the project health pane (<see cref="PaneKinds.Health"/>).</summary>
public sealed class HealthPaneModule : IPaneModule
{
    public string Id => PaneKinds.Health;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(PaneKinds.Health, descriptor => new HealthPaneViewModel(descriptor));
        registry.RegisterView<HealthPaneViewModel>(HealthPaneViews.Health);
    }
}
