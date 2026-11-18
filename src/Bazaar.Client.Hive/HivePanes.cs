using Bazaar.Client.Core.Panes;
using Bazaar.Client.Hive.Panes;

namespace Bazaar.Client.Hive;

/// <summary>The Hive client module: the pane modules a head composes to ship the Hive pane (doc 25, WP10).</summary>
public static class HivePanes
{
    /// <summary>The Hive pane module, in registration order.</summary>
    public static IReadOnlyList<IPaneModule> Modules { get; } =
    [
        new HivePaneModule(),
    ];
}
