using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Modules;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Analytics;

/// <summary>Registers the company analytics pane (<see cref="PaneKinds.Analytics"/>, doc 25).</summary>
public sealed class AnalyticsPaneModule : IPaneModule, IClientModule
{
    public ModuleManifest Manifest { get; } = FirstPartyManifest.Create(typeof(AnalyticsPaneModule), PaneKinds.Analytics, [PaneKinds.Analytics], ["ui:pane", "http:analytics.read"]);

    public void Register(ClientModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var kind in builder.Manifest.PaneKinds)
        {
            builder.AddPaneKind(kind);
        }
    }

    public string Id => PaneKinds.Analytics;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(PaneKinds.Analytics, descriptor => new AnalyticsPaneViewModel(descriptor));
        registry.RegisterView<AnalyticsPaneViewModel>(AnalyticsPaneView.Create);
    }
}
