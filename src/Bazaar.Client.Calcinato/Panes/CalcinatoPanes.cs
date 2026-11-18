using Bazaar.Client.Core.Panes;

namespace Bazaar.Client.Calcinato.Panes;

/// <summary>The pane modules the Calcinato terminal module ships, in pager order.</summary>
public static class CalcinatoPanes
{
    /// <summary>The terminal pane module. A head adds these to its module list.</summary>
    public static IReadOnlyList<IPaneModule> Modules { get; } =
    [
        new TerminalPaneModule(),
    ];
}
