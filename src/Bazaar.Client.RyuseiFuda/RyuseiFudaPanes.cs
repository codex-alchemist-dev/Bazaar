using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.RyuseiFuda.Panes;

namespace Bazaar.Client.RyuseiFuda;

/// <summary>The RyuseiFuda client module: the pane modules a head composes to ship RyuseiFuda (doc 40, step 7).</summary>
public static class RyuseiFudaPanes
{
    /// <summary>The Workday board. Heads list it first, so it opens the phone pager (doc 29).</summary>
    public static IPaneModule Board { get; } = new KanbanPaneModule();

    /// <summary>The Orrery schedule, the Roadmap and the Workday team schedule, in registration order.</summary>
    public static IReadOnlyList<IPaneModule> Modules { get; } =
    [
        new RyuseiSchedulePaneModule(),
        new RoadmapPaneModule(),
        new WorkdayPaneModule(),
    ];
}
