using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Modules;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.ColdArchive.Panes;

/// <summary>Registers the owner-only cold-storage pane (<see cref="PaneKinds.ColdArchive"/>). The head hands it an <see cref="IColdArchiveClient"/> through the pane module context.</summary>
public sealed class ColdArchivePaneModule : IPaneModule, IClientModule
{
    /// <summary>The manifest the host checks at load (doc 41 section 8). The routes are the server's own, so it asks for the pane only.</summary>
    public ModuleManifest Manifest { get; } = FirstPartyManifest.Create(typeof(ColdArchivePaneModule), PaneKinds.ColdArchive, [PaneKinds.ColdArchive], ["ui:pane"]);

    public void Register(ClientModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var kind in builder.Manifest.PaneKinds)
        {
            builder.AddPaneKind(kind);
        }
    }

    public string Id => PaneKinds.ColdArchive;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(context);

        var client = context.Get<IColdArchiveClient>();
        registry.Register(PaneKinds.ColdArchive, descriptor => new ColdArchivePaneViewModel(descriptor, client));
        registry.RegisterView<ColdArchivePaneViewModel>(ColdArchivePaneView.Create);
    }
}
