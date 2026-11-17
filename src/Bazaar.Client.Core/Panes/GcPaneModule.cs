using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Core.Chat;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.Core.Panes;

/// <summary>Registers the group chat pane (<see cref="GcPaneViewModel.PaneKind"/>).</summary>
public sealed class GcPaneModule : IPaneModule
{
    public string Id => GcPaneViewModel.PaneKind;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(GcPaneViewModel.PaneKind, descriptor => new GcPaneViewModel(descriptor, context.Get<ChatViewModel>()));
        registry.RegisterView<GcPaneViewModel>(PaneViews.GcPane);
    }
}
