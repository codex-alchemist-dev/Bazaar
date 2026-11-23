using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.ColdArchive.Panes;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.ColdArchive;

/// <summary>The cold-storage client module: the pane modules a head composes to manage cold storage (doc 25).</summary>
public static class ColdArchivePanes
{
    public static IReadOnlyList<IPaneModule> Modules { get; } = [new ColdArchivePaneModule()];
}
