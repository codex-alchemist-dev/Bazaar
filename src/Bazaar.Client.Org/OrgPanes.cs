using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Org.Panes;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.Org;

/// <summary>The Org client module: the pane modules a head composes to ship the read-only Org chart (doc 24 section 2).</summary>
public static class OrgPanes
{
    public static IReadOnlyList<IPaneModule> Modules { get; } = [new OrgPaneModule()];
}
