using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Health.Panes;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.Health;

/// <summary>The Health client module: the pane modules a head composes to ship the health and analytics panes (doc 25).</summary>
public static class HealthPanes
{
    /// <summary>The project health block, then the analytics trends, in registration order.</summary>
    public static IReadOnlyList<IPaneModule> Modules { get; } =
    [
        new HealthPaneModule(),
        new AnalyticsPaneModule(),
    ];
}
