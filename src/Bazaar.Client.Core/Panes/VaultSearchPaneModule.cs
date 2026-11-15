using Bazaar.Client.Core.Server;
using Bazaar.Client.Core.Vault;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Panes;

/// <summary>Registers the Vault search pane (<see cref="PaneKinds.VaultSearch"/>).</summary>
public sealed class VaultSearchPaneModule : IPaneModule
{
    public string Id => PaneKinds.VaultSearch;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        var vaultApi = context.Vault ?? VaultApiFactory.Create(ClientServer.Current);
        registry.Register(PaneKinds.VaultSearch, descriptor => new VaultSearchPaneViewModel(descriptor, vaultApi));
        registry.RegisterView<VaultSearchPaneViewModel>(pane => new VaultSearchView(pane));
    }
}
