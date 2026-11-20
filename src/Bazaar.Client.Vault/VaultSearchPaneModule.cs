using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Modules;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Vault;

/// <summary>Registers the Vault search pane (<see cref="PaneKinds.VaultSearch"/>).</summary>
public sealed class VaultSearchPaneModule : IPaneModule, IClientModule
{
    /// <summary>The manifest the host checks at load (doc 41 section 8).</summary>
    public ModuleManifest Manifest { get; } = FirstPartyManifest.Create(typeof(VaultSearchPaneModule), PaneKinds.VaultSearch, [PaneKinds.VaultSearch], ["ui:pane", "wire:vault.read"]);

    /// <summary>Records the pane kinds this module registers, as its manifest declares them.</summary>
    public void Register(ClientModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var kind in builder.Manifest.PaneKinds)
        {
            builder.AddPaneKind(kind);
        }
    }

    public string Id => PaneKinds.VaultSearch;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        var vaultApi = context.Get<VaultApi>() ?? VaultApiFactory.Create(ClientServer.Current);
        registry.Register(PaneKinds.VaultSearch, descriptor => new VaultSearchPaneViewModel(descriptor, vaultApi));
        registry.RegisterView<VaultSearchPaneViewModel>(pane => new VaultSearchView(pane));
    }
}
