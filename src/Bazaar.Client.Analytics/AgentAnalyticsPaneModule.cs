using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Modules;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Analytics;

/// <summary>Registers the company analytics pane (<see cref="PaneKinds.AgentAnalytics"/>, doc 25).</summary>
public sealed class AgentAnalyticsPaneModule : IPaneModule, IClientModule
{
    public ModuleManifest Manifest { get; } = FirstPartyManifest.Create(typeof(AgentAnalyticsPaneModule), PaneKinds.AgentAnalytics, [PaneKinds.AgentAnalytics], ["ui:pane"]);

    public void Register(ClientModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var kind in builder.Manifest.PaneKinds)
        {
            builder.AddPaneKind(kind);
        }
    }

    public string Id => PaneKinds.AgentAnalytics;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(PaneKinds.AgentAnalytics, descriptor => new AgentAnalyticsPaneViewModel(descriptor));
        registry.RegisterView<AgentAnalyticsPaneViewModel>(AgentAnalyticsPaneView.Create);
    }
}
