using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.GitSolarSystem.Abstractions;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Modules;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.GitSolarSystem.Panes;

/// <summary>Registers the Git Solar System pane (<see cref="PaneKinds.GitSolarSystem"/>): its view model and its view.</summary>
public sealed class GitSolarPaneModule : IPaneModule, IClientModule
{
    /// <summary>The manifest the host checks at load (doc 41 section 8). The feed is the module's own HTTP route, so it asks for the pane only.</summary>
    public ModuleManifest Manifest { get; } = FirstPartyManifest.Create(typeof(GitSolarPaneModule), PaneKinds.GitSolarSystem, [PaneKinds.GitSolarSystem], ["ui:pane"]);

    /// <summary>Records the pane kinds this module registers, as its manifest declares them.</summary>
    public void Register(ClientModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var kind in builder.Manifest.PaneKinds)
        {
            builder.AddPaneKind(kind);
        }
    }

    public string Id => PaneKinds.GitSolarSystem;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(context);

        var source = context.Get<IGitSolarSource>();
        registry.Register(PaneKinds.GitSolarSystem, descriptor => new GitSolarPaneViewModel(descriptor, source));
        registry.RegisterView<GitSolarPaneViewModel>(pane => new GitSolarPaneView(pane));
    }
}
