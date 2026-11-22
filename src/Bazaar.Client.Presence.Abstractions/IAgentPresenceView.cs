using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Presence.Abstractions;

/// <summary>
/// What the client currently knows about each agent's presence (docs/design/23, "Client views"). The Observatory, the dashboard
/// tiles and the GC presence chip read it; the wire receiver writes it.
/// </summary>
public interface IAgentPresenceView
{
    /// <summary>The held row for <paramref name="agentId"/>, or null when none has arrived.</summary>
    WirePresenceRow? TryGetRow(Guid agentId);

    /// <summary>Every held row.</summary>
    IReadOnlyList<WirePresenceRow> All { get; }

    /// <summary>Raised, with the agent id, after the held row for that agent changed. May be raised on a timer or network thread.</summary>
    event Action<Guid>? Changed;
}
