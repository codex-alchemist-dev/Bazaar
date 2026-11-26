using System.Text.Json;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Constellation.Abstractions;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Modules;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Constellation;

/// <summary>Registers the Constellation graph view (<see cref="PaneKinds.Constellation"/>).</summary>
public sealed class ConstellationPaneModule : IPaneModule, IClientModule
{
    /// <summary>The Constellation pane with an empty model: no graph loaded yet. The pane fetches its tiles itself.</summary>
    public static PaneDescriptor WindowPane { get; } = new(PaneKinds.Constellation, "Constellation", string.Empty, JsonElement.Parse("{}"));

    /// <summary>Command id that opens the Constellation.</summary>
    public const string OpenCommandId = "nav.constellation";

    /// <summary>The commands this module contributes, with their default mnemonics.</summary>
    public static IReadOnlyList<ClientCommandContribution> Commands { get; } =
    [
        new(OpenCommandId, "Constellation", "constellation"),
    ];

    /// <summary>The manifest the host checks at load (doc 41 section 8).</summary>
    public ModuleManifest Manifest { get; } = FirstPartyManifest.Create(typeof(ConstellationPaneModule), PaneKinds.Constellation, [PaneKinds.Constellation], ["ui:pane", "wire:constellation.read"], Commands.Select(c => c.Id).ToArray());

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

    public string Id => PaneKinds.Constellation;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        PaneCommandBindings.Open(context, OpenCommandId, PaneKinds.Constellation);
        var tiles = context.Get<IConstellationTileSource>();
        registry.Register(PaneKinds.Constellation, descriptor =>
            new ConstellationPaneViewModel(descriptor, tiles, ConstellationPaneViewModel.DefaultNodeCap));
        registry.RegisterView<ConstellationPaneViewModel>(pane => new ConstellationPaneView(pane));
    }
}
