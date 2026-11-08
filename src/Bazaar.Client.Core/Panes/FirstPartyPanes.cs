using Bazaar.Client.Core.Constellation;
using Bazaar.Client.Core.Server;
using Bazaar.Client.Core.Vault;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Panes;

/// <summary>Registers the panes every client ships with.</summary>
public static class FirstPartyPanes
{
    // TODO(component): the kernel (Bazaar.Server.Components, IBazaarComponent) is server-side and the client has no DI
    // container yet. Decide how client components register (a head composition root or a client kernel), then turn this
    // into per-component registrations. Until then the heads call CreateRegistry.
    /// <param name="report">Called once per kind that falls back.</param>
    /// <param name="vault">The Vault API to use. Null builds one for <see cref="ClientServer.Current"/>.</param>
    public static PaneRegistry CreateRegistry(Action<string>? report = null, VaultApi? vault = null)
    {
        var registry = new PaneRegistry(report);
        registry.Register(PaneKinds.Kanban, descriptor => new KanbanPaneViewModel(descriptor));
        registry.Register(PaneKinds.Dashboard, descriptor => new DashboardPaneViewModel(descriptor));
        registry.Register(PaneKinds.Constellation, descriptor => new ConstellationPaneViewModel(descriptor));
        var vaultApi = vault ?? VaultApiFactory.Create(ClientServer.Current);
        registry.Register(PaneKinds.VaultSearch, descriptor => new VaultSearchPaneViewModel(descriptor, vaultApi));

        return registry;
    }
}
