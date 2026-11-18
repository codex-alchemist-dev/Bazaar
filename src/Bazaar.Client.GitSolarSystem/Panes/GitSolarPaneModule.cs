using Bazaar.Client.Core.Panes;
using Bazaar.Client.GitSolarSystem.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.GitSolarSystem.Panes;

/// <summary>Registers the Git Solar System pane (<see cref="PaneKinds.GitSolarSystem"/>): its view model and its view.</summary>
public sealed class GitSolarPaneModule : IPaneModule
{
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
