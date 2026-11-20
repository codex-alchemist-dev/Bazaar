using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Modules;

namespace Bazaar.Client.Org.Panes;

/// <summary>Registers the Org chart pane.</summary>
public sealed class OrgPaneModule : IPaneModule, IClientModule
{
    // Matches PaneKinds.Org ("bazaar.org") once Bazaar.Contracts carries that constant; kept local until then.
    internal const string Kind = Bazaar.Contracts.Panes.PaneKinds.Org;

    /// <summary>The manifest the host checks at load (doc 41 section 8).</summary>
    public ModuleManifest Manifest { get; } = FirstPartyManifest.Create(typeof(OrgPaneModule), Kind, [Kind], ["ui:pane"]);

    /// <summary>Records the pane kinds this module registers, as its manifest declares them.</summary>
    public void Register(ClientModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var kind in builder.Manifest.PaneKinds)
        {
            builder.AddPaneKind(kind);
        }
    }

    public string Id => Kind;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(Kind, descriptor => new OrgPaneViewModel(descriptor));
        registry.RegisterView<OrgPaneViewModel>(OrgPaneViews.Org);
    }
}
