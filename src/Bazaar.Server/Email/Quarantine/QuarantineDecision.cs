using System.Collections.Immutable;

namespace Bazaar.Server.Email.Quarantine;

/// <summary>What to do with an inbound message.</summary>
public enum QuarantineAction
{
    /// <summary>The message passed every screen and may reach the agent's inbox.</summary>
    Deliver,

    /// <summary>The message is held outside all agent context.</summary>
    Quarantine,
}

/// <summary>The screening result for one inbound message.</summary>
/// <param name="Action">Deliver when no reason applies, otherwise Quarantine.</param>
/// <param name="Reasons">Every reason that applied, distinct and in enum order. Empty when delivered.</param>
public sealed record QuarantineDecision(QuarantineAction Action, ImmutableArray<QuarantineReason> Reasons);
