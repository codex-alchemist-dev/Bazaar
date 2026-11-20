using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Archive.Panes;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.Archive;

/// <summary>The Archive client module: the pane modules a head composes to ship the Archive book (doc 25).</summary>
public static class ArchivePanes
{
    /// <summary>The Archive pane modules, in registration order.</summary>
    public static IReadOnlyList<IPaneModule> Modules { get; } =
    [
        new ArchivePaneModule(),
    ];
}
