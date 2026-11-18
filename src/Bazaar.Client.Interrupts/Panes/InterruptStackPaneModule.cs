using Bazaar.Client.Core.Panes;

namespace Bazaar.Client.Interrupts.Panes;

/// <summary>Registers the interrupt stack pane.</summary>
public sealed class InterruptStackPaneModule : IPaneModule
{
    public string Id => InterruptPanes.StackKind;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(InterruptPanes.StackKind, descriptor => new InterruptStackPaneViewModel(descriptor));
        registry.RegisterView<InterruptStackPaneViewModel>(InterruptStackPaneView.Create);
    }
}
