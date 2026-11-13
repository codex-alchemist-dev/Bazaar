namespace Bazaar.Client.Core.Panes;

/// <summary>Registers the group chat pane (<see cref="GcPaneViewModel.PaneKind"/>).</summary>
public sealed class GcPaneModule : IPaneModule
{
    public string Id => GcPaneViewModel.PaneKind;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(GcPaneViewModel.PaneKind, descriptor => new GcPaneViewModel(descriptor, context.Chat));
        registry.RegisterView<GcPaneViewModel>(PaneViews.GcPane);
    }
}
