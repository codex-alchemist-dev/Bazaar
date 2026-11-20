using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.Twin.Panes;

namespace Bazaar.Client.Twin;

/// <summary>The Twin client module: the pane modules a head composes to show the Digital Twin (doc 25, Digital Twin row).</summary>
public static class TwinPanes
{
    /// <summary>The Digital Twin project card. Heads compose it with the rest of the first-party panes.</summary>
    public static IReadOnlyList<IPaneModule> Modules { get; } =
    [
        new TwinPaneModule(),
    ];
}
