using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.RyuseiFuda.Orrery;
using Bazaar.Client.Views;
using Bazaar.Contracts.Modules;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.RyuseiFuda.Panes;

/// <summary>
/// Registers the RyuseiFuda schedule pane (<see cref="PaneKinds.RyuseiSchedule"/>). The pause and resume actions come
/// from the head through <see cref="PaneModuleContext.With{T}"/>; with none, the pane's buttons stay disabled.
/// </summary>
public sealed class RyuseiSchedulePaneModule : IPaneModule, IClientModule
{
    /// <summary>The manifest the host checks at load (doc 41 section 8).</summary>
    public ModuleManifest Manifest { get; } = FirstPartyManifest.Create(typeof(RyuseiSchedulePaneModule), PaneKinds.RyuseiSchedule, [PaneKinds.RyuseiSchedule], ["ui:pane", "wire:kanban.read", "wire:kanban.write"]);

    /// <summary>Records the pane kinds this module registers, as its manifest declares them.</summary>
    public void Register(ClientModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var kind in builder.Manifest.PaneKinds)
        {
            builder.AddPaneKind(kind);
        }
    }

    public string Id => PaneKinds.RyuseiSchedule;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        var actions = context.Get<IRyuseiTriggerActions>();
        registry.Register(PaneKinds.RyuseiSchedule, descriptor => new RyuseiSchedulePaneViewModel(descriptor, actions));
        var head = context.Get<ViewContext>();
        registry.RegisterView<RyuseiSchedulePaneViewModel>(pane => head is null ? RyuseiFudaPaneViews.RyuseiSchedule(pane) : RyuseiViewPanes.Schedule(pane, head));
    }
}
