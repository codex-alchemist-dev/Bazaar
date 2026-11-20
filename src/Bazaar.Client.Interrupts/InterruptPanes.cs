using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Interrupts.Panes;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.Interrupts;

/// <summary>The Interrupts client module: the pane modules a head composes to show agent interrupt stacks (doc 24 section 3).</summary>
public static class InterruptPanes
{
    /// <summary>The interrupt stack pane kind (the value of <c>PaneKinds.InterruptStack</c> once Contracts carries it).</summary>
    public const string StackKind = Bazaar.Contracts.Panes.PaneKinds.InterruptStack;

    public static IReadOnlyList<IPaneModule> Modules { get; } = [new InterruptStackPaneModule()];
}
