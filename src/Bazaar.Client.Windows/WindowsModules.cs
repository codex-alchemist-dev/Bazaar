using Bazaar.Client.Analytics;
using Bazaar.Client.Calcinato.Panes;
using Bazaar.Client.Calendar;
using Bazaar.Client.Constellation;
using Bazaar.Client.Dashboard;
using Bazaar.Client.Engines;
using Bazaar.Client.Gc;
using Bazaar.Client.GitGui;
using Bazaar.Client.GitSolarSystem.Panes;
using Bazaar.Client.ColdArchive;
using Bazaar.Client.Interrupts;
using Bazaar.Client.Org;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.RyuseiFuda;
using Bazaar.Client.Settings;
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
        .. OrgPanes.Modules,
        .. CalendarPanes.Modules,
        .. InterruptPanes.Modules,
        .. ColdArchivePanes.Modules,
        new EnginePaneModule(),
        .. GitSolarSystemPanes.Modules,
        .. GitGuiPanes.Modules,
        .. CommunityPanes.Modules(),
        new AgentAnalyticsPaneModule(),
        new SettingsPaneModule(),
        // The terminal pane comes last in pager order (doc 55, section 7).
        .. CalcinatoPanes.Modules,
    ];
}
