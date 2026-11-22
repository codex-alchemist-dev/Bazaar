using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Modules;

namespace Bazaar.Client.Interrupts.Panes;

/// <summary>Registers the interrupt stack pane.</summary>
public sealed class InterruptStackPaneModule : IPaneModule, IClientModule
{
    /// <summary>The manifest the host checks at load (doc 41 section 8).</summary>
    public ModuleManifest Manifest { get; } = FirstPartyManifest.Create(typeof(InterruptStackPaneModule), InterruptPanes.StackKind, [InterruptPanes.StackKind], ["ui:pane"]);

    /// <summary>Records the pane kinds this module registers, as its manifest declares them.</summary>
    public void Register(ClientModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var kind in builder.Manifest.PaneKinds)
        {
            builder.AddPaneKind(kind);
        }
    }

    public string Id => InterruptPanes.StackKind;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(InterruptPanes.StackKind, descriptor => new InterruptStackPaneViewModel(descriptor));
        registry.RegisterView<InterruptStackPaneViewModel>(InterruptStackPaneView.Create);
    }
}
