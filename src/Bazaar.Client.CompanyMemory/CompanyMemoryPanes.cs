using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.CompanyMemory.Panes;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.CompanyMemory;

/// <summary>The company memory client module: the pane modules a head composes to show the decision chain (doc 25).</summary>
public static class CompanyMemoryPanes
{
    /// <summary>The company memory pane modules, in registration order.</summary>
    public static IReadOnlyList<IPaneModule> Modules { get; } =
    [
        new CompanyMemoryPaneModule(),
    ];
}
