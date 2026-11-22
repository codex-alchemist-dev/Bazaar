using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.Views;
using Bazaar.Contracts.Modules;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.RyuseiFuda.Panes;

/// <summary>Registers the RyuseiFuda Workday pane (<see cref="PaneKinds.Workday"/>).</summary>
public sealed class WorkdayPaneModule : IPaneModule, IClientModule
{
    /// <summary>The manifest the host checks at load (doc 41 section 8).</summary>
    public ModuleManifest Manifest { get; } = FirstPartyManifest.Create(typeof(WorkdayPaneModule), PaneKinds.Workday, [PaneKinds.Workday], ["ui:pane", "wire:kanban.read"]);

    /// <summary>Records the pane kinds this module registers, as its manifest declares them.</summary>
    public void Register(ClientModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var kind in builder.Manifest.PaneKinds)
        {
            builder.AddPaneKind(kind);
        }
    }

    public string Id => PaneKinds.Workday;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(PaneKinds.Workday, descriptor => new WorkdayPaneViewModel(descriptor));
        var head = context.Get<ViewContext>();
        registry.RegisterView<WorkdayPaneViewModel>(pane => head is null ? RyuseiFudaPaneViews.Workday(pane) : RyuseiViewPanes.Workday(pane, head));
    }
}
