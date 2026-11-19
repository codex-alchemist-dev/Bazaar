using System.Collections.Immutable;

namespace Bazaar.Server.Agents.LoopGuard;

/// <summary>
/// The result of one guard check. Ok has no <see cref="Kind"/>; a trip names the guard, a reason for the GC
/// notice and <c>guard_event</c>, and the agents involved (sorted ordinally, distinct).
/// </summary>
public sealed record GuardVerdict(GuardKind? Kind, string Reason, ImmutableArray<string> AgentIds)
{
    /// <summary>No guard tripped.</summary>
    public static GuardVerdict Ok { get; } = new(null, string.Empty, ImmutableArray<string>.Empty);

    /// <summary>True when a guard tripped.</summary>
    public bool Tripped => Kind is not null;

    /// <summary>Builds a trip verdict. Agent ids are de-duplicated and sorted so the verdict is deterministic.</summary>
    public static GuardVerdict Trip(GuardKind kind, string reason, IEnumerable<string> agentIds) =>
        new(kind, reason, agentIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray());
}
