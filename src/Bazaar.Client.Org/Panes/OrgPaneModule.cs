using Bazaar.Client.Core.Panes;

namespace Bazaar.Client.Org.Panes;

/// <summary>Registers the Org chart pane.</summary>
public sealed class OrgPaneModule : IPaneModule
{
    // Matches PaneKinds.Org ("bazaar.org") once Bazaar.Contracts carries that constant; kept local until then.
    internal const string Kind = "bazaar.org";

    public string Id => Kind;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(Kind, descriptor => new OrgPaneViewModel(descriptor));
        registry.RegisterView<OrgPaneViewModel>(OrgPaneViews.Org);
    }
}
