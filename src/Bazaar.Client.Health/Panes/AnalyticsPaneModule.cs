using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Health.Panes;

/// <summary>Registers the analytics pane (<see cref="PaneKinds.Analytics"/>).</summary>
public sealed class AnalyticsPaneModule : IPaneModule
{
    public string Id => PaneKinds.Analytics;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(PaneKinds.Analytics, descriptor => new AnalyticsPaneViewModel(descriptor));
        registry.RegisterView<AnalyticsPaneViewModel>(HealthPaneViews.Analytics);
    }
}
