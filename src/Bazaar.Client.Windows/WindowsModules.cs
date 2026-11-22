using Bazaar.Client.Constellation;
using Bazaar.Client.Dashboard;
using Bazaar.Client.Engines;
using Bazaar.Client.Gc;
using Bazaar.Client.GitGui;
using Bazaar.Client.GitSolarSystem.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.RyuseiFuda;
using Bazaar.Client.Vault;

namespace Bazaar.Client.Windows;

/// <summary>The pane modules this head ships, in pager order. One list, shared by the normal start and by <c>--self-check</c>.</summary>
public static class WindowsModules
{
    /// <summary>The Dashboard comes first, then Constellation, then the GC pane, then Vault, then the rest of RyuseiFuda, then Git Solar System (pager order).</summary>
    public static IPaneModule[] Create() =>
    [
        RyuseiFudaPanes.Board,
        new DashboardPaneModule(),
        new ConstellationPaneModule(),
        new GcPaneModule(),
        new VaultSearchPaneModule(),
        .. RyuseiFudaPanes.Modules,
        new EnginePaneModule(),
        .. GitSolarSystemPanes.Modules,
        .. GitGuiPanes.Modules,
    ];
}
