using Bazaar.Server.WorkLife;

namespace Bazaar.Server.Presence.Abstractions;

/// <summary>
/// One agent's presence (docs/design/23, <c>agent_activity</c>). <see cref="Revision"/> is monotonic per agent and orders the
/// deltas clients receive.
/// </summary>
/// <param name="AgentId">The agent.</param>
/// <param name="Revision">Per-agent monotonic revision.</param>
/// <param name="Availability">Availability copied from doc 09.</param>
/// <param name="Overlay">Overlays shown beside the label.</param>
/// <param name="Presence">The presence label (DECISIONS-LOG C43), or <see cref="UnknownLabel"/>.</param>
/// <param name="ActivityKind">The enumerated activity kind.</param>
/// <param name="GcId">The GC of the current turn.</param>
/// <param name="TurnId">The current turn.</param>
/// <param name="Label">Model-written label, at most <see cref="MaxLabelLength"/> characters, or null.</param>
/// <param name="Since">When the label or activity last changed.</param>
/// <param name="UpdatedAt">When the row was last written.</param>
public sealed record PresenceRow(
    Guid AgentId,
    long Revision,
    PresenceAvailability Availability,
    PresenceOverlay Overlay,
    string Presence,
    PresenceActivityKind ActivityKind,
    Guid? GcId,
    Guid? TurnId,
    string? Label,
    DateTimeOffset Since,
    DateTimeOffset UpdatedAt)
{
    /// <summary>The longest <see cref="Label"/> (doc 23, <c>label varchar(80)</c>).</summary>
    public const int MaxLabelLength = 80;

    /// <summary>The label of a row with no turn event for too long (doc 23, "Restart").</summary>
    public const string UnknownLabel = "unknown";
}
