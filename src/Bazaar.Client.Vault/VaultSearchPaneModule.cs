using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Vault;

/// <summary>Registers the Vault search pane (<see cref="PaneKinds.VaultSearch"/>).</summary>
public sealed class VaultSearchPaneModule : IPaneModule
{
    public string Id => PaneKinds.VaultSearch;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        var vaultApi = context.Get<VaultApi>() ?? VaultApiFactory.Create(ClientServer.Current);
        registry.Register(PaneKinds.VaultSearch, descriptor => new VaultSearchPaneViewModel(descriptor, vaultApi));
        registry.RegisterView<VaultSearchPaneViewModel>(pane => new VaultSearchView(pane));
    }
}
