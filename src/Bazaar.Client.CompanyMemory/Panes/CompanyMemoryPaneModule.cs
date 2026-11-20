using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.CompanyMemory.Panes;

/// <summary>Registers the company memory decision chain pane (<see cref="PaneKinds.CompanyMemory"/>).</summary>
public sealed class CompanyMemoryPaneModule : IPaneModule
{
    public string Id => PaneKinds.CompanyMemory;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(PaneKinds.CompanyMemory, descriptor => new CompanyMemoryPaneViewModel(descriptor));
        registry.RegisterView<CompanyMemoryPaneViewModel>(CompanyMemoryPaneViews.DecisionChain);
    }
}
