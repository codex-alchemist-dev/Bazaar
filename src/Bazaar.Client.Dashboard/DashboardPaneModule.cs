using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Modules;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Dashboard;

/// <summary>Registers the start-screen dashboard (<see cref="PaneKinds.Dashboard"/>).</summary>
public sealed class DashboardPaneModule : IPaneModule, IClientModule
{
    /// <summary>Command id that opens the dashboard.</summary>
    public const string OpenCommandId = "nav.dashboard";

    /// <summary>The commands this module contributes, with their default mnemonics.</summary>
    public static IReadOnlyList<ClientCommandContribution> Commands { get; } =
    [
        new(OpenCommandId, "Dashboard", "dashboard"),
    ];

    /// <summary>The manifest the host checks at load (doc 41 section 8).</summary>
    public ModuleManifest Manifest { get; } = FirstPartyManifest.Create(typeof(DashboardPaneModule), PaneKinds.Dashboard, [PaneKinds.Dashboard], ["ui:pane", "wire:gc.history", "wire:kanban.read"], Commands.Select(c => c.Id).ToArray());

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

    public string Id => PaneKinds.Dashboard;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        PaneCommandBindings.Open(context, OpenCommandId, PaneKinds.Dashboard);
        registry.Register(PaneKinds.Dashboard, descriptor => new DashboardPaneViewModel(descriptor));
        registry.RegisterView<DashboardPaneViewModel>(DashboardPaneView.Create);
    }
}
