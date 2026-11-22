using Bazaar.Client.Analytics;
using Bazaar.Client.Calcinato.Panes;
using Bazaar.Client.Calendar;
using Bazaar.Client.ColdArchive;
using Bazaar.Client.Constellation;
using Bazaar.Client.Dashboard;
using Bazaar.Client.Engines;
using Bazaar.Client.Gc;
using Bazaar.Client.GitGui;
using Bazaar.Client.GitSolarSystem.Panes;
using Bazaar.Client.Interrupts;
using Bazaar.Client.Org;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.RyuseiFuda;
using Bazaar.Client.Vault;

namespace Bazaar.Client.Linux;

/// <summary>
/// The pane modules this head ships, in pager order (doc 30 section 1, mirroring the Windows head). Only the cross-platform
/// Avalonia modules are here. GitGui is composed without merge approval, which needs a device-bound signer (the merge card says so).
/// </summary>
public static class LinuxModules
{
    /// <summary>The Dashboard comes first, then Constellation, then the GC pane, then Vault, then the rest of RyuseiFuda.</summary>
    public static IPaneModule[] Create() =>
    [
        RyuseiFudaPanes.Board,
        new DashboardPaneModule(),
        new ConstellationPaneModule(),
        new GcPaneModule(),
        new VaultSearchPaneModule(),
        .. RyuseiFudaPanes.Modules,
        .. OrgPanes.Modules,
        .. CalendarPanes.Modules,
        .. InterruptPanes.Modules,
        .. ColdArchivePanes.Modules,
        new EnginePaneModule(),
        .. GitSolarSystemPanes.Modules,
        .. GitGuiPanes.Modules,
        new AgentAnalyticsPaneModule(),
        // The terminal pane comes last in pager order (doc 55, section 7).
        .. CalcinatoPanes.Modules,
    ];
}
