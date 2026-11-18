using Bazaar.Client.Core.Panes;

namespace Bazaar.Client.GitSolarSystem.Panes;

/// <summary>The Git Solar System client module: the pane modules a head composes to ship the solar-system view.</summary>
public static class GitSolarSystemPanes
{
    /// <summary>The solar-system pane modules, in registration order.</summary>
    public static IReadOnlyList<IPaneModule> Modules { get; } =
    [
        new GitSolarPaneModule(),
    ];
}
