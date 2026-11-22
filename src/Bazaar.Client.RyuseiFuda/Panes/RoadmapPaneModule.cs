using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Modules;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.RyuseiFuda.Panes;

/// <summary>Registers the RyuseiFuda objective roadmap pane (<see cref="PaneKinds.Roadmap"/>).</summary>
public sealed class RoadmapPaneModule : IPaneModule, IClientModule
{
    /// <summary>Command id that opens the RyuseiFuda Roadmap.</summary>
    public const string OpenCommandId = "nav.ryusei.roadmap";

    /// <summary>The commands this module contributes, with their default mnemonics.</summary>
    public static IReadOnlyList<ClientCommandContribution> Commands { get; } =
    [
        new(OpenCommandId, "RyuseiFuda Roadmap", "nrfdrm"),
    ];

    /// <summary>The manifest the host checks at load (doc 41 section 8).</summary>
    public ModuleManifest Manifest { get; } = FirstPartyManifest.Create(typeof(RoadmapPaneModule), PaneKinds.Roadmap, [PaneKinds.Roadmap], ["ui:pane", "wire:kanban.read"], Commands.Select(c => c.Id).ToArray());

    /// <summary>Records the pane kinds this module registers, as its manifest declares them.</summary>
    public void Register(ClientModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var kind in builder.Manifest.PaneKinds)
        {
            builder.AddPaneKind(kind);
        }

        foreach (var command in Commands)
        {
            builder.AddCommand(command);
        }
    }

    public string Id => PaneKinds.Roadmap;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        PaneCommandBindings.Open(context, OpenCommandId, PaneKinds.Roadmap);
        registry.Register(PaneKinds.Roadmap, descriptor => new RoadmapPaneViewModel(descriptor));
        registry.RegisterView<RoadmapPaneViewModel>(RyuseiFudaPaneViews.Roadmap);
    }
}
