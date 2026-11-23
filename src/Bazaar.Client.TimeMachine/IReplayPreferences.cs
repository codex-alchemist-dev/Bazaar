namespace Bazaar.Client.TimeMachine;

/// <summary>
/// Where the viewer's override of <c>replay.default_view</c> is kept. Bazaar.Client.Abstractions has no preference or settings
/// store yet, so this module owns the seam; a head that gains a durable store implements it and hands it over with
/// <c>PaneModuleContext.With</c>. Without one, <see cref="InMemoryReplayPreferences"/> keeps the choice for the session only.
/// Implementations must not throw: a failing store means "no override".
/// </summary>
public interface IReplayPreferences
{
    /// <summary>The viewer's override, or null to follow the install default.</summary>
    ReplayViewMode? GetViewerOverride();

    /// <summary>Stores the override; null clears it.</summary>
    void SetViewerOverride(ReplayViewMode? mode);
}

/// <summary>Keeps the override in memory. Shared by every scrubber pane that receives the same instance.</summary>
public sealed class InMemoryReplayPreferences : IReplayPreferences
{
    private ReplayViewMode? _override;

    public ReplayViewMode? GetViewerOverride() => _override;

    public void SetViewerOverride(ReplayViewMode? mode) => _override = mode;
}
