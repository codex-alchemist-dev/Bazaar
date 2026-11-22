using Bazaar.Server.WorkLife;

namespace Bazaar.Server.Presence.Abstractions;

/// <summary>One agent's state at restart: its availability, overlays and any open turn (docs/design/23, "Restart").</summary>
/// <param name="AgentId">The agent.</param>
/// <param name="Availability">The current availability (doc 09).</param>
/// <param name="Throttled">True while THROTTLED.</param>
/// <param name="Covered">True while a cover answers for the agent.</param>
/// <param name="OpenTurn">The turn event of an open turn (doc 06), or null when no turn is open.</param>
public sealed record PresenceSeed(
    Guid AgentId,
    PresenceAvailability Availability,
    bool Throttled = false,
    bool Covered = false,
    ActivityEvent? OpenTurn = null);

/// <summary>Reads availability and open turns for the restart rebuild. The work-life and agent runtime modules supply it.</summary>
public interface IPresenceSeedSource
{
    /// <summary>Loads one seed per agent.</summary>
    ValueTask<IReadOnlyList<PresenceSeed>> LoadAsync(CancellationToken ct);
}
