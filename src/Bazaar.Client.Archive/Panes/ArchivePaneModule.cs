using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Archive.Panes;

/// <summary>Registers the read-only Archive book (<see cref="PaneKinds.Archive"/>).</summary>
public sealed class ArchivePaneModule : IPaneModule
{
    public string Id => PaneKinds.Archive;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(PaneKinds.Archive, descriptor => new ArchivePaneViewModel(descriptor));
        registry.RegisterView<ArchivePaneViewModel>(ArchivePaneViews.Book);
    }
}
